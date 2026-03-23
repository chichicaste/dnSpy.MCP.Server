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
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.MCP.Server.Contracts;
using dnSpy.MCP.Server.Helper;

namespace dnSpy.MCP.Server.Application {
	/// <summary>
	/// Runtime memory inspection tools: read local variables and parameters from
	/// a paused debug session frame using the IDbgDotNetRuntime evaluation API.
	/// </summary>
	[Export(typeof(MemoryInspectTools))]
	public sealed class MemoryInspectTools {
		readonly Lazy<DbgManager> dbgManager;
		readonly Lazy<DbgLanguageService> languageService;
		readonly IDocumentTreeView documentTreeView;

		[ImportingConstructor]
		public MemoryInspectTools(
			Lazy<DbgManager> dbgManager,
			Lazy<DbgLanguageService> languageService,
			IDocumentTreeView documentTreeView) {
			this.dbgManager = dbgManager;
			this.languageService = languageService;
			this.documentTreeView = documentTreeView;
		}

		// ── get_local_variables ──────────────────────────────────────────────────

		/// <summary>
		/// Reads all local variables and parameters from the current (or specified) stack frame.
		/// Requires the debugger to be paused (breakpoint/step).
		/// Arguments: frame_index (int, optional, default=0), process_id (int, optional)
		/// </summary>
		public CallToolResult GetLocalVariables(Dictionary<string, object>? arguments) {
			var mgr = dbgManager.Value;
			if (!mgr.IsDebugging)
				throw new InvalidOperationException("Debugger is not active. Start a debug session first.");

			int frameIndex = 0;
			if (arguments != null && arguments.TryGetValue("frame_index", out var fiObj)) {
				if (fiObj is JsonElement fiElem) fiElem.TryGetInt32(out frameIndex);
				else int.TryParse(fiObj?.ToString(), out frameIndex);
			}

			int? filterPid = null;
			if (arguments != null && arguments.TryGetValue("process_id", out var pidObj) &&
				pidObj is JsonElement pidElem && pidElem.TryGetInt32(out var pidInt))
				filterPid = pidInt;

			// All evaluation must happen on the UI/debugger dispatcher thread
			return System.Windows.Application.Current.Dispatcher.Invoke(() => {
				var process = filterPid.HasValue
					? mgr.Processes.FirstOrDefault(p => p.Id == filterPid.Value)
					: mgr.Processes.FirstOrDefault(p => p.State == DbgProcessState.Paused)
					  ?? mgr.Processes.FirstOrDefault();

				if (process == null)
					throw new InvalidOperationException("No debugged process found.");

				if (process.State != DbgProcessState.Paused)
					throw new InvalidOperationException(
						$"Process {process.Id} is not paused. Set a breakpoint and wait for it to hit.");

				// Find current thread (prefer the one with a stack)
				var thread = process.Threads.FirstOrDefault(t => t.GetTopStackFrame() != null)
							 ?? process.Threads.FirstOrDefault();
				if (thread == null)
					throw new InvalidOperationException("No threads found in the process.");

				// Get frames
				var frames = thread.GetFrames(frameIndex + 1);
				if (frames.Length <= frameIndex)
					throw new ArgumentException(
						$"frame_index {frameIndex} out of range (process has {frames.Length} frame(s)).");

				var frame = frames[frameIndex];
				var runtime = frame.Runtime;

				// Get language and create evaluation context
				var language = languageService.Value.GetCurrentLanguage(runtime.RuntimeKindGuid);
				DbgEvaluationContext? context = null;
				try {
					context = language.CreateContext(runtime, frame.Location,
						DbgEvaluationContextOptions.None,
						TimeSpan.FromSeconds(3));

					var evalInfo = new DbgEvaluationInfo(context, frame);

					if (runtime.InternalRuntime is not IDbgDotNetRuntime dotNetRuntime)
						throw new InvalidOperationException("Runtime is not a .NET runtime.");

					// Get method metadata for local names/types
					var method = dotNetRuntime.GetFrameMethod(evalInfo);

					var locals = new List<object>();
					var parameters = new List<object>();

					if (method != null) {
						// Parameters
						var paramInfos = method.GetParameters();
						for (int i = 0; i < paramInfos.Count; i++) {
							var p = paramInfos[i];
							var valResult = dotNetRuntime.GetParameterValue(evalInfo, (uint)i);
							parameters.Add(new {
								Index = i,
								Name = p.Name ?? $"param_{i}",
								Type = p.ParameterType?.FullName ?? "?",
								Value = FormatValueResult(valResult, process)
							});
							valResult.Value?.Dispose();
						}

						// Locals
						var body = method.GetMethodBody();
						if (body != null) {
							foreach (var local in body.LocalVariables) {
								var valResult = dotNetRuntime.GetLocalValue(evalInfo, (uint)local.LocalIndex);
								locals.Add(new {
									Index = local.LocalIndex,
									Name = $"V_{local.LocalIndex}",
									Type = local.LocalType?.FullName ?? "?",
									IsPinned = local.IsPinned,
									Value = FormatValueResult(valResult, process)
								});
								valResult.Value?.Dispose();
							}
						}
					}

					// Frame info
					string frameDesc = frame.Module != null
						? $"{method?.Name ?? "?"} in {frame.Module.Name}"
						: method?.ToString() ?? "Unknown frame";

					var result = JsonSerializer.Serialize(new {
						FrameIndex = frameIndex,
						Frame = frameDesc,
						FunctionToken = $"0x{frame.FunctionToken:X8}",
						FunctionOffset = $"0x{frame.FunctionOffset:X}",
						Parameters = parameters,
						ParameterCount = parameters.Count,
						Locals = locals,
						LocalCount = locals.Count,
						Note = locals.Count == 0 && parameters.Count == 0
							? "No locals/parameters found. The method may be optimized or native."
							: null
					}, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

					return new CallToolResult {
						Content = new List<ToolContent> { new ToolContent { Text = result } }
					};
				}
				finally {
					context?.Close();
					// Note: frame is owned by thread.GetFrames() and should be closed
					foreach (var f in frames)
						f.Close();
				}
			});
		}

		// ── Helpers ──────────────────────────────────────────────────────────────

	// ── eval_expression ──────────────────────────────────────────────────────

	/// <summary>
	/// Evaluates a C# expression in the context of the current paused stack frame.
	/// Equivalent to the Watch window in dnSpy. Requires debugger to be paused.
	/// Arguments: expression* (C# string), frame_index (int, optional, default=0),
	///            process_id (int, optional), func_eval_timeout_seconds (int, optional, default=5)
	/// </summary>
	public CallToolResult EvalExpression(Dictionary<string, object>? arguments) {
		if (arguments == null)
			throw new ArgumentException("Arguments required");
		if (!arguments.TryGetValue("expression", out var exprObj))
			throw new ArgumentException("expression is required");
		var expression = exprObj?.ToString() ?? "";

		var mgr = dbgManager.Value;
		if (!mgr.IsDebugging)
			throw new InvalidOperationException("Debugger is not active. Start a debug session first.");

		int frameIndex = 0;
		if (arguments.TryGetValue("frame_index", out var fiObj)) {
			if (fiObj is JsonElement fiElem) fiElem.TryGetInt32(out frameIndex);
			else int.TryParse(fiObj?.ToString(), out frameIndex);
		}

		int? filterPid = null;
		if (arguments.TryGetValue("process_id", out var pidObj2) &&
			pidObj2 is JsonElement pidElem2 && pidElem2.TryGetInt32(out var pidInt))
			filterPid = pidInt;

		int funcEvalTimeout = 5;
		if (arguments.TryGetValue("func_eval_timeout_seconds", out var tsoObj)) {
			if (tsoObj is JsonElement tsoElem && tsoElem.TryGetInt32(out var tso)) funcEvalTimeout = Math.Max(1, tso);
			else if (int.TryParse(tsoObj?.ToString(), out var tso2)) funcEvalTimeout = Math.Max(1, tso2);
		}

		return System.Windows.Application.Current.Dispatcher.Invoke(() => {
			var process = filterPid.HasValue
				? mgr.Processes.FirstOrDefault(p => p.Id == filterPid.Value)
				: mgr.Processes.FirstOrDefault(p => p.State == DbgProcessState.Paused)
				  ?? mgr.Processes.FirstOrDefault();

			if (process == null)
				throw new InvalidOperationException("No debugged process found.");
			if (process.State != DbgProcessState.Paused)
				throw new InvalidOperationException(
					$"Process {process.Id} is not paused. Use break_debugger or wait for a breakpoint.");

			var thread = process.Threads.FirstOrDefault(t => t.GetTopStackFrame() != null)
			             ?? process.Threads.FirstOrDefault();
			if (thread == null)
				throw new InvalidOperationException("No threads found in the process.");

			var frames = thread.GetFrames(frameIndex + 1);
			if (frames.Length <= frameIndex)
				throw new ArgumentException(
					$"frame_index {frameIndex} out of range ({frames.Length} frames available).");

			var frame = frames[frameIndex];
			var runtime = frame.Runtime;
			var language = languageService.Value.GetCurrentLanguage(runtime.RuntimeKindGuid);
			DbgEvaluationContext? context = null;
			try {
				context = language.CreateContext(runtime, frame.Location,
					DbgEvaluationContextOptions.None,
					TimeSpan.FromSeconds(funcEvalTimeout));

				var evalInfo = new DbgEvaluationInfo(context, frame);

				if (runtime.InternalRuntime is not IDbgDotNetRuntime dotNetRuntime)
					throw new InvalidOperationException("Runtime is not a .NET runtime.");

				var frameMethod = dotNetRuntime.GetFrameMethod(evalInfo);
				var parameterNames = frameMethod?.GetParameters()
					.Select(p => p.Name)
					.ToList() ?? new List<string?>();
				var localIndexes = frameMethod?.GetMethodBody()?.LocalVariables
					.Select(v => (int)v.LocalIndex)
					.ToHashSet() ?? new HashSet<int>();
				var methodDef = ResolveMethodDef(frame);
				var aliasContext = methodDef != null ? DebuggerExpressionAliasContext.Create(methodDef) : null;

				var rewrite = DebuggerExpressionAliasHelper.Rewrite(expression, parameterNames, localIndexes, aliasContext);
				if (rewrite.Error != null) {
					var aliasErrJson = JsonSerializer.Serialize(new {
						Expression = expression,
						RewrittenExpression = rewrite.RewrittenExpression,
						Error = rewrite.Error,
						AliasNotes = rewrite.Notes
					}, new JsonSerializerOptions { WriteIndented = true });
					return new CallToolResult {
						Content = new List<ToolContent> { new ToolContent { Text = aliasErrJson } },
						IsError = true
					};
				}

				var evalResult = language.ExpressionEvaluator.Evaluate(
					evalInfo, rewrite.RewrittenExpression, DbgEvaluationOptions.Expression, null);

				if (evalResult.Error != null) {
					var errJson = JsonSerializer.Serialize(new {
						Expression = expression,
						RewrittenExpression = rewrite.UsedAliases ? rewrite.RewrittenExpression : null,
						AliasNotes = rewrite.Notes.Count == 0 ? null : rewrite.Notes,
						Error = evalResult.Error,
						IsThrownException = evalResult.IsThrownException
					}, new JsonSerializerOptions {
						WriteIndented = true,
						DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
					});
					return new CallToolResult {
						Content = new List<ToolContent> { new ToolContent { Text = errJson } },
						IsError = true
					};
				}

				var resultJson = JsonSerializer.Serialize(new {
					Expression        = expression,
					RewrittenExpression = rewrite.UsedAliases ? rewrite.RewrittenExpression : null,
					AliasNotes = rewrite.Notes.Count == 0 ? null : rewrite.Notes,
					Value             = FormatDbgValue(evalResult.Value),
					IsThrownException = evalResult.IsThrownException,
					HasSideEffects    = (evalResult.Flags & DbgEvaluationResultFlags.SideEffects) != 0
				}, new JsonSerializerOptions {
					WriteIndented = true,
					DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault
				});
				evalResult.Value?.Close();
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = resultJson } }
				};
			}
			finally {
				context?.Close();
				foreach (var f in frames) f.Close();
			}
		});
	}

	public CallToolResult EvalExpressionEx(Dictionary<string, object>? arguments) => EvalExpression(arguments);

	static object FormatDbgValue(DbgValue? value) {
		if (value == null)
			return new { Kind = "null" };
		if (value.HasRawValue && value.ValueType != DbgSimpleValueType.Other) {
			if (value.ValueType == DbgSimpleValueType.StringUtf16)
				return new { Kind = "String", Value = (object?)(value.RawValue as string ?? "null") };
			return new { Kind = value.ValueType.ToString(), Value = (object?)(value.RawValue?.ToString() ?? "null") };
		}
		var addr = value.GetRawAddressValue(onlyDataAddress: false);
		if (addr.HasValue)
			return new { Kind = "Object", Address = $"0x{addr.Value.Address:X16}", SizeBytes = addr.Value.Length };
		return new { Kind = "Unknown" };
	}

		static object FormatValueResult(DbgDotNetValueResult result, DbgProcess process) {
			if (result.HasError)
				return new { Error = result.ErrorMessage ?? "Unknown error" };
			if (result.ValueIsException)
				return new { Exception = result.Value?.Type?.FullName ?? "Exception" };
			if (result.Value == null)
				return new { Error = "null result" };

			return FormatValue(result.Value, process);
		}

		static object FormatValue(DbgDotNetValue value, DbgProcess process) {
			if (value.IsNull)
				return new { Raw = (object?)"null", Type = value.Type?.FullName };

			var raw = value.GetRawValue();

			if (raw.HasRawValue && raw.ValueType != DbgSimpleValueType.Other) {
				// Primitive or string
				var strVal = raw.ValueType == DbgSimpleValueType.StringUtf16
					? (raw.RawValue as string ?? "null")
					: raw.RawValue?.ToString() ?? "null";
				return new { Raw = (object?)strVal, Type = value.Type?.FullName, Kind = raw.ValueType.ToString() };
			}

			// Complex object: return type and memory address if available
			var addrVal = value.GetRawAddressValue(onlyDataAddress: true);
			if (addrVal.HasValue)
				return new { Type = value.Type?.FullName, Address = $"0x{addrVal.Value.Address:X16}", Length = addrVal.Value.Length, Kind = "Object" };

			// Array?
			if (value.GetArrayCount(out uint elemCount))
				return new { Type = value.Type?.FullName, Kind = "Array", ElementCount = elemCount };

			return new { Type = value.Type?.FullName, Kind = "Complex" };
		}

		MethodDef? ResolveMethodDef(dnSpy.Contracts.Debugger.CallStack.DbgStackFrame frame) {
			var moduleFile = frame.Module?.Filename;
			var token = frame.FunctionToken;
			if (string.IsNullOrWhiteSpace(moduleFile) || token == 0)
				return null;

			var assembly = documentTreeView.GetAllModuleNodes()
				.Select(m => m.Document?.AssemblyDef)
				.FirstOrDefault(a => a != null && a.Modules.Any(mod =>
					string.Equals(mod.Location, moduleFile, StringComparison.OrdinalIgnoreCase) ||
					string.Equals(mod.Name, frame.Module?.Name, StringComparison.OrdinalIgnoreCase)));
			if (assembly == null)
				return null;

			return assembly.Modules
				.SelectMany(m => m.GetTypes())
				.SelectMany(t => t.Methods)
				.FirstOrDefault(m => m.MDToken.Raw == token);
		}
	}
}
