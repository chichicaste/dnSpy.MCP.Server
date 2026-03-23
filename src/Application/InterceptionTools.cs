/*
    Copyright (C) 2026 @chichicaste

    This file is part of dnSpy MCP Server module.

    dnSpy MCP Server is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy MCP Server is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy MCP Server.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.DotNet.Breakpoints.Code;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Metadata;
using dnSpy.MCP.Server.Contracts;

namespace dnSpy.MCP.Server.Application {
	[Export(typeof(InterceptionTools))]
	public sealed class InterceptionTools {
		readonly Lazy<DbgDotNetBreakpointFactory> breakpointFactory;
		readonly IDocumentTreeView documentTreeView;

		static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions {
			WriteIndented = true,
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
		};
		static readonly ConcurrentDictionary<string, InterceptorSession> Sessions = new ConcurrentDictionary<string, InterceptorSession>(StringComparer.OrdinalIgnoreCase);

		[ImportingConstructor]
		public InterceptionTools(Lazy<DbgDotNetBreakpointFactory> breakpointFactory, IDocumentTreeView documentTreeView) {
			this.breakpointFactory = breakpointFactory;
			this.documentTreeView = documentTreeView;
		}

		public CallToolResult TraceMethod(Dictionary<string, object>? arguments) =>
			CreateInterceptor(arguments, InterceptionAction.Trace, "trace_method");

		public CallToolResult HookFunction(Dictionary<string, object>? arguments) =>
			CreateInterceptor(arguments, ParseAction(arguments), "hook_function");

		public CallToolResult ListActiveInterceptors(Dictionary<string, object>? arguments) {
			bool includeInactive = GetBoolean(arguments, "include_inactive", false);
			var sessions = Sessions.Values
				.Where(s => includeInactive || s.IsActive)
				.OrderByDescending(s => s.CreatedUtc)
				.Select(ToSessionSummary)
				.ToList();

			return JsonResult(new {
				Count = sessions.Count,
				Interceptors = sessions
			});
		}

		public CallToolResult GetInterceptorLog(Dictionary<string, object>? arguments) {
			string sessionId = RequireString(arguments, "session_id");
			if (!Sessions.TryGetValue(sessionId, out var session))
				throw new ArgumentException($"Interceptor session not found: {sessionId}");

			InterceptorHitRecord[] hits;
			lock (session.SyncRoot)
				hits = session.Hits.ToArray();

			return JsonResult(new {
				Session = ToSessionSummary(session),
				LogCount = hits.Length,
				Log = hits
			});
		}

		public CallToolResult RemoveInterceptor(Dictionary<string, object>? arguments) {
			string sessionId = RequireString(arguments, "session_id");
			if (!Sessions.TryGetValue(sessionId, out var session))
				throw new ArgumentException($"Interceptor session not found: {sessionId}");

			DeactivateSession(session, "removed");
			return JsonResult(new {
				Removed = true,
				Session = ToSessionSummary(session)
			});
		}

		CallToolResult CreateInterceptor(Dictionary<string, object>? arguments, InterceptionAction defaultAction, string toolName) {
			var resolved = ResolveMethod(arguments);
			uint ilOffset = GetUInt32(arguments, "il_offset") ?? 0;
			int? maxCalls = GetPositiveInt(arguments, "max_calls");
			int maxLogEntries = GetPositiveInt(arguments, "max_log_entries") ?? 256;
			if (maxLogEntries < 1 || maxLogEntries > 4096)
				throw new ArgumentException("max_log_entries must be in range 1..4096");

			var action = defaultAction;
			string sessionId = Guid.NewGuid().ToString("N");
			var labels = new ReadOnlyCollection<string>(new[] {
				"mcp-interceptor",
				"mcp-interceptor:" + sessionId,
				"mcp-interceptor-action:" + action.ToString().ToLowerInvariant()
			});

			var settings = new DbgCodeBreakpointSettings {
				IsEnabled = true,
				Labels = labels,
			};

			string? condition = GetString(arguments, "condition");
			if (!string.IsNullOrWhiteSpace(condition)) {
				string nonNullCondition = condition!;
				var parameterNames = resolved.Method.Parameters
					.Where(p => p.IsNormalMethodParameter)
					.OrderBy(p => p.MethodSigIndex)
					.Select(p => p.Name)
					.ToList();
				var localIndexes = resolved.Method.Body?.Variables.Select(v => (int) v.Index).ToHashSet() ?? new HashSet<int>();
				var aliasContext = DebuggerExpressionAliasContext.Create(resolved.Method);
				var rewrite = DebuggerExpressionAliasHelper.Rewrite(nonNullCondition, parameterNames, localIndexes, aliasContext);
				if (rewrite.Error != null)
					throw new ArgumentException(rewrite.Error);
				settings.Condition = new DbgCodeBreakpointCondition(DbgCodeBreakpointConditionKind.IsTrue, rewrite.RewrittenExpression);
			}

			var breakpoint = breakpointFactory.Value.Create(resolved.ModuleId, resolved.Method.MDToken.Raw, ilOffset, settings);
			if (breakpoint == null) {
				var existing = breakpointFactory.Value.TryGetBreakpoint(resolved.ModuleId, resolved.Method.MDToken.Raw, ilOffset);
				throw new InvalidOperationException(existing != null
					? $"A breakpoint already exists at {resolved.Method.FullName} +IL_{ilOffset:X4}. Remove it or reuse a different offset."
					: $"Failed to create interceptor for {resolved.Method.FullName} +IL_{ilOffset:X4}.");
			}

			var session = new InterceptorSession(sessionId, toolName, action, resolved, breakpoint, ilOffset, maxCalls, maxLogEntries, condition);
			EventHandler<DbgBreakpointHitCheckEventArgs> hitCheckHandler = (s, e) => OnBreakpointHitCheck(session, e);
			session.HitCheckHandler = hitCheckHandler;
			session.Breakpoint.Closed += Breakpoint_Closed;
			session.Breakpoint.HitCheck += hitCheckHandler;

			if (!Sessions.TryAdd(session.Id, session)) {
				session.Breakpoint.HitCheck -= hitCheckHandler;
				session.Breakpoint.Closed -= Breakpoint_Closed;
				session.Breakpoint.Remove();
				throw new InvalidOperationException($"Interceptor session collision: {session.Id}");
			}

			return JsonResult(new {
				Created = true,
				Session = ToSessionSummary(session),
				Note = action == InterceptionAction.Break
					? "Persistent break interceptor armed. It will pause execution on each hit until removed or max_calls is reached."
					: "Persistent trace interceptor armed. It will keep logging hits without pausing execution until removed or max_calls is reached."
			});
		}

		void OnBreakpointHitCheck(InterceptorSession session, DbgBreakpointHitCheckEventArgs e) {
			bool shouldRemove = false;
			lock (session.SyncRoot) {
				if (!session.IsActive)
					return;

				session.HitCount++;
				session.AppendHit(CreateHitRecord(session, e, session.HitCount));
				e.Pause = session.Action == InterceptionAction.Break;

				if (session.MaxCalls.HasValue && session.HitCount >= session.MaxCalls.Value) {
					session.IsActive = false;
					session.DeactivatedReason = "max_calls reached";
					shouldRemove = true;
				}
			}

			if (shouldRemove)
				DeactivateSession(session, "max_calls reached");
		}

		void Breakpoint_Closed(object? sender, EventArgs e) {
			var closedBreakpoint = sender as DbgCodeBreakpoint;
			if (closedBreakpoint == null)
				return;

			foreach (var session in Sessions.Values.Where(s => ReferenceEquals(s.Breakpoint, closedBreakpoint))) {
				lock (session.SyncRoot) {
					if (!session.IsActive)
						continue;
					session.IsActive = false;
					session.DeactivatedReason = session.DeactivatedReason ?? "breakpoint closed";
				}
			}
		}

		void DeactivateSession(InterceptorSession session, string reason) {
			lock (session.SyncRoot) {
				if (!session.IsActive && (session.Breakpoint.IsClosed || session.DeactivatedReason != null))
					return;
				session.IsActive = false;
				session.DeactivatedReason = reason;
			}

			try {
				if (session.HitCheckHandler != null)
					session.Breakpoint.HitCheck -= session.HitCheckHandler;
				session.Breakpoint.Closed -= Breakpoint_Closed;
				if (!session.Breakpoint.IsClosed)
					session.Breakpoint.Remove();
			}
			catch {
			}
		}

		static object ToSessionSummary(InterceptorSession session) {
			lock (session.SyncRoot) {
				return new {
					session.Id,
					session.ToolName,
					Action = session.Action.ToString().ToLowerInvariant(),
					session.IsActive,
					session.DeactivatedReason,
					BreakpointId = session.Breakpoint.Id,
					session.AssemblyName,
					session.TypeFullName,
					session.MethodName,
					session.Token,
					ILOffset = $"0x{session.ILOffset:X4}",
					session.MaxCalls,
					session.HitCount,
					LogCount = session.Hits.Count,
					CreatedUtc = session.CreatedUtc
				};
			}
		}

		static InterceptorHitRecord CreateHitRecord(InterceptorSession session, DbgBreakpointHitCheckEventArgs e, int sequence) {
			DbgStackFrame? frame = null;
			try {
				frame = e.Thread.GetTopStackFrame();
			}
			catch {
			}

			return new InterceptorHitRecord {
				Sequence = sequence,
				TimestampUtc = DateTimeOffset.UtcNow,
				ProcessId = e.Thread.Process.Id,
				ProcessName = e.Thread.Process.Name,
				ThreadId = $"0x{e.Thread.Id:X}",
				ManagedThreadId = e.Thread.ManagedId.HasValue ? $"0x{e.Thread.ManagedId.Value:X}" : null,
				PauseRequested = session.Action == InterceptionAction.Break,
				FunctionToken = frame?.HasFunctionToken == true ? $"0x{frame.FunctionToken:X8}" : null,
				FunctionOffset = frame != null ? $"0x{frame.FunctionOffset:X4}" : null,
				RuntimeName = e.Thread.Runtime.Name,
			};
		}

		ResolvedMethod ResolveMethod(Dictionary<string, object>? arguments) {
			string assemblyName = RequireString(arguments, "assembly_name");
			string? filePath = GetString(arguments, "file_path");
			var assembly = FindAssemblyByName(assemblyName, filePath)
				?? throw new ArgumentException($"Assembly not found: {assemblyName}");

			MethodDef? method;
			TypeDef? type = null;
			var token = GetUInt32(arguments, "token");
			if (token.HasValue) {
				method = assembly.Modules
					.SelectMany(m => GetAllTypesRecursive(m.Types))
					.SelectMany(t => t.Methods)
					.FirstOrDefault(m => m.MDToken.Raw == token.Value);
				if (method == null)
					throw new ArgumentException($"Method token 0x{token.Value:X8} was not found in assembly '{assemblyName}'.");
				type = method.DeclaringType;
			}
			else {
				string typeFullName = RequireString(arguments, "type_full_name");
				string methodName = RequireString(arguments, "method_name");
				type = FindTypeInAssembly(assembly, typeFullName)
					?? throw new ArgumentException($"Type not found: {typeFullName}");
				method = type.Methods.FirstOrDefault(m => m.Name.String == methodName)
					?? throw new ArgumentException($"Method not found: {methodName}");
			}

			if (method == null || type == null)
				throw new ArgumentException("Could not resolve target method.");
			if (!method.HasBody)
				throw new ArgumentException($"Method '{method.FullName}' does not have a CIL body.");

			return new ResolvedMethod(
				assembly.Name.String,
				type.FullName,
				method.Name.String,
				$"0x{method.MDToken.Raw:X8}",
				method,
				ModuleId.CreateFromFile(method.Module));
		}

		AssemblyDef? FindAssemblyByName(string name, string? filePath = null) {
			if (!string.IsNullOrEmpty(filePath)) {
				string normalized = filePath!.Replace('/', '\\');
				var byPath = documentTreeView.GetAllModuleNodes()
					.FirstOrDefault(m => (m.Document?.Filename ?? string.Empty).Replace('/', '\\')
						.Equals(normalized, StringComparison.OrdinalIgnoreCase));
				if (byPath?.Document?.AssemblyDef != null)
					return byPath.Document.AssemblyDef;
			}

			return documentTreeView.GetAllModuleNodes()
				.Select(m => m.Document?.AssemblyDef)
				.FirstOrDefault(a => a != null && a.Name.String.Equals(name, StringComparison.OrdinalIgnoreCase));
		}

		static TypeDef? FindTypeInAssembly(AssemblyDef assembly, string fullName) =>
			assembly.Modules
				.SelectMany(m => GetAllTypesRecursive(m.Types))
				.FirstOrDefault(t => t.FullName.Equals(fullName, StringComparison.Ordinal));

		static IEnumerable<TypeDef> GetAllTypesRecursive(IEnumerable<TypeDef> types) {
			foreach (var type in types) {
				yield return type;
				foreach (var nested in GetAllTypesRecursive(type.NestedTypes))
					yield return nested;
			}
		}

		static string RequireString(Dictionary<string, object>? arguments, string name) {
			var value = GetString(arguments, name);
			if (string.IsNullOrWhiteSpace(value))
				throw new ArgumentException($"{name} is required");
			return value!;
		}

		static string? GetString(Dictionary<string, object>? arguments, string name) {
			if (arguments == null || !arguments.TryGetValue(name, out var value) || value == null)
				return null;
			if (value is JsonElement elem) {
				if (elem.ValueKind == JsonValueKind.String)
					return elem.GetString();
				return elem.ToString();
			}
			return value.ToString();
		}

		static bool GetBoolean(Dictionary<string, object>? arguments, string name, bool defaultValue) {
			if (arguments == null || !arguments.TryGetValue(name, out var value) || value == null)
				return defaultValue;
			if (value is JsonElement elem) {
				if (elem.ValueKind == JsonValueKind.True || elem.ValueKind == JsonValueKind.False)
					return elem.GetBoolean();
				if (elem.ValueKind == JsonValueKind.String && bool.TryParse(elem.GetString(), out var parsed))
					return parsed;
				return defaultValue;
			}
			return bool.TryParse(value.ToString(), out var result) ? result : defaultValue;
		}

		static int? GetPositiveInt(Dictionary<string, object>? arguments, string name) {
			var value = GetUInt32(arguments, name);
			if (!value.HasValue)
				return null;
			if (value.Value == 0 || value.Value > int.MaxValue)
				throw new ArgumentException($"{name} must be a positive integer");
			return (int) value.Value;
		}

		static uint? GetUInt32(Dictionary<string, object>? arguments, string name) {
			if (arguments == null || !arguments.TryGetValue(name, out var value) || value == null)
				return null;
			if (value is JsonElement elem) {
				if (elem.ValueKind == JsonValueKind.Number && elem.TryGetUInt32(out var number))
					return number;
				if (elem.ValueKind == JsonValueKind.String)
					return ParseUInt32(elem.GetString());
				return null;
			}
			return ParseUInt32(value.ToString());
		}

		static uint? ParseUInt32(string? value) {
			if (string.IsNullOrWhiteSpace(value))
				return null;
			string text = value!.Trim();
			if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				return Convert.ToUInt32(text.Substring(2), 16);
			return Convert.ToUInt32(text, 10);
		}

		static InterceptionAction ParseAction(Dictionary<string, object>? arguments) {
			string value = (GetString(arguments, "action") ?? "break").Trim().ToLowerInvariant();
			return value switch {
				"break" => InterceptionAction.Break,
				"log" => InterceptionAction.Trace,
				"trace" => InterceptionAction.Trace,
				"count" => InterceptionAction.Count,
				"modify_return" => throw new ArgumentException("hook_function action 'modify_return' is not implemented yet. Supported actions: break, log, count."),
				_ => throw new ArgumentException($"Unsupported hook action: {value}. Supported actions: break, log, count.")
			};
		}

		static CallToolResult JsonResult(object value) => new CallToolResult {
			Content = new List<ToolContent> {
				new ToolContent { Text = JsonSerializer.Serialize(value, JsonOptions) }
			}
		};

		enum InterceptionAction {
			Trace,
			Count,
			Break,
		}

		sealed class ResolvedMethod {
			public ResolvedMethod(string assemblyName, string typeFullName, string methodName, string token, MethodDef method, ModuleId moduleId) {
				AssemblyName = assemblyName;
				TypeFullName = typeFullName;
				MethodName = methodName;
				Token = token;
				Method = method;
				ModuleId = moduleId;
			}

			public string AssemblyName { get; }
			public string TypeFullName { get; }
			public string MethodName { get; }
			public string Token { get; }
			public MethodDef Method { get; }
			public ModuleId ModuleId { get; }
		}

		sealed class InterceptorSession {
			public InterceptorSession(string id, string toolName, InterceptionAction action, ResolvedMethod method, DbgCodeBreakpoint breakpoint, uint ilOffset, int? maxCalls, int maxLogEntries, string? condition) {
				Id = id;
				ToolName = toolName;
				Action = action;
				AssemblyName = method.AssemblyName;
				TypeFullName = method.TypeFullName;
				MethodName = method.MethodName;
				Token = method.Token;
				Breakpoint = breakpoint;
				ILOffset = ilOffset;
				MaxCalls = maxCalls;
				MaxLogEntries = maxLogEntries;
				Condition = condition;
				CreatedUtc = DateTimeOffset.UtcNow;
			}

			public object SyncRoot { get; } = new object();
			public string Id { get; }
			public string ToolName { get; }
			public InterceptionAction Action { get; }
			public string AssemblyName { get; }
			public string TypeFullName { get; }
			public string MethodName { get; }
			public string Token { get; }
			public DbgCodeBreakpoint Breakpoint { get; }
			public uint ILOffset { get; }
			public int? MaxCalls { get; }
			public int MaxLogEntries { get; }
			public string? Condition { get; }
			public DateTimeOffset CreatedUtc { get; }
			public bool IsActive { get; set; } = true;
			public string? DeactivatedReason { get; set; }
			public int HitCount { get; set; }
			public EventHandler<DbgBreakpointHitCheckEventArgs>? HitCheckHandler { get; set; }
			public List<InterceptorHitRecord> Hits { get; } = new List<InterceptorHitRecord>();

			public void AppendHit(InterceptorHitRecord hit) {
				if (Action == InterceptionAction.Count && Hits.Count == 0)
					Hits.Add(hit);
				else
					Hits.Add(hit);
				if (Hits.Count > MaxLogEntries)
					Hits.RemoveAt(0);
			}
		}

		sealed class InterceptorHitRecord {
			public int Sequence { get; set; }
			public DateTimeOffset TimestampUtc { get; set; }
			public int ProcessId { get; set; }
			public string ProcessName { get; set; } = string.Empty;
			public string ThreadId { get; set; } = string.Empty;
			public string? ManagedThreadId { get; set; }
			public bool PauseRequested { get; set; }
			public string? FunctionToken { get; set; }
			public string? FunctionOffset { get; set; }
			public string? RuntimeName { get; set; }
		}
	}
}
