/*
    Copyright (C) 2026 @chichicaste
    Modifications Copyright (C) 2026 @geocine

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
using System.Threading;
using dnlib.DotNet;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.DotNet.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.CorDebug;
using dnSpy.Contracts.Debugger.Exceptions;
using dnSpy.Contracts.Debugger.Steppers;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Metadata;
using dnSpy.MCP.Server.Contracts;
using dnSpy.MCP.Server.Helper;

namespace dnSpy.MCP.Server.Application {
	/// <summary>
	/// Debugger integration tools: get state, manage breakpoints, control execution, and inspect the call stack.
	/// Requires the Debugger extension to be loaded. Operations that need a paused debugger will return
	/// descriptive errors when the debugger is not in the required state.
	/// </summary>
	[Export(typeof(DebugTools))]
	public sealed class DebugTools {
		readonly Lazy<DbgManager> dbgManager;
		readonly Lazy<DbgCodeBreakpointsService> breakpointsService;
		readonly Lazy<DbgDotNetBreakpointFactory> breakpointFactory;
		readonly IDocumentTreeView documentTreeView;
		readonly Lazy<AttachableProcessesService> attachableProcessesService;
		readonly Lazy<DbgExceptionSettingsService> exceptionSettingsService;

		[ImportingConstructor]
		public DebugTools(
			Lazy<DbgManager> dbgManager,
			Lazy<DbgCodeBreakpointsService> breakpointsService,
			Lazy<DbgDotNetBreakpointFactory> breakpointFactory,
			IDocumentTreeView documentTreeView,
			Lazy<AttachableProcessesService> attachableProcessesService,
			Lazy<DbgExceptionSettingsService> exceptionSettingsService) {
			this.dbgManager = dbgManager;
			this.breakpointsService = breakpointsService;
			this.breakpointFactory = breakpointFactory;
			this.documentTreeView = documentTreeView;
			this.attachableProcessesService = attachableProcessesService;
			this.exceptionSettingsService = exceptionSettingsService;
		}

		/// <summary>
		/// Returns the current debugger state.
		/// Arguments: none
		/// </summary>
		public CallToolResult GetDebuggerState() {
			try {
				var mgr = dbgManager.Value;
				var processes = mgr.Processes.Select(p => new {
					Id = p.Id,
					State = p.State.ToString(),
					IsRunning = p.IsRunning,
					RuntimeCount = p.Runtimes.Length,
					ThreadCount = p.Threads.Length
				}).ToList();

				var result = JsonSerializer.Serialize(new {
					IsDebugging = mgr.IsDebugging,
					IsRunning = mgr.IsRunning,
					ProcessCount = processes.Count,
					Processes = processes
				}, new JsonSerializerOptions { WriteIndented = true });

				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = result } }
				};
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "GetDebuggerState failed");
				var result = JsonSerializer.Serialize(new {
					IsDebugging = false,
					IsRunning = (bool?)null,
					ProcessCount = 0,
					Processes = Array.Empty<object>(),
					Error = ex.Message
				}, new JsonSerializerOptions { WriteIndented = true });
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = result } }
				};
			}
		}

		/// <summary>
		/// Lists all code breakpoints currently registered in dnSpy.
		/// Arguments: none
		/// </summary>
		public CallToolResult ListBreakpoints() {
			try {
				var service = breakpointsService.Value;
				var bps = service.VisibleBreakpoints.Select((bp, idx) => new {
					Index = idx,
					IsEnabled = bp.IsEnabled,
					IsHidden = bp.IsHidden,
					BoundCount = bp.BoundBreakpoints.Length,
					LocationType = bp.Location?.Type ?? "Unknown",
					LocationString = bp.Location?.ToString() ?? "Unknown",
					Labels = bp.Labels?.ToArray() ?? Array.Empty<string>()
				}).ToList();

				var result = JsonSerializer.Serialize(new {
					BreakpointCount = bps.Count,
					Breakpoints = bps
				}, new JsonSerializerOptions { WriteIndented = true });

				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = result } }
				};
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "ListBreakpoints failed");
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = $"Error listing breakpoints: {ex.Message}" } },
					IsError = true
				};
			}
		}

		/// <summary>
		/// Sets a breakpoint at a method entry point (IL offset 0 by default).
		/// Arguments: assembly_name, type_full_name, method_name, il_offset (optional, default 0)
		/// The breakpoint persists across debug sessions via dnSpy's breakpoint storage.
		/// </summary>
		public CallToolResult SetBreakpoint(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("assembly_name", out var asmNameObj))
				throw new ArgumentException("assembly_name is required");
			if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
				throw new ArgumentException("type_full_name is required");
			if (!arguments.TryGetValue("method_name", out var methodNameObj))
				throw new ArgumentException("method_name is required");

			string? filePath = null;
			if (arguments.TryGetValue("file_path", out var fpObj))
				filePath = fpObj?.ToString();

			var assembly = FindAssemblyByName(asmNameObj.ToString() ?? "", filePath);
			if (assembly == null)
				throw new ArgumentException($"Assembly not found: {asmNameObj}");

			var type = FindTypeInAssembly(assembly, typeNameObj.ToString() ?? "");
			if (type == null)
				throw new ArgumentException($"Type not found: {typeNameObj}");

			var method = type.Methods.FirstOrDefault(m => m.Name.String == (methodNameObj.ToString() ?? ""));
			if (method == null)
				throw new ArgumentException($"Method not found: {methodNameObj}");

			uint ilOffset = 0;
			if (arguments.TryGetValue("il_offset", out var offsetObj) && offsetObj is JsonElement offsetElem)
				offsetElem.TryGetUInt32(out ilOffset);

			var module = method.Module;
			var moduleId = ModuleId.CreateFromFile(module);
			var token = method.MDToken.Raw;

			// Optional condition expression
			string? conditionExpr = null;
			if (arguments.TryGetValue("condition", out var condObj) && !string.IsNullOrWhiteSpace(condObj?.ToString()))
				conditionExpr = condObj!.ToString()!.Trim();

			try {
				var bp = breakpointFactory.Value.Create(moduleId, token, ilOffset);
				if (bp == null) {
					return new CallToolResult {
						Content = new List<ToolContent> { new ToolContent { Text = $"A breakpoint already exists at {method.FullName} +IL_{ilOffset:X4}" } }
					};
				}

				// Apply condition if provided
				if (conditionExpr != null)
					bp.Condition = new DbgCodeBreakpointCondition(DbgCodeBreakpointConditionKind.IsTrue, conditionExpr);

				var result = JsonSerializer.Serialize(new {
					Success    = true,
					Method     = method.FullName,
					ILOffset   = ilOffset,
					Token      = $"0x{token:X8}",
					ModulePath = module.Location,
					IsEnabled  = bp.IsEnabled,
					Condition  = conditionExpr
				}, new JsonSerializerOptions {
					WriteIndented = true,
					DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
				});

				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = result } }
				};
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "SetBreakpoint failed");
				throw new Exception($"Failed to set breakpoint at {method.FullName}: {ex.Message}");
			}
		}

		/// <summary>
		/// Removes a breakpoint from a method.
		/// Arguments: assembly_name, type_full_name, method_name, il_offset (optional, default 0)
		/// </summary>
		public CallToolResult RemoveBreakpoint(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("assembly_name", out var asmNameObj))
				throw new ArgumentException("assembly_name is required");
			if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
				throw new ArgumentException("type_full_name is required");
			if (!arguments.TryGetValue("method_name", out var methodNameObj))
				throw new ArgumentException("method_name is required");

			var assembly = FindAssemblyByName(asmNameObj.ToString() ?? "");
			if (assembly == null)
				throw new ArgumentException($"Assembly not found: {asmNameObj}");

			var type = FindTypeInAssembly(assembly, typeNameObj.ToString() ?? "");
			if (type == null)
				throw new ArgumentException($"Type not found: {typeNameObj}");

			var method = type.Methods.FirstOrDefault(m => m.Name.String == (methodNameObj.ToString() ?? ""));
			if (method == null)
				throw new ArgumentException($"Method not found: {methodNameObj}");

			uint ilOffset = 0;
			if (arguments.TryGetValue("il_offset", out var offsetObj) && offsetObj is JsonElement offsetElem)
				offsetElem.TryGetUInt32(out ilOffset);

			var module = method.Module;
			var moduleId = ModuleId.CreateFromFile(module);
			var token = method.MDToken.Raw;

			var bp = breakpointFactory.Value.TryGetBreakpoint(moduleId, token, ilOffset);
			if (bp == null) {
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = $"No breakpoint found at {method.FullName} +IL_{ilOffset:X4}" } }
				};
			}

			breakpointsService.Value.Remove(bp);

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = $"Breakpoint removed from {method.FullName} +IL_{ilOffset:X4}" } }
			};
		}

		/// <summary>
		/// Removes all visible breakpoints.
		/// Arguments: none
		/// </summary>
		public CallToolResult ClearAllBreakpoints() {
			try {
				breakpointsService.Value.Clear();
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = "All breakpoints cleared." } }
				};
			}
			catch (Exception ex) {
				throw new Exception($"Failed to clear breakpoints: {ex.Message}");
			}
		}

		/// <summary>
		/// Resumes execution of all paused processes.
		/// Arguments: none
		/// </summary>
		public CallToolResult ContinueDebugger() {
			try {
				dbgManager.Value.RunAll();
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = "Debugger resumed (RunAll called)." } }
				};
			}
			catch (Exception ex) {
				throw new Exception($"Failed to continue debugger: {ex.Message}");
			}
		}

		/// <summary>
		/// Pauses all running processes.
		/// Arguments: none
		/// </summary>
		public CallToolResult BreakDebugger() {
			try {
				dbgManager.Value.BreakAll();
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = "Debugger paused (BreakAll called)." } }
				};
			}
			catch (Exception ex) {
				throw new Exception($"Failed to break debugger: {ex.Message}");
			}
		}

		/// <summary>
		/// Stops all active debug sessions.
		/// Arguments: none
		/// </summary>
		public CallToolResult StopDebugging() {
			try {
				dbgManager.Value.StopDebuggingAll();
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = "All debug sessions stopped." } }
				};
			}
			catch (Exception ex) {
				throw new Exception($"Failed to stop debugging: {ex.Message}");
			}
		}

		/// <summary>
		/// Returns the call stack of the current (or first paused) thread.
		/// Arguments: none — debugger must be paused.
		/// </summary>
		public CallToolResult GetCallStack() {
			try {
				var mgr = dbgManager.Value;

				if (!mgr.IsDebugging) {
					return new CallToolResult {
						Content = new List<ToolContent> { new ToolContent { Text = "Debugger is not active. Start debugging first." } }
					};
				}

				// Prefer the currently selected thread; fall back to the first paused thread.
				DbgThread? currentThread = mgr.CurrentThread?.Current;

				if (currentThread == null) {
					var pausedProcess = mgr.Processes.FirstOrDefault(p => p.State == DbgProcessState.Paused);
					if (pausedProcess == null) {
						return new CallToolResult {
							Content = new List<ToolContent> { new ToolContent { Text = "No paused process found. Debugger may still be running. Use break_debugger first." } }
						};
					}
					currentThread = pausedProcess.Threads.FirstOrDefault();
				}

				if (currentThread == null) {
					return new CallToolResult {
						Content = new List<ToolContent> { new ToolContent { Text = "No thread is available." } }
					};
				}

				const int maxFrames = 50;
				var frames = new List<object>();
				var stackFrames = currentThread.GetFrames(maxFrames);
				try {
					foreach (var frame in stackFrames) {
						try {
							frames.Add(new {
								Index = frames.Count,
								FunctionToken = $"0x{frame.FunctionToken:X8}",
								FunctionOffset = frame.FunctionOffset,
								ModuleName = frame.Module?.Name ?? "Unknown",
								IsCurrentStatement = (frame.Flags & DbgStackFrameFlags.LocationIsNextStatement) != 0
							});
						}
						finally {
							frame.Close();
						}
					}
				}
				finally {
					// GetFrames() returns owned frames; they were closed in the loop above.
				}

				var result = JsonSerializer.Serialize(new {
					ThreadId = currentThread.Id,
					ManagedId = currentThread.ManagedId,
					FrameCount = frames.Count,
					MaxFramesReturned = maxFrames,
					Frames = frames
				}, new JsonSerializerOptions { WriteIndented = true });

				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = result } }
				};
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "GetCallStack failed");
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = $"Error getting call stack: {ex.Message}" } },
					IsError = true
				};
			}
		}

		// ── start_debugging ──────────────────────────────────────────────────────

		/// <summary>
		/// Launches an EXE under the dnSpy debugger.
		/// Arguments: exe_path* | arguments | working_directory | break_kind (default "EntryPoint")
		/// </summary>
		public CallToolResult StartDebugging(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("exe_path", out var exePathObj))
				throw new ArgumentException("exe_path is required");

			var exePath = exePathObj.ToString() ?? string.Empty;
			if (!System.IO.File.Exists(exePath))
				throw new ArgumentException($"File not found: {exePath}");

			string? commandLine = null;
			if (arguments.TryGetValue("arguments", out var argsObj))
				commandLine = argsObj?.ToString();

			string? workingDir = null;
			if (arguments.TryGetValue("working_directory", out var wdObj))
				workingDir = wdObj?.ToString();
			if (string.IsNullOrEmpty(workingDir))
				workingDir = System.IO.Path.GetDirectoryName(exePath);

			string breakKind = PredefinedBreakKinds.EntryPoint;
			if (arguments.TryGetValue("break_kind", out var bkObj) && bkObj?.ToString() is string bkStr && !string.IsNullOrEmpty(bkStr))
				breakKind = bkStr;

			var opts = new DotNetFrameworkStartDebuggingOptions {
				Filename         = exePath,
				CommandLine      = commandLine ?? string.Empty,
				WorkingDirectory = workingDir,
				BreakKind        = breakKind,
			};

			var error = dbgManager.Value.Start(opts);

			var result = JsonSerializer.Serialize(new {
				Started   = error == null,
				ExePath   = exePath,
				BreakKind = breakKind,
				Error     = error,
				Note      = error == null
					? "Process launched asynchronously. Use get_debugger_state to check when it is paused."
					: null
			}, new JsonSerializerOptions {
				WriteIndented = true,
				DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
			});

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = result } }
			};
		}

		// ── attach_to_process ────────────────────────────────────────────────────

		/// <summary>
		/// Attaches the dnSpy debugger to a running .NET process by PID.
		/// Arguments: process_id*
		/// </summary>
		public CallToolResult AttachToProcess(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("process_id", out var pidObj))
				throw new ArgumentException("process_id is required");

			int pid = 0;
			if (pidObj is JsonElement pidElem) pidElem.TryGetInt32(out pid);
			else int.TryParse(pidObj?.ToString(), out pid);
			if (pid <= 0)
				throw new ArgumentException("process_id must be a positive integer");

			var processes = attachableProcessesService.Value
				.GetAttachableProcessesAsync(null, new[] { pid }, null)
				.GetAwaiter().GetResult();

			if (processes.Length == 0)
				throw new ArgumentException(
					$"No attachable .NET process found with PID {pid}. " +
					"The process may not exist or does not expose a supported runtime.");

			// Attach to the first matching entry (each entry represents one CLR runtime in the process)
			var target = processes[0];
			target.Attach();

			var result = JsonSerializer.Serialize(new {
				Attached    = true,
				ProcessId   = pid,
				RuntimeName = target.RuntimeName,
				Name        = target.Name,
				Note        = "Attach is asynchronous. Use get_debugger_state to verify the session."
			}, new JsonSerializerOptions { WriteIndented = true });

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = result } }
			};
		}

		// ── Helpers ─────────────────────────────────────────────────────────────

	// ── Stepping ─────────────────────────────────────────────────────────────

	/// <summary>
	/// Steps over the current statement.
	/// Arguments: thread_id (optional), process_id (optional), timeout_seconds (optional, default 30)
	/// </summary>
	public CallToolResult StepOver(Dictionary<string, object>? arguments) =>
		StepImpl(arguments, DbgStepKind.StepOver);

	/// <summary>
	/// Steps into the current statement (enters called methods).
	/// Arguments: thread_id (optional), process_id (optional), timeout_seconds (optional, default 30)
	/// </summary>
	public CallToolResult StepInto(Dictionary<string, object>? arguments) =>
		StepImpl(arguments, DbgStepKind.StepInto);

	/// <summary>
	/// Steps out of the current method (runs until caller resumes).
	/// Arguments: thread_id (optional), process_id (optional), timeout_seconds (optional, default 30)
	/// </summary>
	public CallToolResult StepOut(Dictionary<string, object>? arguments) =>
		StepImpl(arguments, DbgStepKind.StepOut);

	/// <summary>
	/// Returns the current execution location (top frame of current/first paused thread).
	/// Arguments: thread_id (optional), process_id (optional)
	/// </summary>
	public CallToolResult GetCurrentLocation(Dictionary<string, object>? arguments) {
		var mgr = dbgManager.Value;
		if (!mgr.IsDebugging)
			throw new InvalidOperationException("No active debug session.");

		var thread = ResolveThread(mgr, arguments);
		if (thread == null)
			throw new InvalidOperationException(
				"No paused thread found. Use break_debugger or wait for a breakpoint.");

		return System.Windows.Application.Current.Dispatcher.Invoke(() => {
			var frames = thread.GetFrames(1);
			if (frames.Length == 0)
				return new CallToolResult { Content = new List<ToolContent> {
					new ToolContent { Text = "Thread has no stack frames." } } };
			var f = frames[0];
			var json = JsonSerializer.Serialize(new {
				ThreadId        = (int)thread.Id,
				FunctionToken   = $"0x{f.FunctionToken:X8}",
				FunctionOffset  = f.FunctionOffset,
				ModuleName      = f.Module?.Name ?? "?",
				IsNextStatement = (f.Flags & DbgStackFrameFlags.LocationIsNextStatement) != 0
			}, new JsonSerializerOptions { WriteIndented = true });
			f.Close();
			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = json } }
			};
		});
	}

	/// <summary>
	/// Polls until any process becomes paused, then returns info about it.
	/// Arguments: timeout_seconds (optional, default 30)
	/// </summary>
	public CallToolResult WaitForPause(Dictionary<string, object>? arguments) {
		var mgr = dbgManager.Value;
		if (!mgr.IsDebugging)
			throw new InvalidOperationException("No active debug session.");

		int timeoutSeconds = 30;
		if (arguments != null && arguments.TryGetValue("timeout_seconds", out var tso)) {
			if (tso is JsonElement je && je.TryGetInt32(out var ti)) timeoutSeconds = Math.Max(1, ti);
			else if (int.TryParse(tso?.ToString(), out var ti2)) timeoutSeconds = Math.Max(1, ti2);
		}

		var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
		while (DateTime.UtcNow < deadline) {
			var paused = mgr.Processes.FirstOrDefault(p => p.State == DbgProcessState.Paused);
			if (paused != null) {
				var json = JsonSerializer.Serialize(new {
					Paused      = true,
					ProcessId   = (int)paused.Id,
					ThreadCount = paused.Threads.Length
				}, new JsonSerializerOptions { WriteIndented = true });
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = json } }
				};
			}
			Thread.Sleep(100);
		}
		throw new TimeoutException($"Debugger did not pause within {timeoutSeconds}s.");
	}

	// ── Private stepping helpers ──────────────────────────────────────────────

	DbgThread? ResolveThread(DbgManager mgr, Dictionary<string, object>? args) {
		// 1. Explicit thread_id
		if (args != null && args.TryGetValue("thread_id", out var tidObj)) {
			if (uint.TryParse(tidObj?.ToString(), out var tidVal))
				foreach (var p in mgr.Processes)
					foreach (var t in p.Threads)
						if (t.Id == tidVal) return t;
			throw new ArgumentException($"Thread {tidObj} not found");
		}
		// 2. Optional process_id filter
		DbgProcess? targetProc = null;
		if (args != null && args.TryGetValue("process_id", out var pidObj)) {
			if (uint.TryParse(pidObj?.ToString(), out var pidVal))
				targetProc = mgr.Processes.FirstOrDefault(p => p.Id == pidVal);
			if (targetProc == null) throw new ArgumentException($"Process {pidObj} not found");
		}
		// 3. Current thread (honors process_id if set)
		var cur = mgr.CurrentThread?.Current;
		if (cur != null && (targetProc == null || cur.Process == targetProc)) return cur;
		// 4. First paused thread in target/any process
		var procs = targetProc != null ? new[] { targetProc } : mgr.Processes.ToArray();
		return procs.Where(p => p.State == DbgProcessState.Paused)
		            .SelectMany(p => p.Threads).FirstOrDefault();
	}

	CallToolResult StepImpl(Dictionary<string, object>? arguments, DbgStepKind stepKind) {
		var mgr = dbgManager.Value;
		if (!mgr.IsDebugging)
			throw new InvalidOperationException(
				"No active debug session. Use start_debugging or attach_to_process first.");

		// Validate that the process is actually paused before attempting to step.
		// IsRunning is bool? — null = no session, true = running, false = paused.
		if (mgr.IsRunning == true)
			throw new InvalidOperationException(
				"Cannot step: the process is currently running. " +
				"Use break_debugger or wait_for_pause to pause it first.");

		var thread = ResolveThread(mgr, arguments);
		if (thread == null)
			throw new InvalidOperationException(
				"No paused thread found. Use break_debugger or wait for a breakpoint to hit.");

		// Default 15s — leaves a safety margin before typical MCP client timeouts (~30s).
		int timeoutSeconds = 15;
		if (arguments != null && arguments.TryGetValue("timeout_seconds", out var tso)) {
			if (tso is JsonElement je && je.TryGetInt32(out var ti)) timeoutSeconds = Math.Max(1, ti);
			else if (int.TryParse(tso?.ToString(), out var ti2)) timeoutSeconds = Math.Max(1, ti2);
		}

		var mre = new ManualResetEventSlim(false);
		string? stepError = null;
		object? frameInfo = null;

		System.Windows.Application.Current.Dispatcher.Invoke(() => {
			var stepper = thread.CreateStepper();
			if (!stepper.CanStep) {
				stepper.Close();
				throw new InvalidOperationException(
					"Cannot step: stepper reports CanStep=false. " +
					"The thread may not be at a steppable IL instruction (e.g. native frame or JIT thunk). " +
					"Try step_into or verify the current frame with get_current_location.");
			}
			stepper.StepComplete += (s, e) => {
				stepError = e.Error;
				var frames = e.Thread.GetFrames(1);
				if (frames.Length > 0) {
					frameInfo = new {
						ThreadId        = (int)e.Thread.Id,
						FunctionToken   = $"0x{frames[0].FunctionToken:X8}",
						FunctionOffset  = frames[0].FunctionOffset,
						ModuleName      = frames[0].Module?.Name ?? "?",
						IsNextStatement = (frames[0].Flags & DbgStackFrameFlags.LocationIsNextStatement) != 0
					};
					frames[0].Close();
				}
				mre.Set();
			};
			stepper.Step(stepKind, autoClose: true);
		});

		// Poll every 100 ms instead of a single blocking wait.
		// This allows us to detect process death (crash, unhandled exception, detach) immediately,
		// rather than waiting the full timeout when StepComplete will never fire.
		var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
		while (!mre.Wait(100)) {
			if (DateTime.UtcNow > deadline)
				throw new TimeoutException(
					$"Step did not complete within {timeoutSeconds}s. " +
					"The process may have resumed without triggering StepComplete, " +
					"or a dialog may be blocking the debugger. " +
					"Check get_debugger_state and list_dialogs.");
			if (!mgr.IsDebugging)
				throw new InvalidOperationException(
					"The debug session ended while waiting for the step to complete. " +
					"The process likely crashed due to an unhandled exception (e.g. Anti-Tamper protection " +
					"detected a patched binary and caused an AccessViolationException in the module .cctor). " +
					"To bypass Anti-Tamper: use unpack_from_memory (break_kind=EntryPoint) BEFORE patching, " +
					"or neutralize the Anti-Tamper .cctor with patch_method_to_ret before saving.");
		}

		if (stepError != null)
			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent {
					Text = $"Step completed with error: {stepError}" } },
				IsError = true
			};

		var json = JsonSerializer.Serialize(new {
			StepKind = stepKind.ToString(),
			Location = frameInfo
		}, new JsonSerializerOptions { WriteIndented = true });
		return new CallToolResult {
			Content = new List<ToolContent> { new ToolContent { Text = json } }
		};
	}

	// ── Exception breakpoints ─────────────────────────────────────────────────

	/// <summary>
	/// Adds or updates an exception breakpoint so the debugger pauses when the exception is thrown.
	/// Arguments: exception_type* | first_chance (bool, default true) | second_chance (bool, default false) | category (optional, default "DotNet")
	/// </summary>
	public CallToolResult SetExceptionBreakpoint(Dictionary<string, object>? arguments) {
		if (arguments == null || !arguments.TryGetValue("exception_type", out var typeObj))
			throw new ArgumentException("exception_type is required");
		string exceptionType = typeObj?.ToString() ?? "";
		if (string.IsNullOrEmpty(exceptionType))
			throw new ArgumentException("exception_type cannot be empty");

		string category = PredefinedExceptionCategories.DotNet;
		if (arguments.TryGetValue("category", out var catObj) && !string.IsNullOrEmpty(catObj?.ToString()))
			category = catObj!.ToString()!;

		bool firstChance = true;
		if (arguments.TryGetValue("first_chance", out var fcObj)) {
			if (fcObj is JsonElement fce) firstChance = fce.ValueKind != JsonValueKind.False;
			else bool.TryParse(fcObj?.ToString(), out firstChance);
		}
		bool secondChance = false;
		if (arguments.TryGetValue("second_chance", out var scObj)) {
			if (scObj is JsonElement sce) secondChance = sce.ValueKind != JsonValueKind.False;
			else bool.TryParse(scObj?.ToString(), out secondChance);
		}

		var flags = DbgExceptionDefinitionFlags.None;
		if (firstChance)  flags |= DbgExceptionDefinitionFlags.StopFirstChance;
		if (secondChance) flags |= DbgExceptionDefinitionFlags.StopSecondChance;

		var id  = new DbgExceptionId(category, exceptionType);
		var svc = exceptionSettingsService.Value;
		var settings = new DbgExceptionSettings(flags);

		string action;
		if (svc.TryGetDefinition(id, out _)) {
			svc.Modify(id, settings);
			action = "modified";
		} else {
			var def = new DbgExceptionDefinition(id, flags);
			svc.Add(new DbgExceptionSettingsInfo(def, settings));
			action = "added";
		}

		var json = JsonSerializer.Serialize(new {
			Action        = action,
			ExceptionType = exceptionType,
			Category      = category,
			FirstChance   = firstChance,
			SecondChance  = secondChance,
			Note          = "Use continue_debugger to resume; the debugger will now break when this exception is thrown."
		}, new JsonSerializerOptions { WriteIndented = true });
		return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = json } } };
	}

	/// <summary>
	/// Removes an exception breakpoint previously set via set_exception_breakpoint.
	/// Arguments: exception_type* | category (optional, default "DotNet")
	/// </summary>
	public CallToolResult RemoveExceptionBreakpoint(Dictionary<string, object>? arguments) {
		if (arguments == null || !arguments.TryGetValue("exception_type", out var typeObj))
			throw new ArgumentException("exception_type is required");
		string exceptionType = typeObj?.ToString() ?? "";
		if (string.IsNullOrEmpty(exceptionType))
			throw new ArgumentException("exception_type cannot be empty");

		string category = PredefinedExceptionCategories.DotNet;
		if (arguments.TryGetValue("category", out var catObj) && !string.IsNullOrEmpty(catObj?.ToString()))
			category = catObj!.ToString()!;

		var id  = new DbgExceptionId(category, exceptionType);
		var svc = exceptionSettingsService.Value;

		if (!svc.TryGetDefinition(id, out _))
			return new CallToolResult { Content = new List<ToolContent> {
				new ToolContent { Text = $"Exception '{exceptionType}' not found in the exception settings list. Use list_exception_breakpoints to see active entries." } } };

		svc.Remove(new[] { id });
		return new CallToolResult { Content = new List<ToolContent> {
			new ToolContent { Text = $"Exception breakpoint removed: {category} \u2014 {exceptionType}" } } };
	}

	/// <summary>
	/// Lists all exception breakpoints that have StopFirstChance or StopSecondChance enabled.
	/// Arguments: none
	/// </summary>
	public CallToolResult ListExceptionBreakpoints() {
		var svc = exceptionSettingsService.Value;
		var active = svc.Exceptions
			.Where(e => e.Settings.Flags != DbgExceptionDefinitionFlags.None)
			.Select(e => new {
				Category      = e.Definition.Id.Category,
				ExceptionType = e.Definition.Id.HasName
					? e.Definition.Id.Name
					: (e.Definition.Id.IsDefaultId ? "<<default>>" : $"Code:0x{e.Definition.Id.Code:X8}"),
				FirstChance   = (e.Settings.Flags & DbgExceptionDefinitionFlags.StopFirstChance)  != 0,
				SecondChance  = (e.Settings.Flags & DbgExceptionDefinitionFlags.StopSecondChance) != 0
			})
			.OrderBy(e => e.Category).ThenBy(e => e.ExceptionType)
			.ToList();

		var json = JsonSerializer.Serialize(new {
			Count = active.Count,
			ExceptionBreakpoints = active
		}, new JsonSerializerOptions { WriteIndented = true });
		return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = json } } };
	}

	AssemblyDef? FindAssemblyByName(string name, string? filePath = null) {
			return UiThreadHelper.Invoke(() => {
				if (!string.IsNullOrEmpty(filePath)) {
					var normalized = filePath!.Replace('/', '\\');
					var byPath = documentTreeView.GetAllModuleNodes()
						.FirstOrDefault(m => (m.Document?.Filename ?? "").Replace('/', '\\')
							.Equals(normalized, StringComparison.OrdinalIgnoreCase));
					if (byPath?.Document?.AssemblyDef != null)
						return byPath.Document.AssemblyDef;
				}

				return documentTreeView.GetAllModuleNodes()
					.Select(m => m.Document?.AssemblyDef)
					.FirstOrDefault(a => a != null && a.Name.String.Equals(name, StringComparison.OrdinalIgnoreCase));
			});
		}

		TypeDef? FindTypeInAssembly(AssemblyDef assembly, string fullName) =>
			assembly.Modules
				.SelectMany(m => GetAllTypesRecursive(m.Types))
				.FirstOrDefault(t => t.FullName.Equals(fullName, StringComparison.Ordinal));

		static System.Collections.Generic.IEnumerable<TypeDef> GetAllTypesRecursive(System.Collections.Generic.IEnumerable<TypeDef> types) {
			foreach (var t in types) {
				yield return t;
				foreach (var n in GetAllTypesRecursive(t.NestedTypes))
					yield return n;
			}
		}
	}
}
