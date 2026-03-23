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
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet;
using dnSpy.Contracts.Debugger.DotNet.CorDebug;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.MCP.Server.Contracts;
using dnSpy.MCP.Server.Helper;

namespace dnSpy.MCP.Server.Application {
	/// <summary>
	/// Memory-dump tools: enumerate modules in running processes and extract their bytes from
	/// memory.  Requires an active debug session (started via dnSpy's debug menu or
	/// attach-to-process). Most operations return descriptive errors when no debugger is active.
	/// </summary>
	[Export(typeof(DumpTools))]
	public sealed class DumpTools {
		readonly Lazy<DbgManager> dbgManager;

		[ImportingConstructor]
		public DumpTools(Lazy<DbgManager> dbgManager) {
			this.dbgManager = dbgManager;
		}

		// ── list_runtime_modules ────────────────────────────────────────────────

		/// <summary>
		/// Lists every module loaded in the currently debugged processes.
		/// Arguments: process_id (optional int), name_filter (optional substring/regex)
		/// </summary>
		public CallToolResult ListRuntimeModules(Dictionary<string, object>? arguments) {
			try {
				var mgr = dbgManager.Value;
				if (!mgr.IsDebugging) {
					return new CallToolResult {
						Content = new List<ToolContent> { new ToolContent {
							Text = "Debugger is not active. Start a debug session first."
						}}
					};
				}

				int? filterPid = null;
				if (arguments != null && arguments.TryGetValue("process_id", out var pidRaw) && pidRaw is JsonElement pidElem && pidElem.TryGetInt32(out var pidInt))
					filterPid = pidInt;

				string? nameFilter = null;
				if (arguments != null && arguments.TryGetValue("name_filter", out var nfObj))
					nameFilter = nfObj?.ToString();

				System.Text.RegularExpressions.Regex? nameRegex = null;
				if (!string.IsNullOrEmpty(nameFilter))
					nameRegex = BuildPatternRegex(nameFilter!);

				var modules = new List<object>();
				foreach (var process in mgr.Processes) {
					if (filterPid.HasValue && process.Id != filterPid.Value)
						continue;
					foreach (var runtime in process.Runtimes) {
						foreach (var module in runtime.Modules) {
							var name = module.Name ?? string.Empty;
							if (nameRegex != null &&
								!nameRegex.IsMatch(name) &&
								!nameRegex.IsMatch(Path.GetFileName(module.Filename ?? string.Empty)))
								continue;

							modules.Add(new {
								ProcessId = process.Id,
								ProcessName = process.Name,
								RuntimeName = runtime.Name,
								ModuleName = name,
								Filename = module.Filename,
								Address = module.HasAddress ? $"0x{module.Address:X16}" : "N/A",
								SizeBytes = module.HasAddress ? (long)module.Size : 0L,
								IsExe = module.IsExe,
								IsDynamic = module.IsDynamic,
								IsInMemory = module.IsInMemory,
								IsOptimized = module.IsOptimized,
								Version = module.Version,
								AppDomain = module.AppDomain?.Name ?? "None"
							});
						}
					}
				}

				var json = JsonSerializer.Serialize(new {
					ModuleCount = modules.Count,
					Modules = modules
				}, new JsonSerializerOptions { WriteIndented = true });

				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = json } }
				};
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "ListRuntimeModules failed");
				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = $"Error: {ex.Message}" } },
					IsError = true
				};
			}
		}

		// ── dump_module_from_memory ──────────────────────────────────────────────

		/// <summary>
		/// Dumps a loaded .NET module from the debugged process to disk.
		/// Arguments: module_name (required), output_path (required), process_id (optional int)
		/// First tries IDbgDotNetRuntime.GetRawModuleBytes (high-quality .NET bytes), then
		/// falls back to reading process memory directly via DbgProcess.ReadMemory.
		/// </summary>
		public CallToolResult DumpModuleFromMemory(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("module_name", out var moduleNameObj))
				throw new ArgumentException("module_name is required");
			if (!arguments.TryGetValue("output_path", out var outputPathObj))
				throw new ArgumentException("output_path is required");

			var moduleName = moduleNameObj.ToString() ?? string.Empty;
			var outputPath = outputPathObj.ToString() ?? string.Empty;

			if (string.IsNullOrWhiteSpace(moduleName))
				throw new ArgumentException("module_name must not be empty");
			if (string.IsNullOrWhiteSpace(outputPath))
				throw new ArgumentException("output_path must not be empty");

			int? filterPid = null;
			if (arguments.TryGetValue("process_id", out var pidObj) && pidObj is JsonElement pidElem && pidElem.TryGetInt32(out var pidInt))
				filterPid = pidInt;

			var mgr = dbgManager.Value;
			if (!mgr.IsDebugging)
				throw new InvalidOperationException("Debugger is not active. Start a debug session first.");

			// Locate the module
			DbgModule? targetModule = null;
			DbgRuntime? targetRuntime = null;

			foreach (var process in mgr.Processes) {
				if (filterPid.HasValue && process.Id != filterPid.Value)
					continue;
				foreach (var runtime in process.Runtimes) {
					var found = runtime.Modules.FirstOrDefault(m =>
						(m.Name ?? string.Empty).Equals(moduleName, StringComparison.OrdinalIgnoreCase) ||
						(m.Filename ?? string.Empty).Equals(moduleName, StringComparison.OrdinalIgnoreCase) ||
						Path.GetFileName(m.Filename ?? string.Empty).Equals(moduleName, StringComparison.OrdinalIgnoreCase));
					if (found != null) {
						targetModule = found;
						targetRuntime = runtime;
						break;
					}
				}
				if (targetModule != null) break;
			}

			if (targetModule == null)
				throw new ArgumentException($"Module '{moduleName}' not found in any debugged process. Use list_runtime_modules to see loaded modules.");

			// Ensure output directory exists
			var outDir = Path.GetDirectoryName(outputPath);
			if (!string.IsNullOrEmpty(outDir))
				Directory.CreateDirectory(outDir);

			// Strategy 1: IDbgDotNetRuntime.GetRawModuleBytes — preferred for .NET modules
			if (targetRuntime!.InternalRuntime is IDbgDotNetRuntime dotNetRuntime) {
				try {
					var rawData = dotNetRuntime.GetRawModuleBytes(targetModule);
					if (rawData.RawBytes != null && rawData.RawBytes.Length > 0) {
						File.WriteAllBytes(outputPath, rawData.RawBytes);
						var json = JsonSerializer.Serialize(new {
							Success = true,
							Module = targetModule.Name,
							OutputPath = outputPath,
							SizeBytes = rawData.RawBytes.Length,
							IsFileLayout = rawData.IsFileLayout,
							Method = "IDbgDotNetRuntime.GetRawModuleBytes"
						}, new JsonSerializerOptions { WriteIndented = true });
						return new CallToolResult {
							Content = new List<ToolContent> { new ToolContent { Text = json } }
						};
					}
				}
				catch (Exception ex) {
					McpLogger.Exception(ex, $"GetRawModuleBytes failed for {moduleName}, falling back to ReadMemory");
				}
			}

			// Strategy 2: DbgProcess.ReadMemory — raw memory fallback
			if (!targetModule.HasAddress)
				throw new InvalidOperationException(
					$"Module '{moduleName}' has no mapped address and IDbgDotNetRuntime returned no bytes. " +
					"This can happen for dynamic (in-memory) modules without a file layout.");

			var moduleSize = (int)targetModule.Size;
			if (moduleSize <= 0 || moduleSize > 256 * 1024 * 1024)
				throw new InvalidOperationException($"Module size out of safe range: {moduleSize:N0} bytes.");

			var bytes = targetModule.Process.ReadMemory(targetModule.Address, moduleSize);
			File.WriteAllBytes(outputPath, bytes);

			var fallbackJson = JsonSerializer.Serialize(new {
				Success = true,
				Module = targetModule.Name,
				OutputPath = outputPath,
				SizeBytes = bytes.Length,
				IsFileLayout = false,
				Method = "DbgProcess.ReadMemory",
				Warning = "Dumped raw process memory (memory layout). " +
						  "The PE headers may be in memory-mapped form. " +
						  "Use a tool like 'pe_unmapper' or LordPE to convert to file layout before loading."
			}, new JsonSerializerOptions { WriteIndented = true });

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = fallbackJson } }
			};
		}

		// ── read_process_memory ──────────────────────────────────────────────────

		/// <summary>
		/// Read raw bytes from a debugged process address and return a formatted hex dump.
		/// Arguments: address (hex string "0x7FF..." or decimal), size (1-65536 bytes),
		///            process_id (optional int)
		/// </summary>
		public CallToolResult ReadProcessMemory(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("address", out var addressObj))
				throw new ArgumentException("address is required");
			if (!arguments.TryGetValue("size", out var sizeObj))
				throw new ArgumentException("size is required");

			var addressStr = (addressObj?.ToString() ?? string.Empty).Trim();
			if (!TryParseAddress(addressStr, out ulong address))
				throw new ArgumentException($"Invalid address '{addressStr}'. Use hex (0x7FF000) or decimal.");

			int size = 0;
			if (sizeObj is JsonElement sizeElem) sizeElem.TryGetInt32(out size);
			else int.TryParse(sizeObj?.ToString(), out size);
			if (size <= 0 || size > 65536)
				throw new ArgumentException($"size must be 1..65536, got {size}.");

			int? filterPid = null;
			if (arguments.TryGetValue("process_id", out var pidObj) && pidObj is JsonElement pidElem && pidElem.TryGetInt32(out var pidInt))
				filterPid = pidInt;

			var mgr = dbgManager.Value;
			if (!mgr.IsDebugging)
				throw new InvalidOperationException("Debugger is not active.");

			DbgProcess? process = null;
			if (filterPid.HasValue)
				process = mgr.Processes.FirstOrDefault(p => p.Id == filterPid.Value);
			else
				process = mgr.Processes.FirstOrDefault(p => p.State == DbgProcessState.Paused)
					?? mgr.Processes.FirstOrDefault();

			if (process == null)
				throw new InvalidOperationException("No debugged process found.");

			var bytes = process.ReadMemory(address, size);

			var json = JsonSerializer.Serialize(new {
				ProcessId = process.Id,
				ProcessName = process.Name,
				Address = $"0x{address:X16}",
				SizeRequested = size,
				SizeRead = bytes.Length,
				HexDump = BuildHexDump(bytes, address),
				Base64 = Convert.ToBase64String(bytes)
			}, new JsonSerializerOptions { WriteIndented = true });

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = json } }
			};
		}

		// ── write_process_memory ─────────────────────────────────────────────────

		/// <summary>
		/// Write bytes to a debugged process address (hot-patching). Requires an active debug session.
		/// Arguments: address (hex/decimal string), bytes_base64 OR hex_bytes (required),
		///            process_id (optional int)
		/// </summary>
		public CallToolResult WriteProcessMemory(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("address", out var addressObj))
				throw new ArgumentException("address is required");

			var addressStr = (addressObj?.ToString() ?? string.Empty).Trim();
			if (!TryParseAddress(addressStr, out ulong address))
				throw new ArgumentException($"Invalid address '{addressStr}'. Use hex (0x7FF000) or decimal.");

			byte[] bytes;
			if (arguments.TryGetValue("bytes_base64", out var b64Obj)) {
				var b64 = b64Obj?.ToString() ?? "";
				try { bytes = Convert.FromBase64String(b64); }
				catch { throw new ArgumentException("bytes_base64 is not valid base64."); }
			}
			else if (arguments.TryGetValue("hex_bytes", out var hexObj)) {
				bytes = ParseHexBytes(hexObj?.ToString() ?? "");
			}
			else {
				throw new ArgumentException("Either bytes_base64 or hex_bytes is required.");
			}

			if (bytes.Length == 0)
				throw new ArgumentException("Byte array must not be empty.");
			if (bytes.Length > 65536)
				throw new ArgumentException($"Byte array too large ({bytes.Length} bytes); max 65536.");

			int? filterPid = null;
			if (arguments.TryGetValue("process_id", out var pidObj) && pidObj is JsonElement pidElem && pidElem.TryGetInt32(out var pidInt))
				filterPid = pidInt;

			bool autoVirtualProtect = false;
			if (arguments.TryGetValue("auto_virtual_protect", out var avpObj)) {
				if (avpObj is JsonElement avpElem && (avpElem.ValueKind == JsonValueKind.True || avpElem.ValueKind == JsonValueKind.False))
					autoVirtualProtect = avpElem.GetBoolean();
				else if (bool.TryParse(avpObj?.ToString(), out var parsed))
					autoVirtualProtect = parsed;
			}

			var mgr = dbgManager.Value;
			if (!mgr.IsDebugging)
				throw new InvalidOperationException("Debugger is not active.");

			DbgProcess? process = null;
			if (filterPid.HasValue)
				process = mgr.Processes.FirstOrDefault(p => p.Id == filterPid.Value);
			else
				process = mgr.Processes.FirstOrDefault(p => p.State == DbgProcessState.Paused)
					?? mgr.Processes.FirstOrDefault();

			if (process == null)
				throw new InvalidOperationException("No debugged process found.");

			uint? originalProtect = null;
			if (autoVirtualProtect) {
				using var handle = OpenProcessForMemoryPatch(process.Id);
				originalProtect = TemporarilyEnableWritableProtection(handle.DangerousGetHandle(), address, bytes.Length);
				try {
					process.WriteMemory(address, bytes);
				}
				finally {
					RestoreMemoryProtection(handle.DangerousGetHandle(), address, bytes.Length, originalProtect.Value);
				}
			}
			else {
				process.WriteMemory(address, bytes);
			}

			var json = JsonSerializer.Serialize(new {
				ProcessId    = process.Id,
				ProcessName  = process.Name,
				Address      = $"0x{address:X16}",
				BytesWritten = bytes.Length,
				AutoVirtualProtect = autoVirtualProtect,
				OriginalProtection = originalProtect.HasValue ? $"0x{originalProtect.Value:X8}" : null,
				Note         = "Memory write completed. Use read_process_memory to verify."
			}, new JsonSerializerOptions {
				WriteIndented = true,
				DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
			});

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = json } }
			};
		}

		// ── get_pe_sections ──────────────────────────────────────────────────────

		/// <summary>
		/// Lists the PE sections of a module loaded in the debugged process.
		/// Arguments: module_name (required), process_id (int, optional)
		/// </summary>
		public CallToolResult GetPeSections(Dictionary<string, object>? arguments) {
			if (arguments == null || !arguments.TryGetValue("module_name", out var moduleNameObj))
				throw new ArgumentException("module_name is required");

			var moduleName = moduleNameObj.ToString() ?? string.Empty;
			int? filterPid = null;
			if (arguments.TryGetValue("process_id", out var pidObj) && pidObj is JsonElement pidElem && pidElem.TryGetInt32(out var pidInt))
				filterPid = pidInt;

			var mgr = dbgManager.Value;
			if (!mgr.IsDebugging)
				throw new InvalidOperationException("Debugger is not active.");

			var (targetModule, _) = FindModule(mgr, moduleName, filterPid);
			if (targetModule == null)
				throw new ArgumentException($"Module '{moduleName}' not found. Use list_runtime_modules.");

			if (!targetModule.HasAddress)
				throw new InvalidOperationException($"Module '{moduleName}' has no mapped address.");

			// Read enough bytes to parse PE headers (4 KB is sufficient for section table)
			int headerSize = (int)Math.Min(targetModule.Size, 8192u);
			var headerBytes = targetModule.Process.ReadMemory(targetModule.Address, headerSize);

			try {
				using var peImage = new dnlib.PE.PEImage(headerBytes, dnlib.PE.ImageLayout.Memory, false);
				var ntHdr = peImage.ImageNTHeaders;
				bool is64 = ntHdr?.OptionalHeader?.Magic == 0x20B; // PE32+ magic
				ulong imageBase = ntHdr?.OptionalHeader?.ImageBase ?? 0;
				uint entryPoint = ntHdr?.OptionalHeader != null ? (uint)ntHdr.OptionalHeader.AddressOfEntryPoint : 0;
				var dataDirs = ntHdr?.OptionalHeader?.DataDirectories;
				bool isDotNet = dataDirs != null && dataDirs.Length > 14 &&
					(uint)dataDirs[14].VirtualAddress != 0;

				var sections = peImage.ImageSectionHeaders.Select(s => new {
					Name = s.DisplayName,
					VirtualAddress = $"0x{(uint)s.VirtualAddress:X8}",
					VirtualSize = s.VirtualSize,
					PointerToRawData = $"0x{s.PointerToRawData:X8}",
					SizeOfRawData = s.SizeOfRawData,
					Characteristics = DescribeSectionCharacteristics((uint)s.Characteristics),
					CharacteristicsRaw = $"0x{(uint)s.Characteristics:X8}"
				}).ToList();

				var result = JsonSerializer.Serialize(new {
					Module = targetModule.Name,
					BaseAddress = $"0x{targetModule.Address:X16}",
					ModuleSize = targetModule.Size,
					ImageBase = $"0x{imageBase:X16}",
					EntryPoint = $"0x{entryPoint:X8}",
					Bitness = is64 ? 64 : 32,
					IsDotNet = isDotNet,
					ImageLayout = targetModule.ImageLayout.ToString(),
					SectionCount = sections.Count,
					Sections = sections
				}, new JsonSerializerOptions { WriteIndented = true });

				return new CallToolResult {
					Content = new List<ToolContent> { new ToolContent { Text = result } }
				};
			}
			catch (Exception ex) {
				throw new InvalidOperationException($"Failed to parse PE headers for '{moduleName}': {ex.Message}");
			}
		}

		// ── dump_pe_section ───────────────────────────────────────────────────────

		/// <summary>
		/// Dumps a specific PE section from a loaded module. Writes to disk and returns base64.
		/// Arguments: module_name (required), section_name (required, e.g. ".text"),
		///            output_path (optional), process_id (int, optional)
		/// </summary>
		public CallToolResult DumpPeSection(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("module_name", out var moduleNameObj))
				throw new ArgumentException("module_name is required");
			if (!arguments.TryGetValue("section_name", out var sectionNameObj))
				throw new ArgumentException("section_name is required");

			var moduleName = moduleNameObj.ToString() ?? string.Empty;
			var sectionName = sectionNameObj.ToString() ?? string.Empty;

			string? outputPath = null;
			if (arguments.TryGetValue("output_path", out var opObj))
				outputPath = opObj?.ToString();

			int? filterPid = null;
			if (arguments.TryGetValue("process_id", out var pidObj) && pidObj is JsonElement pidElem && pidElem.TryGetInt32(out var pidInt))
				filterPid = pidInt;

			var mgr = dbgManager.Value;
			if (!mgr.IsDebugging)
				throw new InvalidOperationException("Debugger is not active.");

			var (targetModule, _) = FindModule(mgr, moduleName, filterPid);
			if (targetModule == null)
				throw new ArgumentException($"Module '{moduleName}' not found.");
			if (!targetModule.HasAddress)
				throw new InvalidOperationException($"Module '{moduleName}' has no mapped address.");

			int moduleSize = (int)targetModule.Size;
			var moduleBytes = targetModule.Process.ReadMemory(targetModule.Address, moduleSize);

			using var peImage = new dnlib.PE.PEImage(moduleBytes, dnlib.PE.ImageLayout.Memory, false);

			var section = peImage.ImageSectionHeaders.FirstOrDefault(s =>
				s.DisplayName.Equals(sectionName, StringComparison.OrdinalIgnoreCase) ||
				s.DisplayName.TrimEnd('\0').Equals(sectionName.TrimStart('.'), StringComparison.OrdinalIgnoreCase));

			if (section == null) {
				var available = string.Join(", ", peImage.ImageSectionHeaders.Select(s => s.DisplayName));
				throw new ArgumentException($"Section '{sectionName}' not found. Available: {available}");
			}

			uint va = (uint)section.VirtualAddress;
			uint sz = Math.Max(section.VirtualSize, section.SizeOfRawData);
			sz = Math.Min(sz, (uint)(moduleBytes.Length - (int)va));

			var sectionBytes = new byte[sz];
			Array.Copy(moduleBytes, (int)va, sectionBytes, 0, (int)sz);

			if (!string.IsNullOrEmpty(outputPath)) {
				var dir = Path.GetDirectoryName(outputPath);
				if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
				File.WriteAllBytes(outputPath, sectionBytes);
			}

			var json = JsonSerializer.Serialize(new {
				Module = targetModule.Name,
				Section = section.DisplayName,
				VirtualAddress = $"0x{va:X8}",
				AbsoluteAddress = $"0x{targetModule.Address + va:X16}",
				SizeBytes = sz,
				OutputPath = outputPath,
				Base64 = Convert.ToBase64String(sectionBytes),
				Characteristics = DescribeSectionCharacteristics((uint)section.Characteristics)
			}, new JsonSerializerOptions { WriteIndented = true });

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = json } }
			};
		}

		// ── dump_module_unpacked ──────────────────────────────────────────────────

		/// <summary>
		/// Full module dump with optional memory→file layout conversion.
		/// Handles .NET, native, and mixed-mode modules. Preferred over dump_module_from_memory
		/// when you need a file that loads cleanly in IDA/Ghidra/dnSpy.
		/// Arguments: module_name (required), output_path (required),
		///            try_fix_pe_layout (bool, default=true), process_id (int, optional)
		/// </summary>
		public CallToolResult DumpModuleUnpacked(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("module_name", out var moduleNameObj))
				throw new ArgumentException("module_name is required");
			if (!arguments.TryGetValue("output_path", out var outputPathObj))
				throw new ArgumentException("output_path is required");

			var moduleName = moduleNameObj.ToString() ?? string.Empty;
			var outputPath = outputPathObj.ToString() ?? string.Empty;

			bool fixLayout = true;
			if (arguments.TryGetValue("try_fix_pe_layout", out var fixObj)) {
				if (fixObj is bool fb) fixLayout = fb;
				else if (fixObj is JsonElement fe) fixLayout = fe.ValueKind == JsonValueKind.True;
				else if (fixObj?.ToString()?.ToLowerInvariant() == "false") fixLayout = false;
			}

			int? filterPid = null;
			if (arguments.TryGetValue("process_id", out var pidObj) && pidObj is JsonElement pidElem && pidElem.TryGetInt32(out var pidInt))
				filterPid = pidInt;

			var mgr = dbgManager.Value;
			if (!mgr.IsDebugging)
				throw new InvalidOperationException("Debugger is not active.");

			var (targetModule, targetRuntime) = FindModule(mgr, moduleName, filterPid);
			if (targetModule == null)
				throw new ArgumentException($"Module '{moduleName}' not found. Use list_runtime_modules.");

			var outDir = Path.GetDirectoryName(outputPath);
			if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

			// Strategy A: IDbgDotNetRuntime.GetRawModuleBytes (best quality for .NET)
			if (targetRuntime?.InternalRuntime is IDbgDotNetRuntime dotNetRuntime) {
				try {
					var rawData = dotNetRuntime.GetRawModuleBytes(targetModule);
					if (rawData.RawBytes != null && rawData.RawBytes.Length > 0) {
						File.WriteAllBytes(outputPath, rawData.RawBytes);
						var j = JsonSerializer.Serialize(new {
							Success = true, Module = targetModule.Name, OutputPath = outputPath,
							SizeBytes = rawData.RawBytes.Length, IsFileLayout = rawData.IsFileLayout,
							Method = "IDbgDotNetRuntime.GetRawModuleBytes",
							Note = "High-quality .NET module bytes. Ready to load in dnSpy."
						}, new JsonSerializerOptions { WriteIndented = true });
						return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = j } } };
					}
				}
				catch (Exception ex) {
					McpLogger.Exception(ex, $"GetRawModuleBytes failed for {moduleName}, falling back to ReadMemory");
				}
			}

			// Strategy B: ReadMemory + optional PE layout fix
			if (!targetModule.HasAddress)
				throw new InvalidOperationException($"Module '{moduleName}' has no mapped address.");

			int moduleSize = (int)targetModule.Size;
			if (moduleSize <= 0 || moduleSize > 512 * 1024 * 1024)
				throw new InvalidOperationException($"Module size out of safe range: {moduleSize:N0} bytes.");

			var bytes = targetModule.Process.ReadMemory(targetModule.Address, moduleSize);
			bool isMemoryLayout = targetModule.ImageLayout == DbgImageLayout.Memory;
			string method;
			int finalSize;

			if (fixLayout && isMemoryLayout) {
				var fixedBytes = TryConvertMemoryToFileLayout(bytes, out int fixedSize);
				if (fixedBytes != null) {
					File.WriteAllBytes(outputPath, fixedBytes.Take(fixedSize).ToArray());
					method = "ReadMemory+PELayoutFix";
					finalSize = fixedSize;
				}
				else {
					File.WriteAllBytes(outputPath, bytes);
					method = "ReadMemory (PE fix failed, raw dump)";
					finalSize = bytes.Length;
				}
			}
			else {
				File.WriteAllBytes(outputPath, bytes);
				method = "ReadMemory (raw)";
				finalSize = bytes.Length;
			}

			var result = JsonSerializer.Serialize(new {
				Success = true, Module = targetModule.Name, OutputPath = outputPath,
				OriginalSize = bytes.Length, FileLayoutSize = finalSize,
				IsFileLayout = fixLayout && isMemoryLayout,
				Method = method,
				Warning = isMemoryLayout && !fixLayout
					? "Memory layout dump. Use a PE fixer (LordPE, CFF Explorer) before analysis."
					: null
			}, new JsonSerializerOptions {
				WriteIndented = true,
				DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
			});

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = result } }
			};
		}

		// ── dump_memory_to_file ───────────────────────────────────────────────────

		/// <summary>
		/// Saves a raw memory range from the debugged process to a file.
		/// Complement of read_process_memory (which returns hex/base64 but has 64KB limit).
		/// Arguments: address (hex/dec), size (up to 256MB), output_path (required),
		///            process_id (int, optional)
		/// </summary>
		public CallToolResult DumpMemoryToFile(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("address", out var addressObj))
				throw new ArgumentException("address is required");
			if (!arguments.TryGetValue("size", out var sizeObj))
				throw new ArgumentException("size is required");
			if (!arguments.TryGetValue("output_path", out var outputPathObj))
				throw new ArgumentException("output_path is required");

			var addressStr = (addressObj?.ToString() ?? string.Empty).Trim();
			if (!TryParseAddress(addressStr, out ulong address))
				throw new ArgumentException($"Invalid address '{addressStr}'.");

			int size = 0;
			if (sizeObj is JsonElement sizeElem) sizeElem.TryGetInt32(out size);
			else int.TryParse(sizeObj?.ToString(), out size);
			if (size <= 0 || size > 256 * 1024 * 1024)
				throw new ArgumentException($"size must be 1..268435456 (256 MB), got {size}.");

			var outputPath = outputPathObj?.ToString() ?? string.Empty;
			if (string.IsNullOrWhiteSpace(outputPath))
				throw new ArgumentException("output_path must not be empty.");

			int? filterPid = null;
			if (arguments.TryGetValue("process_id", out var pidObj) && pidObj is JsonElement pidElem && pidElem.TryGetInt32(out var pidInt))
				filterPid = pidInt;

			var mgr = dbgManager.Value;
			if (!mgr.IsDebugging)
				throw new InvalidOperationException("Debugger is not active.");

			DbgProcess? process = filterPid.HasValue
				? mgr.Processes.FirstOrDefault(p => p.Id == filterPid.Value)
				: mgr.Processes.FirstOrDefault(p => p.State == DbgProcessState.Paused)
				  ?? mgr.Processes.FirstOrDefault();

			if (process == null)
				throw new InvalidOperationException("No debugged process found.");

			var bytes = process.ReadMemory(address, size);

			var dir = Path.GetDirectoryName(outputPath);
			if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
			File.WriteAllBytes(outputPath, bytes);

			var json = JsonSerializer.Serialize(new {
				ProcessId = process.Id,
				ProcessName = process.Name,
				Address = $"0x{address:X16}",
				SizeRequested = size,
				SizeRead = bytes.Length,
				OutputPath = outputPath,
				Note = bytes.Length < size ? "Partial read — some pages may be inaccessible." : null
			}, new JsonSerializerOptions {
				WriteIndented = true,
				DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
			});

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = json } }
			};
		}

		// ── Helpers ──────────────────────────────────────────────────────────────

		// ── unpack_from_memory ───────────────────────────────────────────────────

		/// <summary>
		/// All-in-one unpack: launches the EXE under the debugger with BreakKind=EntryPoint
		/// (so the module .cctor/decryptor has already run), waits until paused, dumps the
		/// main module with PE-layout fix, and optionally stops the session.
		/// Arguments: exe_path* | output_path* | timeout_ms (default 30000) |
		///            stop_after_dump (default true) | module_name (auto-detected if omitted)
		/// </summary>
		public CallToolResult UnpackFromMemory(Dictionary<string, object>? arguments) {
			if (arguments == null)
				throw new ArgumentException("Arguments required");
			if (!arguments.TryGetValue("exe_path", out var exePathObj))
				throw new ArgumentException("exe_path is required");
			if (!arguments.TryGetValue("output_path", out var outputPathObj))
				throw new ArgumentException("output_path is required");

			var exePath    = exePathObj.ToString()    ?? string.Empty;
			var outputPath = outputPathObj.ToString() ?? string.Empty;

			if (!File.Exists(exePath))
				throw new ArgumentException($"File not found: {exePath}");
			if (string.IsNullOrWhiteSpace(outputPath))
				throw new ArgumentException("output_path must not be empty");

			int timeoutMs = 30000;
			if (arguments.TryGetValue("timeout_ms", out var toObj) && toObj is JsonElement toElem && toElem.TryGetInt32(out var toInt))
				timeoutMs = Math.Max(3000, toInt);

			bool stopAfterDump = true;
			if (arguments.TryGetValue("stop_after_dump", out var sadObj)) {
				if (sadObj is bool sadBool) stopAfterDump = sadBool;
				else if (sadObj is JsonElement sadElem) stopAfterDump = sadElem.ValueKind != JsonValueKind.False;
				else if (sadObj?.ToString()?.ToLowerInvariant() == "false") stopAfterDump = false;
			}

			string? moduleName = null;
			if (arguments.TryGetValue("module_name", out var mnObj))
				moduleName = mnObj?.ToString();

			var mgr = dbgManager.Value;
			var sw  = System.Diagnostics.Stopwatch.StartNew();

			// Launch under debugger if no session is active
			bool ownedSession = false;
			if (!mgr.IsDebugging) {
				var workDir = Path.GetDirectoryName(exePath) ?? Directory.GetCurrentDirectory();
				var opts = new DotNetFrameworkStartDebuggingOptions {
					Filename         = exePath,
					WorkingDirectory = workDir,
					BreakKind        = PredefinedBreakKinds.EntryPoint,
				};
				var launchError = mgr.Start(opts);
				if (launchError != null)
					throw new InvalidOperationException($"Failed to launch debugger: {launchError}");
				ownedSession = true;
			}

			// Poll until the process pauses at the entry point
			bool hadProcess = false;
			var  deadline   = DateTime.UtcNow.AddMilliseconds(timeoutMs);
			while (DateTime.UtcNow < deadline) {
				Thread.Sleep(200);
				var procs = mgr.Processes;
				if (procs.Length > 0 && mgr.IsRunning == false)
					break; // paused ✓
				if (procs.Length == 0 && hadProcess)
					throw new InvalidOperationException(
						"Process exited before reaching the entry point. " +
						"The target may have anti-debug protection or crashed on startup.");
				if (procs.Length > 0)
					hadProcess = true;
			}
			if (mgr.IsRunning != false || mgr.Processes.Length == 0)
				throw new TimeoutException(
					$"Timed out after {timeoutMs}ms waiting for the process to pause at entry point.");

			// Locate the target module
			var searchName = !string.IsNullOrEmpty(moduleName)
				? moduleName!
				: Path.GetFileName(exePath);
			var (targetModule, targetRuntime) = FindModule(mgr, searchName, null);

			if (targetModule == null) {
				// Fallback: find the sole exe module across all runtimes of the first process
				var exeMods = mgr.Processes[0].Runtimes
					.SelectMany(r => r.Modules.Select(m => (mod: m, rt: r)))
					.Where(t => t.mod.IsExe)
					.ToArray();
				if (exeMods.Length == 1) {
					targetModule  = exeMods[0].mod;
					targetRuntime = exeMods[0].rt;
				}
				else {
					var names = string.Join(", ", exeMods.Select(t => t.mod.Name));
					throw new ArgumentException(
						$"Module '{searchName}' not found. Loaded exe modules: {names}. " +
						"Pass module_name explicitly to disambiguate.");
				}
			}

			// Ensure output directory exists
			var outDir = Path.GetDirectoryName(outputPath);
			if (!string.IsNullOrEmpty(outDir))
				Directory.CreateDirectory(outDir);

			// Dump with the best available strategy
			var (dumpMethod, fileSize) = DumpModuleBytesToPath(targetModule, targetRuntime, outputPath);

			sw.Stop();

			// Optionally stop the debug session we started
			bool stopped = false;
			if (stopAfterDump && ownedSession) {
				try {
					mgr.StopDebuggingAll();
					stopped = true;
				}
				catch (Exception ex) {
					McpLogger.Exception(ex, "StopDebuggingAll failed after unpack");
				}
			}

			var result = JsonSerializer.Serialize(new {
				ExePath       = exePath,
				OutputPath    = outputPath,
				ModuleName    = targetModule.Name,
				FileSizeBytes = fileSize,
				Method        = dumpMethod,
				ElapsedMs     = (int)sw.ElapsedMilliseconds,
				Stopped       = stopped
			}, new JsonSerializerOptions { WriteIndented = true });

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = result } }
			};
		}

		/// <summary>Dump a module to a file using the best available strategy. Returns (method, size).</summary>
		(string method, int size) DumpModuleBytesToPath(DbgModule module, DbgRuntime? runtime, string outputPath) {
			// Strategy A: IDbgDotNetRuntime.GetRawModuleBytes — highest-quality .NET dump
			if (runtime?.InternalRuntime is IDbgDotNetRuntime dotNetRuntime) {
				try {
					var rawData = dotNetRuntime.GetRawModuleBytes(module);
					if (rawData.RawBytes != null && rawData.RawBytes.Length > 0) {
						File.WriteAllBytes(outputPath, rawData.RawBytes);
						return ("IDbgDotNetRuntime.GetRawModuleBytes", rawData.RawBytes.Length);
					}
				}
				catch (Exception ex) {
					McpLogger.Exception(ex, $"GetRawModuleBytes failed for {module.Name}, falling back to ReadMemory");
				}
			}

			// Strategy B: ReadMemory + optional PE layout fix
			if (!module.HasAddress)
				throw new InvalidOperationException(
					$"Module '{module.Name}' has no mapped address and GetRawModuleBytes returned nothing.");

			int moduleSize = (int)module.Size;
			if (moduleSize <= 0 || moduleSize > 512 * 1024 * 1024)
				throw new InvalidOperationException($"Module size out of safe range: {moduleSize:N0} bytes.");

			var bytes = module.Process.ReadMemory(module.Address, moduleSize);

			if (module.ImageLayout == DbgImageLayout.Memory) {
				var fixedBytes = TryConvertMemoryToFileLayout(bytes, out int fixedSize);
				if (fixedBytes != null) {
					File.WriteAllBytes(outputPath, fixedBytes.Take(fixedSize).ToArray());
					return ("ReadMemory+PELayoutFix", fixedSize);
				}
			}

			File.WriteAllBytes(outputPath, bytes);
			return ("ReadMemory", bytes.Length);
		}

		static System.Text.RegularExpressions.Regex BuildPatternRegex(string pattern) {
			// If the pattern contains regex metacharacters beyond simple wildcards, treat as regex
			bool isRegex = pattern.IndexOfAny(new[] { '^', '$', '[', '(', '|', '+', '{' }) >= 0;
			if (isRegex) {
				return new System.Text.RegularExpressions.Regex(
					pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase |
							 System.Text.RegularExpressions.RegexOptions.CultureInvariant);
			}
			// Treat as glob: * → .*, ? → .
			var escaped = System.Text.RegularExpressions.Regex.Escape(pattern)
				.Replace(@"\*", ".*")
				.Replace(@"\?", ".");
			return new System.Text.RegularExpressions.Regex(
				"^" + escaped + "$",
				System.Text.RegularExpressions.RegexOptions.IgnoreCase |
				System.Text.RegularExpressions.RegexOptions.CultureInvariant);
		}

		static Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcessForMemoryPatch(int processId) {
			const uint desiredAccess = PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE;
			var handle = OpenProcess(desiredAccess, false, processId);
			if (handle.IsInvalid)
				throw new InvalidOperationException($"OpenProcess failed for PID {processId}. Win32 error: {Marshal.GetLastWin32Error()}");
			return handle;
		}

		static uint TemporarilyEnableWritableProtection(IntPtr processHandle, ulong address, int size) {
			if (!VirtualProtectEx(processHandle, new IntPtr(unchecked((long)address)), new UIntPtr((uint)size), PAGE_EXECUTE_READWRITE, out var originalProtect))
				throw new InvalidOperationException($"VirtualProtectEx failed at 0x{address:X16}. Win32 error: {Marshal.GetLastWin32Error()}");
			return originalProtect;
		}

		static void RestoreMemoryProtection(IntPtr processHandle, ulong address, int size, uint originalProtect) {
			if (!VirtualProtectEx(processHandle, new IntPtr(unchecked((long)address)), new UIntPtr((uint)size), originalProtect, out _))
				throw new InvalidOperationException($"VirtualProtectEx restore failed at 0x{address:X16}. Win32 error: {Marshal.GetLastWin32Error()}");
		}

		const uint PROCESS_VM_OPERATION = 0x0008;
		const uint PROCESS_VM_READ = 0x0010;
		const uint PROCESS_VM_WRITE = 0x0020;
		const uint PAGE_EXECUTE_READWRITE = 0x40;

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

		static byte[] ParseHexBytes(string hex) {
			// Accept "90 90 FF", "9090FF", "0x90, 0x90", "90-90-FF", etc.
			// net48: Replace(string, string, StringComparison) not available — strip both cases
			hex = hex
				.Replace("0x", "").Replace("0X", "")
				.Replace(" ", "").Replace(",", "").Replace("-", "");
			if (hex.Length % 2 != 0)
				throw new ArgumentException($"hex_bytes has odd length after normalization: '{hex}'");
			var result = new byte[hex.Length / 2];
			for (int i = 0; i < result.Length; i++)
				result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
			return result;
		}

		static bool TryParseAddress(string s, out ulong value) {
			if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				return ulong.TryParse(s.Substring(2),
					System.Globalization.NumberStyles.HexNumber,
					System.Globalization.CultureInfo.InvariantCulture, out value);
			if (ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber,
					System.Globalization.CultureInfo.InvariantCulture, out value))
				return true;
			return ulong.TryParse(s, out value);
		}

		static string BuildHexDump(byte[] bytes, ulong baseAddress) {
			const int rowWidth = 16;
			var sb = new StringBuilder(bytes.Length * 4);
			for (int i = 0; i < bytes.Length; i += rowWidth) {
				sb.Append($"{baseAddress + (ulong)i:X16}  ");
				int len = Math.Min(rowWidth, bytes.Length - i);
				for (int j = 0; j < len; j++) {
					sb.Append($"{bytes[i + j]:X2} ");
					if (j == 7) sb.Append(' ');
				}
				for (int j = len; j < rowWidth; j++) {
					sb.Append("   ");
					if (j == 7) sb.Append(' ');
				}
				sb.Append(" |");
				for (int j = 0; j < len; j++) {
					char c = (char)bytes[i + j];
					sb.Append(c >= 32 && c < 127 ? c : '.');
				}
				sb.AppendLine("|");
			}
			return sb.ToString().TrimEnd();
		}

		/// <summary>Helper: find a DbgModule by name across all processes/runtimes.</summary>
		(DbgModule? module, DbgRuntime? runtime) FindModule(DbgManager mgr, string moduleName, int? filterPid) {
			foreach (var process in mgr.Processes) {
				if (filterPid.HasValue && process.Id != filterPid.Value) continue;
				foreach (var runtime in process.Runtimes) {
					var found = runtime.Modules.FirstOrDefault(m =>
						(m.Name ?? string.Empty).Equals(moduleName, StringComparison.OrdinalIgnoreCase) ||
						(m.Filename ?? string.Empty).Equals(moduleName, StringComparison.OrdinalIgnoreCase) ||
						Path.GetFileName(m.Filename ?? string.Empty).Equals(moduleName, StringComparison.OrdinalIgnoreCase));
					if (found != null) return (found, runtime);
				}
			}
			return (null, null);
		}

		/// <summary>
		/// Attempts to convert a memory-layout PE dump to a file-layout PE.
		/// Copies section data from their virtual addresses to their raw file offsets.
		/// Returns null if conversion fails (malformed PE / no sections).
		/// </summary>
		static byte[]? TryConvertMemoryToFileLayout(byte[] memBytes, out int finalSize) {
			finalSize = 0;
			try {
				using var peImage = new dnlib.PE.PEImage(memBytes, dnlib.PE.ImageLayout.Memory, false);

				var sections = peImage.ImageSectionHeaders;
				if (sections == null || sections.Count == 0) return null;

				// Calculate required output size: max(PointerToRawData + SizeOfRawData)
				uint sizeOfHeaders = peImage.ImageNTHeaders?.OptionalHeader?.SizeOfHeaders ?? 0x400;
				uint maxRawEnd = sizeOfHeaders;
				foreach (var s in sections) {
					uint end = s.PointerToRawData + s.SizeOfRawData;
					if (end > maxRawEnd) maxRawEnd = end;
				}

				if (maxRawEnd == 0 || maxRawEnd > 512 * 1024 * 1024u) return null;

				var fileBytes = new byte[maxRawEnd];

				// Copy PE headers
				int hdrCopy = (int)Math.Min(sizeOfHeaders, (uint)memBytes.Length);
				Array.Copy(memBytes, 0, fileBytes, 0, hdrCopy);

				// Copy each section from its virtual address to its file offset
				foreach (var s in sections) {
					uint va = (uint)s.VirtualAddress;
					uint ptr = s.PointerToRawData;
					uint rawSz = s.SizeOfRawData;
					uint virtSz = s.VirtualSize;
					uint copyLen = Math.Min(rawSz, Math.Min(virtSz > 0 ? virtSz : rawSz, (uint)(memBytes.Length - (int)va)));

					if (va + copyLen > memBytes.Length || ptr + copyLen > fileBytes.Length) continue;
					Array.Copy(memBytes, (int)va, fileBytes, (int)ptr, (int)copyLen);
				}

				finalSize = (int)maxRawEnd;
				return fileBytes;
			}
			catch {
				return null;
			}
		}

		static string DescribeSectionCharacteristics(uint ch) {
			var parts = new List<string>();
			if ((ch & 0x00000020) != 0) parts.Add("CODE");
			if ((ch & 0x00000040) != 0) parts.Add("INITIALIZED_DATA");
			if ((ch & 0x00000080) != 0) parts.Add("UNINITIALIZED_DATA");
			if ((ch & 0x02000000) != 0) parts.Add("DISCARDABLE");
			if ((ch & 0x10000000) != 0) parts.Add("SHARED");
			if ((ch & 0x20000000) != 0) parts.Add("EXECUTE");
			if ((ch & 0x40000000) != 0) parts.Add("READ");
			if ((ch & 0x80000000u) != 0) parts.Add("WRITE");
			return parts.Count > 0 ? string.Join("|", parts) : $"0x{ch:X8}";
		}

	// ── dump_cordbg_il ────────────────────────────────────────────────────────

	/// <summary>
	/// For each MethodDef token in the paused module, reads ICorDebugFunction.ILCode.Address
	/// and ILCode.Size via reflection through the private DbgEngineImpl.DbgModuleData.DnModule
	/// chain.  Reports whether the IL address falls inside the PE image (likely the encrypted
	/// stub still on-disk) or outside it (potentially the hook-provided decrypted buffer that
	/// is still in CLR-internal memory).
	/// Arguments: module_name (optional), output_path (optional JSON file), max_methods (int,
	///            default 10000), include_bytes (bool, default false — set true to get base64 IL)
	/// </summary>
	public CallToolResult DumpCordbgIL(Dictionary<string, object>? arguments) {
		string? moduleFilter = null;
		if (arguments != null && arguments.TryGetValue("module_name", out var mnObj))
			moduleFilter = mnObj?.ToString();

		string? outputPath = null;
		if (arguments != null && arguments.TryGetValue("output_path", out var opObj))
			outputPath = opObj?.ToString();

		int maxMethods = 10000;
		if (arguments != null && arguments.TryGetValue("max_methods", out var mmObj) &&
			mmObj is JsonElement mmElem && mmElem.TryGetInt32(out var mmInt))
			maxMethods = Math.Max(1, mmInt);

		bool includeBytes = false;
		if (arguments != null && arguments.TryGetValue("include_bytes", out var ibObj)) {
			if (ibObj is bool ibBool) includeBytes = ibBool;
			else if (ibObj is JsonElement ibElem) includeBytes = ibElem.ValueKind == JsonValueKind.True;
		}

		var mgr = dbgManager.Value;
		if (!mgr.IsDebugging)
			throw new InvalidOperationException("Debugger is not active. Start a debug session first.");

		return System.Windows.Application.Current.Dispatcher.Invoke(() => {
			var process = mgr.Processes.FirstOrDefault(p => p.State == DbgProcessState.Paused)
				?? mgr.Processes.FirstOrDefault()
				?? throw new InvalidOperationException("No debugged process found.");

			// Locate target module
			DbgModule? targetModule = null;
			if (!string.IsNullOrEmpty(moduleFilter)) {
				var (m, _) = FindModule(mgr, moduleFilter!, null);
				targetModule = m;
			}
			if (targetModule == null) {
				// Fallback: first non-dynamic exe module
				targetModule = process.Runtimes
					.SelectMany(r => r.Modules)
					.FirstOrDefault(m => m.IsExe && !m.IsDynamic)
					?? process.Runtimes.SelectMany(r => r.Modules).FirstOrDefault(m => !m.IsDynamic);
			}
			if (targetModule == null)
				throw new InvalidOperationException("No suitable module found. Use module_name to specify.");

			ulong moduleBase = targetModule.HasAddress ? targetModule.Address : 0;
			ulong moduleSize = targetModule.HasAddress ? targetModule.Size : 0;

			// Get DmdModule for type/method enumeration
			var dmdModule = targetModule.GetReflectionModule();
			if (dmdModule == null)
				throw new InvalidOperationException($"Module '{targetModule.Name}' is not a .NET module (no ReflectionModule).");

			// Obtain CorModule via reflection (private DbgEngineImpl+DbgModuleData.DnModule.CorModule)
			object? corModule = null;
			string? corModuleError = null;
			try { corModule = GetCorModuleViaReflection(targetModule); }
			catch (Exception ex) { corModuleError = ex.Message; }

			var methodEntries = new List<object>();
			int totalMethods = 0, inPeCount = 0, outOfPeCount = 0, errorCount = 0, skippedCount = 0;

			foreach (var type in dmdModule.GetTypes()) {
				if (totalMethods >= maxMethods) break;
				foreach (var method in type.DeclaredMethods) {
					if (totalMethods >= maxMethods) { skippedCount++; continue; }

					uint token = (uint)method.MetadataToken;
					if ((token >> 24) != 0x06) continue; // only MethodDef
					totalMethods++;

					if (corModule == null) { errorCount++; continue; }

					try {
						var ilInfo = GetILCodeInfoViaReflection(corModule, token);
						if (ilInfo == null) { errorCount++; continue; }

						var (ilAddr, ilSize) = ilInfo.Value;
						if (ilAddr == 0) { errorCount++; continue; }

						bool isInPE = moduleBase != 0 && ilAddr >= moduleBase && ilAddr < moduleBase + moduleSize;
						if (isInPE) inPeCount++; else outOfPeCount++;

						// Peek at first bytes to check if IL looks valid or encrypted
						int peekSize = (int)Math.Min(ilSize, 32u);
						byte[]? peekBytes = null;
						if (peekSize > 0) {
							try { peekBytes = process.ReadMemory(ilAddr, peekSize); }
							catch { /* ignore read failures */ }
						}

						// Optionally read the full method body (header included, from addr-12)
						string? fullBytesB64 = null;
						if (includeBytes && ilSize > 0 && ilSize < 512 * 1024u) {
							try {
								ulong startAddr = ilAddr >= 12 ? ilAddr - 12 : ilAddr;
								int fullLen = (int)Math.Min(ilSize + 12u, 524300u);
								var fullBytes = process.ReadMemory(startAddr, fullLen);
								fullBytesB64 = Convert.ToBase64String(fullBytes);
							}
							catch { /* ignore */ }
						}

						methodEntries.Add(new {
							Token       = $"0x{token:X8}",
							Type        = type.Name,
							Method      = method.Name,
							ILAddress   = $"0x{ilAddr:X16}",
							ILSize      = ilSize,
							IsInPEImage = isInPE,
							PeekHex     = peekBytes != null
								? BitConverter.ToString(peekBytes).Replace("-", " ")
								: null,
							FullBytesBase64 = fullBytesB64
						});
					}
					catch (Exception ex) {
						errorCount++;
						var actualEx = ex is TargetInvocationException tie ? tie.InnerException ?? tie : ex;
						methodEntries.Add(new {
							Token  = $"0x{token:X8}",
							Type   = type.Name,
							Method = method.Name,
							Error  = $"{actualEx.GetType().Name}: {actualEx.Message}"
						});
					}
				}
			}

			var summaryObj = new {
				Module              = targetModule.Name,
				ModuleBase          = moduleBase != 0 ? $"0x{moduleBase:X16}" : "N/A",
				ModuleSizeBytes     = moduleSize,
				TotalMethodsScanned = totalMethods,
				InPEImage           = inPeCount,
				OutsidePEImage      = outOfPeCount,
				Errors              = errorCount,
				Skipped             = skippedCount,
				CorModuleAccessError = corModuleError,
				Note = outOfPeCount > 0
					? $"{outOfPeCount} method(s) have IL addresses outside the PE image — these may be hook-decrypted buffers still in CLR-internal memory."
					: "All scanned methods have IL inside the PE image (address points to mapped module, possibly encrypted stubs).",
				Methods = methodEntries
			};

			var json = JsonSerializer.Serialize(summaryObj, new JsonSerializerOptions {
				WriteIndented = true,
				DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
			});

			if (!string.IsNullOrWhiteSpace(outputPath)) {
				var dir = Path.GetDirectoryName(outputPath);
				if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
				File.WriteAllText(outputPath, json);
			}

			// Truncate inline response to stay manageable
			const int inlineLimit = 50;
			if (methodEntries.Count > inlineLimit) {
				var trimmedObj = new {
					summaryObj.Module, summaryObj.ModuleBase, summaryObj.ModuleSizeBytes,
					summaryObj.TotalMethodsScanned, summaryObj.InPEImage, summaryObj.OutsidePEImage,
					summaryObj.Errors, summaryObj.Skipped, summaryObj.CorModuleAccessError,
					summaryObj.Note,
					OutputPath = outputPath,
					MethodsSample    = methodEntries.Take(inlineLimit).ToList(),
					TruncatedAt      = inlineLimit,
					HintSeeFile      = !string.IsNullOrWhiteSpace(outputPath)
						? $"Full results saved to {outputPath}"
						: "Pass output_path to save full results to disk."
				};
				json = JsonSerializer.Serialize(trimmedObj, new JsonSerializerOptions {
					WriteIndented = true,
					DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
				});
			}

			return new CallToolResult {
				Content = new List<ToolContent> { new ToolContent { Text = json } }
			};
		});
	}

	/// <summary>Navigate DbgModule → private DbgEngineImpl+DbgModuleData → DnModule → CorModule
	/// using reflection, since DbgModuleData is a private nested class.</summary>
	static object GetCorModuleViaReflection(DbgModule dbgModule) {
		// 1. Find DbgModuleData type (private nested class of DbgEngineImpl)
		Type? moduleDataType = null;
		foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
			moduleDataType = asm.GetType(
				"dnSpy.Debugger.DotNet.CorDebug.Impl.DbgEngineImpl+DbgModuleData",
				throwOnError: false, ignoreCase: false);
			if (moduleDataType != null) break;
		}
		if (moduleDataType == null)
			throw new InvalidOperationException(
				"Type DbgEngineImpl+DbgModuleData not found — is the CorDebug debugger extension loaded?");

		// 2. Find TryGetData<T>(out T?) traversing the type hierarchy
		MethodInfo? tryGetDataDef = null;
		for (var t = dbgModule.GetType(); t != null && tryGetDataDef == null; t = t.BaseType) {
			foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
				if (m.Name == "TryGetData" && m.IsGenericMethodDefinition &&
					m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 1) {
					tryGetDataDef = m;
					break;
				}
			}
		}
		if (tryGetDataDef == null)
			throw new InvalidOperationException("TryGetData<T> method not found on DbgModule hierarchy.");

		// 3. Invoke TryGetData<DbgModuleData>(out DbgModuleData? data)
		var genericMethod = tryGetDataDef.MakeGenericMethod(moduleDataType);
		var invokeArgs   = new object?[] { null };
		var found        = (bool)(genericMethod.Invoke(dbgModule, invokeArgs) ?? false);
		if (!found || invokeArgs[0] == null)
			throw new InvalidOperationException(
				"TryGetData<DbgModuleData> returned false — module may not be a CorDebug-managed module.");

		var moduleData = invokeArgs[0]!;

		// 4. Get DnModule property from DbgModuleData
		var dnModuleProp = moduleDataType.GetProperty("DnModule", BindingFlags.Public | BindingFlags.Instance);
		var dnModule     = dnModuleProp?.GetValue(moduleData)
			?? throw new InvalidOperationException("DbgModuleData.DnModule is null.");

		// 5. Get CorModule property from DnModule
		var corModuleProp = dnModule.GetType().GetProperty("CorModule", BindingFlags.Public | BindingFlags.Instance);
		return corModuleProp?.GetValue(dnModule)
			?? throw new InvalidOperationException("DnModule.CorModule is null.");
	}

	/// <summary>Call CorModule.GetFunctionFromToken(token).ILCode and return (Address, Size).
	/// Unwraps TargetInvocationException so callers see the real error.</summary>
	static (ulong address, uint size)? GetILCodeInfoViaReflection(object corModule, uint mdToken) {
		var getFuncMethod = corModule.GetType().GetMethod("GetFunctionFromToken",
			BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(uint) }, null);
		if (getFuncMethod == null) throw new InvalidOperationException("GetFunctionFromToken method not found on CorModule type.");

		object? corFunc;
		try { corFunc = getFuncMethod.Invoke(corModule, new object[] { mdToken }); }
		catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }

		if (corFunc == null) return null; // method not found in this module (abstract, extern, etc.)

		var ilCodeProp = corFunc.GetType().GetProperty("ILCode", BindingFlags.Public | BindingFlags.Instance);
		object? ilCode;
		try { ilCode = ilCodeProp?.GetValue(corFunc); }
		catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
		if (ilCode == null) return null;

		var addrProp = ilCode.GetType().GetProperty("Address", BindingFlags.Public | BindingFlags.Instance);
		var sizeProp = ilCode.GetType().GetProperty("Size",    BindingFlags.Public | BindingFlags.Instance);
		if (addrProp == null || sizeProp == null) return null;

		ulong addr = (ulong)(addrProp.GetValue(ilCode) ?? 0UL);
		uint  size = (uint) (sizeProp.GetValue(ilCode) ?? 0U);
		return (addr, size);
	}
	}
}
