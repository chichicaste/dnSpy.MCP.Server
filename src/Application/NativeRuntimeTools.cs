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
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Iced.Intel;
using dnSpy.Contracts.Debugger;
using dnSpy.MCP.Server.Contracts;

namespace dnSpy.MCP.Server.Application {
	[Export(typeof(NativeRuntimeTools))]
	public sealed class NativeRuntimeTools {
		readonly Lazy<DbgManager> dbgManager;

		static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions {
			WriteIndented = true,
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
		};
		static readonly ConcurrentDictionary<string, NativePatchRecord> ActivePatches = new ConcurrentDictionary<string, NativePatchRecord>(StringComparer.OrdinalIgnoreCase);
		static readonly ConcurrentDictionary<string, int> ManagedFrozenThreads = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		[ImportingConstructor]
		public NativeRuntimeTools(Lazy<DbgManager> dbgManager) => this.dbgManager = dbgManager;

		public CallToolResult GetProcAddress(Dictionary<string, object>? arguments) {
			var (module, process) = ResolveModule(arguments);
			if (arguments == null || !arguments.TryGetValue("function", out var functionObj))
				throw new ArgumentException("function is required");

			var functionName = functionObj?.ToString() ?? string.Empty;
			if (string.IsNullOrWhiteSpace(functionName))
				throw new ArgumentException("function must not be empty");

			var export = GetExport(module.Filename, functionName);
			var json = JsonSerializer.Serialize(new {
				ProcessId = process.Id,
				ProcessName = process.Name,
				ModuleName = module.Name,
				ModulePath = module.Filename,
				Function = functionName,
				Ordinal = export.Ordinal,
				RVA = $"0x{export.Rva:X8}",
				Address = module.HasAddress ? $"0x{module.Address + export.Rva:X16}" : null,
				Note = module.HasAddress ? null : "Module has no mapped base address; only RVA is available."
			}, JsonOpts);

			return JsonResult(json);
		}

		public CallToolResult PatchNativeFunction(Dictionary<string, object>? arguments) {
			var (module, process) = ResolveModule(arguments);
			if (arguments == null || !arguments.TryGetValue("function", out var functionObj))
				throw new ArgumentException("function is required");
			if (!module.HasAddress)
				throw new InvalidOperationException($"Module '{module.Name}' has no mapped base address.");

			var functionName = functionObj?.ToString() ?? string.Empty;
			if (string.IsNullOrWhiteSpace(functionName))
				throw new ArgumentException("function must not be empty");

			var patchBytes = ReadPatchBytes(arguments);
			bool autoVirtualProtect = GetBoolean(arguments, "auto_virtual_protect", defaultValue: true);

			var export = GetExport(module.Filename, functionName);
			var address = module.Address + export.Rva;
			var originalBytes = process.ReadMemory(address, patchBytes.Length);
			uint? originalProtect = null;

			if (autoVirtualProtect) {
				using var handle = OpenProcessForPatch(process.Id);
				originalProtect = VirtualProtect(handle.DangerousGetHandle(), address, patchBytes.Length, PAGE_EXECUTE_READWRITE);
				try {
					process.WriteMemory(address, patchBytes);
				}
				finally {
					RestoreProtect(handle.DangerousGetHandle(), address, patchBytes.Length, originalProtect.Value);
				}
			}
			else {
				process.WriteMemory(address, patchBytes);
			}

			var patchId = Guid.NewGuid().ToString("N");
			ActivePatches[patchId] = new NativePatchRecord(
				patchId,
				process.Id,
				process.Name,
				module.Name,
				module.Filename,
				functionName,
				address,
				originalBytes,
				patchBytes,
				DateTimeOffset.UtcNow);

			var json = JsonSerializer.Serialize(new {
				PatchId = patchId,
				ProcessId = process.Id,
				ProcessName = process.Name,
				ModuleName = module.Name,
				ModulePath = module.Filename,
				Function = functionName,
				Ordinal = export.Ordinal,
				Address = $"0x{address:X16}",
				OriginalBytes = ToHex(originalBytes),
				PatchedBytes = ToHex(patchBytes),
				AutoVirtualProtect = autoVirtualProtect,
				OriginalProtection = originalProtect.HasValue ? $"0x{originalProtect.Value:X8}" : null
			}, JsonOpts);

			return JsonResult(json);
		}

		public CallToolResult DisassembleNativeFunction(Dictionary<string, object>? arguments) {
			var (module, process) = ResolveModule(arguments);
			if (arguments == null || !arguments.TryGetValue("function", out var functionObj))
				throw new ArgumentException("function is required");
			if (!module.HasAddress)
				throw new InvalidOperationException($"Module '{module.Name}' has no mapped base address.");

			var functionName = functionObj?.ToString() ?? string.Empty;
			if (string.IsNullOrWhiteSpace(functionName))
				throw new ArgumentException("function must not be empty");

			var export = GetExport(module.Filename, functionName);
			var address = module.Address + export.Rva;
			int size = RequireInt(arguments, "size", minValue: 1, maxValue: 0x4000, defaultValue: 128);
			var bytes = process.ReadMemory(address, size);
			var instructions = Disassemble(bytes, address, process.Bitness);

			var json = JsonSerializer.Serialize(new {
				ProcessId = process.Id,
				ProcessName = process.Name,
				ModuleName = module.Name,
				ModulePath = module.Filename,
				Function = functionName,
				Ordinal = export.Ordinal,
				Address = $"0x{address:X16}",
				Size = size,
				Bitness = process.Bitness,
				Instructions = instructions
			}, JsonOpts);
			return JsonResult(json);
		}

		public CallToolResult InjectNativeDll(Dictionary<string, object>? arguments) {
			var process = ResolveProcess(arguments);
			var dllPath = RequireString(arguments, "dll_path");
			if (!File.Exists(dllPath))
				throw new ArgumentException($"DLL not found: {dllPath}");

			using var handle = OpenProcessForInjection(process.Id);
			var loadLibraryAddress = ResolveKernel32Export("LoadLibraryW");
			var dllBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
			var remoteBuffer = VirtualAllocRemote(handle.DangerousGetHandle(), dllBytes.Length, PAGE_READWRITE);
			WriteRemoteBytes(handle.DangerousGetHandle(), remoteBuffer, dllBytes);
			var remoteThread = CreateRemoteThreadChecked(handle.DangerousGetHandle(), loadLibraryAddress, remoteBuffer);
			WaitForSingleObject(remoteThread, INFINITE);
			GetExitCodeThreadChecked(remoteThread, out var exitCode);
			CloseHandle(remoteThread);

			var json = JsonSerializer.Serialize(new {
				ProcessId = process.Id,
				ProcessName = process.Name,
				DllPath = dllPath,
				LoadLibraryW = $"0x{loadLibraryAddress.ToInt64():X16}",
				RemoteBuffer = $"0x{remoteBuffer.ToInt64():X16}",
				RemoteThread = $"0x{remoteThread.ToInt64():X16}",
				ModuleHandle = $"0x{exitCode.ToInt64():X16}"
			}, JsonOpts);
			return JsonResult(json);
		}

		public CallToolResult InjectManagedDll(Dictionary<string, object>? arguments) {
			var process = ResolveProcess(arguments);
			var runtime = process.Runtimes.FirstOrDefault() ?? throw new InvalidOperationException("The process has no debug runtimes.");
			var runtimeType = GetRuntimeType(runtime);
			var dllPath = RequireString(arguments, "dll_path");
			var typeName = RequireString(arguments, "type_name");
			var methodName = RequireString(arguments, "method_name");
			var argument = GetString(arguments, "argument");
			var copyToTemp = GetBoolean(arguments, "copy_to_temp", defaultValue: false);

			if (!File.Exists(dllPath))
				throw new ArgumentException($"DLL not found: {dllPath}");

			var effectivePath = copyToTemp ? CopyInjectedDllToTemp(dllPath) : dllPath;
			try {
				var injectionResult = runtimeType switch {
					ManagedRuntimeType.FrameworkV2 => InjectFrameworkManagedDll(process, effectivePath, typeName, methodName, argument, "v2.0.50727"),
					ManagedRuntimeType.FrameworkV4 => InjectFrameworkManagedDll(process, effectivePath, typeName, methodName, argument, "v4.0.30319"),
					ManagedRuntimeType.Unity => InjectUnityManagedDll(process, effectivePath, typeName, methodName, argument),
					_ => throw new InvalidOperationException($"Managed injection is not supported for runtime '{runtime.Name}'.")
				};

				var json = JsonSerializer.Serialize(new {
					ProcessId = process.Id,
					ProcessName = process.Name,
					Runtime = runtime.Name,
					InjectionRuntime = runtimeType.ToString(),
					DllPath = dllPath,
					EffectiveDllPath = effectivePath,
					TypeName = typeName,
					MethodName = methodName,
					Argument = argument,
					Result = injectionResult
				}, JsonOpts);
				return JsonResult(json);
			}
			finally {
				if (copyToTemp && !string.Equals(effectivePath, dllPath, StringComparison.OrdinalIgnoreCase)) {
					try { File.Delete(effectivePath); } catch { }
				}
			}
		}

		public CallToolResult RevertPatch(Dictionary<string, object>? arguments) {
			var patchId = RequireString(arguments, "patch_id");
			if (!ActivePatches.TryGetValue(patchId, out var patch))
				throw new ArgumentException($"Patch '{patchId}' not found.");

			var process = ResolveProcessById(patch.ProcessId);
			uint? originalProtect = null;
			bool autoVirtualProtect = GetBoolean(arguments, "auto_virtual_protect", defaultValue: true);

			if (autoVirtualProtect) {
				using var handle = OpenProcessForPatch(process.Id);
				originalProtect = VirtualProtect(handle.DangerousGetHandle(), patch.Address, patch.OriginalBytes.Length, PAGE_EXECUTE_READWRITE);
				try {
					process.WriteMemory(patch.Address, patch.OriginalBytes);
				}
				finally {
					RestoreProtect(handle.DangerousGetHandle(), patch.Address, patch.OriginalBytes.Length, originalProtect.Value);
				}
			}
			else {
				process.WriteMemory(patch.Address, patch.OriginalBytes);
			}

			ActivePatches.TryRemove(patchId, out _);
			var json = JsonSerializer.Serialize(new {
				PatchId = patchId,
				Reverted = true,
				ProcessId = process.Id,
				Address = $"0x{patch.Address:X16}",
				RestoredBytes = ToHex(patch.OriginalBytes),
				AutoVirtualProtect = autoVirtualProtect,
				OriginalProtection = originalProtect.HasValue ? $"0x{originalProtect.Value:X8}" : null
			}, JsonOpts);
			return JsonResult(json);
		}

		public CallToolResult ListActivePatches() {
			var patches = ActivePatches.Values
				.OrderBy(p => p.AppliedAtUtc)
				.Select(p => new {
					PatchId = p.PatchId,
					ProcessId = p.ProcessId,
					ProcessName = p.ProcessName,
					ModuleName = p.ModuleName,
					ModulePath = p.ModulePath,
					Function = p.FunctionName,
					Address = $"0x{p.Address:X16}",
					OriginalBytes = ToHex(p.OriginalBytes),
					PatchedBytes = ToHex(p.PatchedBytes),
					AppliedAtUtc = p.AppliedAtUtc
				})
				.ToList();

			var json = JsonSerializer.Serialize(new {
				Count = patches.Count,
				Patches = patches
			}, JsonOpts);
			return JsonResult(json);
		}

		public CallToolResult ReadNativeMemory(Dictionary<string, object>? arguments) {
			var process = ResolveProcess(arguments);
			var address = ParseAddress(arguments, "address");
			var size = RequireInt(arguments, "size", minValue: 1, maxValue: 0x10000);
			var format = GetString(arguments, "format")?.Trim().ToLowerInvariant() ?? "hex";
			var bytes = process.ReadMemory(address, size);

			object payload = format switch {
				"hex" => new {
					ProcessId = process.Id,
					ProcessName = process.Name,
					Address = $"0x{address:X16}",
					Size = size,
					Format = "hex",
					Data = ToHex(bytes)
				},
				"ascii" => new {
					ProcessId = process.Id,
					ProcessName = process.Name,
					Address = $"0x{address:X16}",
					Size = size,
					Format = "ascii",
					Data = ToAscii(bytes),
					Hex = ToHex(bytes)
				},
				"disasm" => new {
					ProcessId = process.Id,
					ProcessName = process.Name,
					Address = $"0x{address:X16}",
					Size = size,
					Format = "disasm",
					Bitness = process.Bitness,
					Instructions = Disassemble(bytes, address, process.Bitness)
				},
				_ => throw new ArgumentException("format must be one of: hex, ascii, disasm")
			};

			return JsonResult(JsonSerializer.Serialize(payload, JsonOpts));
		}

		public CallToolResult SuspendThreads(Dictionary<string, object>? arguments) {
			var process = ResolveProcess(arguments);
			var requestedThreadIds = ParseThreadIds(arguments);
			var threads = process.Threads
				.Where(t => requestedThreadIds.Count == 0 || requestedThreadIds.Contains(t.Id))
				.ToList();
			if (threads.Count == 0)
				throw new ArgumentException("No matching threads found to suspend.");

			var suspended = new List<object>();
			foreach (var thread in threads) {
				thread.Freeze();
				var key = GetThreadKey(process.Id, thread.Id);
				var totalManagedSuspends = ManagedFrozenThreads.AddOrUpdate(key, 1, (_, existing) => existing + 1);
				suspended.Add(new {
					ThreadId = (long)thread.Id,
					ThreadName = thread.Name,
					SuspendedCount = thread.SuspendedCount,
					ManagedSuspends = totalManagedSuspends
				});
			}

			var json = JsonSerializer.Serialize(new {
				ProcessId = process.Id,
				ProcessName = process.Name,
				SuspendedThreadCount = suspended.Count,
				Threads = suspended
			}, JsonOpts);
			return JsonResult(json);
		}

		public CallToolResult ResumeThreads(Dictionary<string, object>? arguments) {
			var process = ResolveProcess(arguments);
			var requestedThreadIds = ParseThreadIds(arguments);
			var threads = process.Threads
				.Where(t => requestedThreadIds.Count == 0 || requestedThreadIds.Contains(t.Id))
				.ToList();
			if (threads.Count == 0)
				throw new ArgumentException("No matching threads found to resume.");

			var resumed = new List<object>();
			foreach (var thread in threads) {
				var key = GetThreadKey(process.Id, thread.Id);
				if (!ManagedFrozenThreads.TryGetValue(key, out var suspendCount) || suspendCount <= 0)
					continue;

				thread.Thaw();
				var remaining = ManagedFrozenThreads.AddOrUpdate(key, 0, (_, existing) => Math.Max(0, existing - 1));
				if (remaining == 0)
					ManagedFrozenThreads.TryRemove(key, out _);

				resumed.Add(new {
					ThreadId = (long)thread.Id,
					ThreadName = thread.Name,
					SuspendedCount = thread.SuspendedCount,
					ManagedSuspendsRemaining = Math.Max(0, remaining)
				});
			}

			var json = JsonSerializer.Serialize(new {
				ProcessId = process.Id,
				ProcessName = process.Name,
				ResumedThreadCount = resumed.Count,
				Threads = resumed,
				Note = resumed.Count == 0 ? "No threads were resumed because they were not suspended through suspend_threads." : null
			}, JsonOpts);
			return JsonResult(json);
		}

		public CallToolResult GetPeb(Dictionary<string, object>? arguments) {
			var process = ResolveProcess(arguments);
			using var handle = OpenProcessForRead(process.Id);
			var pbi = QueryBasicInformation(handle.DangerousGetHandle());
			var pebAddress = unchecked((ulong)pbi.PebBaseAddress.ToInt64());
			var pebBytes = ReadMemoryExact(handle.DangerousGetHandle(), pebAddress, process.Bitness == 64 ? 0x100 : 0x80);

			byte beingDebugged = pebBytes[2];
			uint ntGlobalFlag = process.Bitness == 64
				? BitConverter.ToUInt32(pebBytes, 0xBC)
				: BitConverter.ToUInt32(pebBytes, 0x68);
			ulong processHeap = process.Bitness == 64
				? BitConverter.ToUInt64(pebBytes, 0x30)
				: BitConverter.ToUInt32(pebBytes, 0x18);

			uint? heapFlags = null;
			uint? heapForceFlags = null;
			if (processHeap != 0) {
				try {
					var heapBytes = ReadMemoryExact(handle.DangerousGetHandle(), processHeap, process.Bitness == 64 ? 0x90 : 0x20);
					if (process.Bitness == 64) {
						heapFlags = BitConverter.ToUInt32(heapBytes, 0x70);
						heapForceFlags = BitConverter.ToUInt32(heapBytes, 0x74);
					}
					else {
						heapFlags = BitConverter.ToUInt32(heapBytes, 0x0C);
						heapForceFlags = BitConverter.ToUInt32(heapBytes, 0x10);
					}
				}
				catch {
				}
			}

			var json = JsonSerializer.Serialize(new {
				ProcessId = process.Id,
				ProcessName = process.Name,
				Bitness = process.Bitness,
				PebAddress = $"0x{pebAddress:X16}",
				BeingDebugged = beingDebugged != 0,
				NtGlobalFlag = $"0x{ntGlobalFlag:X8}",
				ProcessHeap = processHeap == 0 ? null : $"0x{processHeap:X16}",
				HeapFlags = heapFlags.HasValue ? $"0x{heapFlags.Value:X8}" : null,
				HeapForceFlags = heapForceFlags.HasValue ? $"0x{heapForceFlags.Value:X8}" : null,
				Note = heapFlags.HasValue ? null : "Heap flags are best-effort and may be unavailable on this target."
			}, JsonOpts);
			return JsonResult(json);
		}

		(DbgModule module, DbgProcess process) ResolveModule(Dictionary<string, object>? arguments) {
			if (arguments == null || !arguments.TryGetValue("module", out var moduleObj) && !arguments.TryGetValue("module_name", out moduleObj))
				throw new ArgumentException("module or module_name is required");

			var moduleName = moduleObj?.ToString() ?? string.Empty;
			if (string.IsNullOrWhiteSpace(moduleName))
				throw new ArgumentException("module must not be empty");

			var filterPid = GetInt(arguments, "process_id");
			foreach (var process in dbgManager.Value.Processes) {
				if (filterPid.HasValue && process.Id != filterPid.Value)
					continue;
				foreach (var runtime in process.Runtimes) {
					var found = runtime.Modules.FirstOrDefault(m =>
						(m.Name ?? string.Empty).Equals(moduleName, StringComparison.OrdinalIgnoreCase) ||
						(m.Filename ?? string.Empty).Equals(moduleName, StringComparison.OrdinalIgnoreCase) ||
						Path.GetFileName(m.Filename ?? string.Empty).Equals(moduleName, StringComparison.OrdinalIgnoreCase));
					if (found != null)
						return (found, process);
				}
			}

			throw new ArgumentException($"Module '{moduleName}' not found in debugged runtimes.");
		}

		DbgProcess ResolveProcess(Dictionary<string, object>? arguments) {
			var mgr = dbgManager.Value;
			if (!mgr.IsDebugging)
				throw new InvalidOperationException("Debugger is not active.");

			var filterPid = GetInt(arguments, "process_id");
			var process = filterPid.HasValue
				? mgr.Processes.FirstOrDefault(p => p.Id == filterPid.Value)
				: mgr.Processes.FirstOrDefault(p => p.State != DbgProcessState.Terminated) ?? mgr.Processes.FirstOrDefault();
			return process ?? throw new InvalidOperationException("No debugged process found.");
		}

		DbgProcess ResolveProcessById(int processId) {
			var process = dbgManager.Value.Processes.FirstOrDefault(p => p.Id == processId);
			return process ?? throw new InvalidOperationException($"Debugged process {processId} is no longer available.");
		}

		static (string Name, uint Ordinal, uint Rva) GetExport(string? modulePath, string functionName) {
			if (string.IsNullOrWhiteSpace(modulePath) || !File.Exists(modulePath))
				throw new ArgumentException($"Module file not found on disk: {modulePath}");

			using var fs = File.OpenRead(modulePath);
			using var br = new BinaryReader(fs);

			fs.Seek(0x3C, SeekOrigin.Begin);
			int peOffset = br.ReadInt32();
			fs.Seek(peOffset, SeekOrigin.Begin);
			if (br.ReadUInt32() != 0x00004550)
				throw new ArgumentException("Module file is not a valid PE image.");

			br.ReadUInt16();
			ushort numberOfSections = br.ReadUInt16();
			br.BaseStream.Seek(12, SeekOrigin.Current);
			ushort sizeOfOptionalHeader = br.ReadUInt16();
			br.BaseStream.Seek(2, SeekOrigin.Current);

			long optionalHeaderStart = br.BaseStream.Position;
			ushort magic = br.ReadUInt16();
			bool isPe32Plus = magic == 0x20b;

			fs.Seek(peOffset + 4 + 20 + sizeOfOptionalHeader, SeekOrigin.Begin);
			var sections = new List<(uint VirtualAddress, uint VirtualSize, uint PointerToRawData, uint SizeOfRawData)>();
			for (int i = 0; i < numberOfSections; i++) {
				br.ReadBytes(8);
				uint virtualSize = br.ReadUInt32();
				uint virtualAddress = br.ReadUInt32();
				uint sizeOfRawData = br.ReadUInt32();
				uint pointerToRawData = br.ReadUInt32();
				br.BaseStream.Seek(16, SeekOrigin.Current);
				sections.Add((virtualAddress, virtualSize, pointerToRawData, sizeOfRawData));
			}

			fs.Seek(optionalHeaderStart, SeekOrigin.Begin);
			br.ReadUInt16();
			br.BaseStream.Seek(isPe32Plus ? 110 : 94, SeekOrigin.Current);
			uint exportRva = br.ReadUInt32();
			uint exportSize = br.ReadUInt32();
			if (exportRva == 0 || exportSize == 0)
				throw new ArgumentException($"Module '{Path.GetFileName(modulePath)}' has no export directory.");

			long RvaToOffset(uint rva) {
				foreach (var section in sections) {
					if (rva >= section.VirtualAddress && rva < section.VirtualAddress + Math.Max(section.VirtualSize, section.SizeOfRawData))
						return section.PointerToRawData + (rva - section.VirtualAddress);
				}
				throw new ArgumentException($"RVA 0x{rva:X8} is not mapped to any section.");
			}

			fs.Seek(RvaToOffset(exportRva), SeekOrigin.Begin);
			br.ReadUInt32();
			br.ReadUInt32();
			br.ReadUInt16();
			br.ReadUInt16();
			br.ReadUInt32();
			uint ordinalBase = br.ReadUInt32();
			br.ReadUInt32();
			uint numberOfNames = br.ReadUInt32();
			uint addressOfFunctions = br.ReadUInt32();
			uint addressOfNames = br.ReadUInt32();
			uint addressOfNameOrdinals = br.ReadUInt32();

			for (uint i = 0; i < numberOfNames; i++) {
				fs.Seek(RvaToOffset(addressOfNames + i * 4), SeekOrigin.Begin);
				uint nameRva = br.ReadUInt32();
				fs.Seek(RvaToOffset(nameRva), SeekOrigin.Begin);
				var sb = new StringBuilder();
				int nextByte;
				while ((nextByte = fs.ReadByte()) > 0)
					sb.Append((char)nextByte);
				var exportName = sb.ToString();
				if (!exportName.Equals(functionName, StringComparison.OrdinalIgnoreCase))
					continue;

				fs.Seek(RvaToOffset(addressOfNameOrdinals + i * 2), SeekOrigin.Begin);
				ushort ordinalIndex = br.ReadUInt16();
				fs.Seek(RvaToOffset(addressOfFunctions + ((uint)ordinalIndex * 4u)), SeekOrigin.Begin);
				uint functionRva = br.ReadUInt32();
				return (exportName, ordinalBase + ordinalIndex, functionRva);
			}

			throw new ArgumentException($"Export '{functionName}' not found in module '{Path.GetFileName(modulePath)}'.");
		}

		static byte[] ReadPatchBytes(Dictionary<string, object> arguments) {
			if (arguments.TryGetValue("bytes_base64", out var b64Obj)) {
				try { return Convert.FromBase64String(b64Obj?.ToString() ?? string.Empty); }
				catch { throw new ArgumentException("bytes_base64 is not valid base64."); }
			}
			if (arguments.TryGetValue("hex_bytes", out var hexObj))
				return ParseHexBytes(hexObj?.ToString() ?? string.Empty);
			if (arguments.TryGetValue("bytes", out var bytesObj))
				return ParseHexBytes(bytesObj?.ToString() ?? string.Empty);
			throw new ArgumentException("hex_bytes, bytes, or bytes_base64 is required.");
		}

		static byte[] ParseHexBytes(string hex) {
			hex = (hex ?? string.Empty).Replace(" ", "").Replace("-", "");
			if (hex.Length == 0 || (hex.Length % 2) != 0)
				throw new ArgumentException("hex bytes must contain an even number of hex digits.");
			var bytes = new byte[hex.Length / 2];
			for (int i = 0; i < bytes.Length; i++)
				bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
			return bytes;
		}

		static List<object> Disassemble(byte[] bytes, ulong address, int bitness) {
			var decoder = Iced.Intel.Decoder.Create(bitness, new ByteArrayCodeReader(bytes), options: DecoderOptions.None);
			decoder.IP = address;
			var formatter = new NasmFormatter();
			var output = new FormatterOutputString();
			var instructions = new List<object>();
			int count = 0;
			while (decoder.IP < address + (ulong)bytes.Length && count < 512) {
				var instruction = decoder.Decode();
				if (instruction.Length == 0)
					break;
				output.Reset();
				formatter.Format(in instruction, output);
				var instrBytes = bytes.Skip((int)(instruction.IP - address)).Take(instruction.Length).ToArray();
				instructions.Add(new {
					Address = $"0x{instruction.IP:X16}",
					Offset = $"0x{instruction.IP - address:X}",
					Mnemonic = instruction.Mnemonic.ToString(),
					Text = output.ToString(),
					Bytes = ToHex(instrBytes),
					FlowControl = instruction.FlowControl.ToString()
				});
				count++;
			}
			return instructions;
		}

		static string ToHex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", " ");

		static string ToAscii(byte[] bytes) {
			var chars = new char[bytes.Length];
			for (int i = 0; i < bytes.Length; i++) {
				var b = bytes[i];
				chars[i] = b >= 0x20 && b <= 0x7E ? (char)b : '.';
			}
			return new string(chars);
		}

		static HashSet<ulong> ParseThreadIds(Dictionary<string, object>? arguments) {
			var ids = new HashSet<ulong>();
			if (arguments == null || !arguments.TryGetValue("thread_ids", out var obj) || obj == null)
				return ids;

			if (obj is JsonElement elem && elem.ValueKind == JsonValueKind.Array) {
				foreach (var item in elem.EnumerateArray()) {
					if (item.ValueKind == JsonValueKind.Number && item.TryGetUInt64(out var value))
						ids.Add(value);
					else if (item.ValueKind == JsonValueKind.String && ulong.TryParse(item.GetString(), out var parsed))
						ids.Add(parsed);
				}
				return ids;
			}

			foreach (var part in obj.ToString()!.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)) {
				if (ulong.TryParse(part.Trim(), out var parsed))
					ids.Add(parsed);
			}

			return ids;
		}

		static Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcessForPatch(int processId) {
			var handle = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION, false, processId);
			if (handle.IsInvalid)
				throw new InvalidOperationException($"OpenProcess failed for PID {processId}. Win32 error: {Marshal.GetLastWin32Error()}");
			return handle;
		}

		static Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcessForRead(int processId) {
			var handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, processId);
			if (handle.IsInvalid)
				throw new InvalidOperationException($"OpenProcess failed for PID {processId}. Win32 error: {Marshal.GetLastWin32Error()}");
			return handle;
		}

		static Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcessForInjection(int processId) {
			var handle = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, processId);
			if (handle.IsInvalid)
				throw new InvalidOperationException($"OpenProcess failed for PID {processId}. Win32 error: {Marshal.GetLastWin32Error()}");
			return handle;
		}

		static uint VirtualProtect(IntPtr processHandle, ulong address, int size, uint protection) {
			if (!VirtualProtectEx(processHandle, new IntPtr(unchecked((long)address)), new UIntPtr((uint)size), protection, out var oldProtect))
				throw new InvalidOperationException($"VirtualProtectEx failed at 0x{address:X16}. Win32 error: {Marshal.GetLastWin32Error()}");
			return oldProtect;
		}

		static void RestoreProtect(IntPtr processHandle, ulong address, int size, uint originalProtect) {
			if (!VirtualProtectEx(processHandle, new IntPtr(unchecked((long)address)), new UIntPtr((uint)size), originalProtect, out _))
				throw new InvalidOperationException($"VirtualProtectEx restore failed at 0x{address:X16}. Win32 error: {Marshal.GetLastWin32Error()}");
		}

		static PROCESS_BASIC_INFORMATION QueryBasicInformation(IntPtr processHandle) {
			int status = NtQueryInformationProcess(processHandle, 0, out PROCESS_BASIC_INFORMATION pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
			if (status != 0)
				throw new InvalidOperationException($"NtQueryInformationProcess(ProcessBasicInformation) failed with NTSTATUS 0x{status:X8}.");
			return pbi;
		}

		static byte[] ReadMemoryExact(IntPtr processHandle, ulong address, int size) {
			var buffer = new byte[size];
			if (!ReadProcessMemory(processHandle, new IntPtr(unchecked((long)address)), buffer, new IntPtr(size), out var bytesRead) || bytesRead.ToInt64() < size)
				throw new InvalidOperationException($"ReadProcessMemory failed at 0x{address:X16}. Win32 error: {Marshal.GetLastWin32Error()}");
			return buffer;
		}

		object InjectFrameworkManagedDll(DbgProcess process, string dllPath, string typeName, string methodName, string? argument, string clrVersion) {
			using var handle = OpenProcessForInjection(process.Id);
			var mscoree = Process.GetProcessById(process.Id).Modules
				.Cast<System.Diagnostics.ProcessModule>()
				.FirstOrDefault(m => string.Equals(m.ModuleName, "mscoree.dll", StringComparison.OrdinalIgnoreCase))
				?? throw new InvalidOperationException("Could not find mscoree.dll in the target process.");

			var corBindOffset = GetExport(mscoree.FileName, "CorBindToRuntimeEx").Rva;
			var corBindAddress = new IntPtr(unchecked((long)(mscoree.BaseAddress.ToInt64() + corBindOffset)));
			var stub = CreateFrameworkInjectionStub(handle.DangerousGetHandle(), dllPath, typeName, methodName, argument, corBindAddress, process.Bitness == 32, clrVersion);
			var remoteCode = VirtualAllocRemote(handle.DangerousGetHandle(), stub.Length, PAGE_EXECUTE_READWRITE);
			WriteRemoteBytes(handle.DangerousGetHandle(), remoteCode, stub);
			var thread = CreateRemoteThreadChecked(handle.DangerousGetHandle(), remoteCode, IntPtr.Zero);
			WaitForSingleObject(thread, INFINITE);
			GetExitCodeThreadChecked(thread, out var exitCode);
			CloseHandle(thread);

			return new {
				Technique = "CLR ExecuteInDefaultAppDomain",
				ClrVersion = clrVersion,
				StubAddress = $"0x{remoteCode.ToInt64():X16}",
				ExitCode = $"0x{exitCode.ToInt64():X16}"
			};
		}

		object InjectUnityManagedDll(DbgProcess process, string dllPath, string typeName, string methodName, string? argument) {
			using var handle = OpenProcessForInjection(process.Id);
			var monoModule = Process.GetProcessById(process.Id).Modules
				.Cast<System.Diagnostics.ProcessModule>()
				.FirstOrDefault(m =>
					string.Equals(m.ModuleName, "mono.dll", StringComparison.OrdinalIgnoreCase) ||
					string.Equals(m.ModuleName, "mono-2.0-bdwgc.dll", StringComparison.OrdinalIgnoreCase))
				?? throw new InvalidOperationException("Could not find Mono module in the target process.");

			var exports = GetAllExportsFromProcessModule(handle.DangerousGetHandle(), monoModule, process.Bitness == 32);
			IntPtr GetFunction(string name) => exports.TryGetValue(name, out var rva)
				? new IntPtr(monoModule.BaseAddress.ToInt64() + rva.ToInt64())
				: throw new InvalidOperationException($"Could not resolve exported function '{name}' from {monoModule.ModuleName}.");

			var rootDomain = CallRemoteFunction(handle.DangerousGetHandle(), GetFunction("mono_get_root_domain"), Array.Empty<object>(), process.Bitness == 32, null);
			var rawImage = CallRemoteFunction(handle.DangerousGetHandle(), GetFunction("mono_image_open"), new object[] { AllocateAnsiString(handle.DangerousGetHandle(), dllPath), IntPtr.Zero }, process.Bitness == 32, null);
			var assembly = CallRemoteFunction(handle.DangerousGetHandle(), GetFunction("mono_assembly_load_from_full"), new object[] { rawImage, AllocateAnsiString(handle.DangerousGetHandle(), string.Empty), IntPtr.Zero, 0 }, process.Bitness == 32, (GetFunction("mono_thread_attach"), rootDomain));
			var image = CallRemoteFunction(handle.DangerousGetHandle(), GetFunction("mono_assembly_get_image"), new object[] { assembly }, process.Bitness == 32, (GetFunction("mono_thread_attach"), rootDomain));
			var (ns, name) = SplitTypeName(typeName);
			var klass = CallRemoteFunction(handle.DangerousGetHandle(), GetFunction("mono_class_from_name"), new object[] { image, AllocateAnsiString(handle.DangerousGetHandle(), ns), AllocateAnsiString(handle.DangerousGetHandle(), name) }, process.Bitness == 32, (GetFunction("mono_thread_attach"), rootDomain));
			var method = CallRemoteFunction(handle.DangerousGetHandle(), GetFunction("mono_class_get_method_from_name"), new object[] { klass, AllocateAnsiString(handle.DangerousGetHandle(), methodName), 1 }, process.Bitness == 32, (GetFunction("mono_thread_attach"), rootDomain));
			var monoString = CallRemoteFunction(handle.DangerousGetHandle(), GetFunction("mono_string_new_wrapper"), new object[] { AllocateAnsiString(handle.DangerousGetHandle(), argument ?? string.Empty) }, process.Bitness == 32, (GetFunction("mono_thread_attach"), rootDomain));
			var argArray = process.Bitness == 32
				? BitConverter.GetBytes(monoString.ToInt32())
				: BitConverter.GetBytes(monoString.ToInt64());
			var argPointer = AllocateBytes(handle.DangerousGetHandle(), argArray);
			CallRemoteFunction(handle.DangerousGetHandle(), GetFunction("mono_runtime_invoke"), new object[] { method, IntPtr.Zero, argPointer, IntPtr.Zero }, process.Bitness == 32, (GetFunction("mono_thread_attach"), rootDomain), wait: false);

			return new {
				Technique = "Mono runtime invoke",
				MonoModule = monoModule.ModuleName,
				RootDomain = $"0x{rootDomain.ToInt64():X16}",
				MonoClass = $"0x{klass.ToInt64():X16}",
				MonoMethod = $"0x{method.ToInt64():X16}"
			};
		}

		static byte[] CreateFrameworkInjectionStub(IntPtr processHandle, string dllPath, string typeName, string methodName, string? argument, IntPtr corBindToRuntimeEx, bool x86, string clrVersion) {
			var clsidClrRuntimeHost = new Guid(0x90F1A06E, 0x7712, 0x4762, 0x86, 0xB5, 0x7A, 0x5E, 0xBA, 0x6B, 0xDB, 0x02);
			var iidClrRuntimeHost = new Guid(0x90F1A06C, 0x7712, 0x4762, 0x86, 0xB5, 0x7A, 0x5E, 0xBA, 0x6B, 0xDB, 0x02);
			const string buildFlavor = "wks";

			IntPtr ppv = VirtualAllocRemote(processHandle, IntPtr.Size, PAGE_READWRITE);
			IntPtr riid = AllocateBytes(processHandle, iidClrRuntimeHost.ToByteArray());
			IntPtr rclsid = AllocateBytes(processHandle, clsidClrRuntimeHost.ToByteArray());
			IntPtr pBuildFlavor = AllocateUnicodeString(processHandle, buildFlavor);
			IntPtr pVersion = AllocateUnicodeString(processHandle, clrVersion);
			IntPtr pReturnValue = VirtualAllocRemote(processHandle, 4, PAGE_READWRITE);
			IntPtr pArgument = AllocateUnicodeString(processHandle, argument ?? string.Empty);
			IntPtr pMethodName = AllocateUnicodeString(processHandle, methodName);
			IntPtr pTypeName = AllocateUnicodeString(processHandle, typeName);
			IntPtr pAssemblyPath = AllocateUnicodeString(processHandle, dllPath);

			var instructions = new InstructionList();
			void AddCallPtr(IntPtr fn, params object[] callArgs) => AddCallStub(instructions, fn, callArgs, x86, x86);
			void AddCallReg(Register reg, params object[] callArgs) => AddCallStub(instructions, reg, callArgs, x86);

			if (x86) {
				AddCallPtr(corBindToRuntimeEx, pVersion, pBuildFlavor, (byte)0, rclsid, riid, ppv);
				instructions.Add(Instruction.Create(Code.Mov_r32_rm32, Register.EDX, new MemoryOperand(Register.None, ppv.ToInt32())));
				instructions.Add(Instruction.Create(Code.Mov_r32_rm32, Register.EAX, new MemoryOperand(Register.EDX)));
				instructions.Add(Instruction.Create(Code.Mov_r32_rm32, Register.EAX, new MemoryOperand(Register.EAX, 0x0C)));
				AddCallReg(Register.EAX, Register.EDX);

				instructions.Add(Instruction.Create(Code.Mov_r32_rm32, Register.EDX, new MemoryOperand(Register.None, ppv.ToInt32())));
				instructions.Add(Instruction.Create(Code.Mov_r32_rm32, Register.EAX, new MemoryOperand(Register.EDX)));
				instructions.Add(Instruction.Create(Code.Mov_r32_rm32, Register.EAX, new MemoryOperand(Register.EAX, 0x2C)));
				AddCallReg(Register.EAX, Register.EDX, pAssemblyPath, pTypeName, pMethodName, pArgument, pReturnValue);
				instructions.Add(Instruction.Create(Code.Retnd));
			}
			else {
				const int maxStackIndex = 3;
				const int stackOffset = 0x20;
				instructions.Add(Instruction.Create(Code.Sub_rm64_imm8, Register.RSP, stackOffset + maxStackIndex * 8));
				AddCallPtr(corBindToRuntimeEx, pVersion, pBuildFlavor, 0, rclsid, riid, ppv);
				instructions.Add(Instruction.Create(Code.Mov_r64_imm64, Register.RCX, ppv.ToInt64()));
				instructions.Add(Instruction.Create(Code.Mov_r64_rm64, Register.RCX, new MemoryOperand(Register.RCX)));
				instructions.Add(Instruction.Create(Code.Mov_r64_rm64, Register.RAX, new MemoryOperand(Register.RCX)));
				instructions.Add(Instruction.Create(Code.Mov_r64_rm64, Register.RDX, new MemoryOperand(Register.RAX, 0x18)));
				AddCallReg(Register.RDX, Register.RCX);

				instructions.Add(Instruction.Create(Code.Mov_r64_imm64, Register.RCX, ppv.ToInt64()));
				instructions.Add(Instruction.Create(Code.Mov_r64_rm64, Register.RCX, new MemoryOperand(Register.RCX)));
				instructions.Add(Instruction.Create(Code.Mov_r64_rm64, Register.RAX, new MemoryOperand(Register.RCX)));
				instructions.Add(Instruction.Create(Code.Mov_r64_rm64, Register.RAX, new MemoryOperand(Register.RAX, 0x58)));
				AddCallReg(Register.RAX, Register.RCX, pAssemblyPath, pTypeName, pMethodName, pArgument, pReturnValue);
				instructions.Add(Instruction.Create(Code.Add_rm64_imm8, Register.RSP, stackOffset + maxStackIndex * 8));
				instructions.Add(Instruction.Create(Code.Retnq));
			}

			return EncodeInstructions(instructions, x86 ? 32 : 64);
		}

		static void AddCallStub(InstructionList instructions, IntPtr function, object[] arguments, bool x86, bool cleanStack = false) {
			if (x86) {
				instructions.Add(Instruction.Create(Code.Mov_r32_imm32, Register.EAX, function.ToInt32()));
				AddCallStub(instructions, Register.EAX, arguments, true, cleanStack);
			}
			else {
				instructions.Add(Instruction.Create(Code.Mov_r64_imm64, Register.RAX, function.ToInt64()));
				AddCallStub(instructions, Register.RAX, arguments, false, cleanStack);
			}
		}

		static void AddCallStub(InstructionList instructions, Register functionRegister, object[] arguments, bool x86, bool cleanStack = false) {
			if (x86) {
				for (int i = arguments.Length - 1; i >= 0; i--) {
					instructions.Add(arguments[i] switch {
						IntPtr ptr => Instruction.Create(Code.Pushd_imm32, ptr.ToInt32()),
						int value => Instruction.Create(Code.Pushd_imm32, value),
						byte value => Instruction.Create(Code.Pushd_imm8, value),
						Register register => Instruction.Create(Code.Push_r32, register),
						_ => throw new NotSupportedException($"Unsupported x86 call argument type: {arguments[i].GetType().FullName}")
					});
				}

				instructions.Add(Instruction.Create(Code.Call_rm32, functionRegister));
				if (cleanStack && arguments.Length > 0)
					instructions.Add(Instruction.Create(Code.Add_rm32_imm8, Register.ESP, arguments.Length * IntPtr.Size));
				return;
			}

			const Register tempRegister = Register.RAX;
			instructions.Add(Instruction.Create(Code.Push_r64, tempRegister));

			for (int i = arguments.Length - 1; i >= 0; i--) {
				object arg = arguments[i];
				Register argRegister = i switch {
					0 => Register.RCX,
					1 => Register.RDX,
					2 => Register.R8,
					3 => Register.R9,
					_ => Register.None
				};

				if (i > 3) {
					if (arg is Register stackRegister) {
						instructions.Add(Instruction.Create(Code.Mov_rm64_r64, new MemoryOperand(Register.RSP, 0x20 + (i - 3) * 8), stackRegister));
					}
					else {
						instructions.Add(Instruction.Create(Code.Mov_r64_imm64, tempRegister, ConvertToLong(arg)));
						instructions.Add(Instruction.Create(Code.Mov_rm64_r64, new MemoryOperand(Register.RSP, 0x20 + (i - 3) * 8), tempRegister));
					}
				}
				else {
					if (arg is Register registerArg)
						instructions.Add(Instruction.Create(Code.Mov_r64_rm64, argRegister, registerArg));
					else
						instructions.Add(Instruction.Create(Code.Mov_r64_imm64, argRegister, ConvertToLong(arg)));
				}
			}

			instructions.Add(Instruction.Create(Code.Pop_r64, tempRegister));
			instructions.Add(Instruction.Create(Code.Call_rm64, functionRegister));

			static long ConvertToLong(object value) => value switch {
				IntPtr ptr => ptr.ToInt64(),
				UIntPtr ptr => unchecked((long) ptr.ToUInt64()),
				_ => Convert.ToInt64(value)
			};
		}

		static IntPtr CallRemoteFunction(IntPtr processHandle, IntPtr function, object[] arguments, bool x86, (IntPtr function, IntPtr arg)? rootDomainInfo, bool wait = true) {
			IntPtr returnValueAddress = VirtualAllocRemote(processHandle, IntPtr.Size, PAGE_READWRITE);
			var instructions = new InstructionList();
			void AddCall(IntPtr fn, params object[] callArgs) => AddCallStub(instructions, fn, callArgs, x86, x86);

			if (x86) {
				if (rootDomainInfo.HasValue)
					AddCall(rootDomainInfo.Value.function, rootDomainInfo.Value.arg);
				AddCall(function, arguments);
				instructions.Add(Instruction.Create(Code.Mov_rm32_r32, new MemoryOperand(Register.None, returnValueAddress.ToInt32()), Register.EAX));
				instructions.Add(Instruction.Create(Code.Retnd));
			}
			else {
				int stackSize = 0x20 + Math.Max(0, arguments.Length - 4) * 8;
				instructions.Add(Instruction.Create(Code.Sub_rm64_imm8, Register.RSP, stackSize));
				if (rootDomainInfo.HasValue)
					AddCall(rootDomainInfo.Value.function, rootDomainInfo.Value.arg);
				AddCall(function, arguments);
				instructions.Add(Instruction.Create(Code.Mov_r64_imm64, Register.RBX, returnValueAddress.ToInt64()));
				instructions.Add(Instruction.Create(Code.Mov_rm64_r64, new MemoryOperand(Register.RBX), Register.RAX));
				instructions.Add(Instruction.Create(Code.Add_rm64_imm8, Register.RSP, stackSize));
				instructions.Add(Instruction.Create(Code.Retnq));
			}

			var stub = EncodeInstructions(instructions, x86 ? 32 : 64);
			var remoteCode = VirtualAllocRemote(processHandle, stub.Length, PAGE_EXECUTE_READWRITE);
			WriteRemoteBytes(processHandle, remoteCode, stub);
			var thread = CreateRemoteThreadChecked(processHandle, remoteCode, IntPtr.Zero);
			if (!wait)
				return IntPtr.Zero;
			WaitForSingleObject(thread, INFINITE);
			GetExitCodeThreadChecked(thread, out _);
			var outBuffer = new byte[IntPtr.Size];
			if (!ReadProcessMemory(processHandle, returnValueAddress, outBuffer, new IntPtr(outBuffer.Length), out var read) || read.ToInt64() < IntPtr.Size)
				throw new InvalidOperationException($"ReadProcessMemory failed while reading remote return value. Win32 error: {Marshal.GetLastWin32Error()}");
			CloseHandle(thread);
			return IntPtr.Size == 8 ? new IntPtr(BitConverter.ToInt64(outBuffer, 0)) : new IntPtr(BitConverter.ToInt32(outBuffer, 0));
		}

		static Dictionary<string, IntPtr> GetAllExportsFromProcessModule(IntPtr processHandle, System.Diagnostics.ProcessModule module, bool x86) {
			var exports = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
			int ReadInt(int offset) => BitConverter.ToInt32(ReadRemoteBytes(processHandle, module.BaseAddress + offset, 4), 0);
			string ReadAsciiZ(int offset) {
				var sb = new StringBuilder();
				int index = 0;
				while (true) {
					var b = ReadRemoteBytes(processHandle, module.BaseAddress + offset + index, 1)[0];
					if (b == 0)
						return sb.ToString();
					sb.Append((char)b);
					index++;
				}
			}

			int hdr = ReadInt(0x3C);
			int exportTableRva = ReadInt(hdr + (x86 ? 0x78 : 0x88));
			var exportDirBytes = ReadRemoteBytes(processHandle, module.BaseAddress + exportTableRva, Marshal.SizeOf<ImageExportDirectory>());
			var exportDir = BytesToStruct<ImageExportDirectory>(exportDirBytes);
			var functions = ReadArray<int>(processHandle, module.BaseAddress + (int)exportDir.AddressOfFunctions, (int)exportDir.NumberOfFunctions);
			var names = ReadArray<int>(processHandle, module.BaseAddress + (int)exportDir.AddressOfNames, (int)exportDir.NumberOfNames);
			var ordinals = ReadArray<ushort>(processHandle, module.BaseAddress + (int)exportDir.AddressOfNameOrdinals, (int)exportDir.NumberOfNames);

			for (int i = 0; i < names.Length; i++) {
				var name = ReadAsciiZ(names[i]);
				exports[name] = new IntPtr(functions[ordinals[i]]);
			}

			return exports;
		}

		static byte[] ReadRemoteBytes(IntPtr processHandle, IntPtr address, int size) {
			var bytes = new byte[size];
			if (!ReadProcessMemory(processHandle, address, bytes, new IntPtr(size), out var read) || read.ToInt64() < size)
				throw new InvalidOperationException($"ReadProcessMemory failed at 0x{address.ToInt64():X16}. Win32 error: {Marshal.GetLastWin32Error()}");
			return bytes;
		}

		static T[] ReadArray<T>(IntPtr processHandle, IntPtr address, int count) where T : struct {
			int size = Marshal.SizeOf<T>() * count;
			var bytes = ReadRemoteBytes(processHandle, address, size);
			var result = new T[count];
			Buffer.BlockCopy(bytes, 0, result, 0, size);
			return result;
		}

		static T BytesToStruct<T>(byte[] bytes) where T : struct {
			var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
			try {
				return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
			}
			finally {
				handle.Free();
			}
		}

		static byte[] EncodeInstructions(InstructionList instructions, int bitness) {
			var writer = new CodeWriterImpl();
			var block = new InstructionBlock(writer, instructions, 0);
			if (!BlockEncoder.TryEncode(bitness, block, out var errorMessage, out _))
				throw new InvalidOperationException("Iced encode failed: " + errorMessage);
			return writer.ToArray();
		}

		static IntPtr AllocateAnsiString(IntPtr processHandle, string value) => AllocateBytes(processHandle, Encoding.ASCII.GetBytes((value ?? string.Empty) + "\0"));
		static IntPtr AllocateUnicodeString(IntPtr processHandle, string value) => AllocateBytes(processHandle, Encoding.Unicode.GetBytes((value ?? string.Empty) + "\0"));
		static IntPtr AllocateBytes(IntPtr processHandle, byte[] bytes) {
			var address = VirtualAllocRemote(processHandle, bytes.Length, PAGE_READWRITE);
			WriteRemoteBytes(processHandle, address, bytes);
			return address;
		}

		static IntPtr VirtualAllocRemote(IntPtr processHandle, int size, uint protection) {
			var address = VirtualAllocEx(processHandle, IntPtr.Zero, new UIntPtr((uint)size), MEM_COMMIT | MEM_RESERVE, protection);
			if (address == IntPtr.Zero)
				throw new InvalidOperationException($"VirtualAllocEx failed. Win32 error: {Marshal.GetLastWin32Error()}");
			return address;
		}

		static void WriteRemoteBytes(IntPtr processHandle, IntPtr address, byte[] bytes) {
			if (!WriteProcessMemory(processHandle, address, bytes, new UIntPtr((uint)bytes.Length), out var written) || written.ToUInt64() < (ulong)bytes.Length)
				throw new InvalidOperationException($"WriteProcessMemory failed at 0x{address.ToInt64():X16}. Win32 error: {Marshal.GetLastWin32Error()}");
		}

		static IntPtr CreateRemoteThreadChecked(IntPtr processHandle, IntPtr startAddress, IntPtr parameter) {
			var thread = CreateRemoteThread(processHandle, IntPtr.Zero, UIntPtr.Zero, startAddress, parameter, 0, IntPtr.Zero);
			if (thread == IntPtr.Zero)
				throw new InvalidOperationException($"CreateRemoteThread failed. Win32 error: {Marshal.GetLastWin32Error()}");
			return thread;
		}

		static void GetExitCodeThreadChecked(IntPtr thread, out IntPtr exitCode) {
			if (!GetExitCodeThread(thread, out exitCode))
				throw new InvalidOperationException($"GetExitCodeThread failed. Win32 error: {Marshal.GetLastWin32Error()}");
		}

		static IntPtr ResolveKernel32Export(string exportName) {
			var kernel32 = Process.GetCurrentProcess().Modules
				.Cast<System.Diagnostics.ProcessModule>()
				.FirstOrDefault(m => string.Equals(m.ModuleName, "kernel32.dll", StringComparison.OrdinalIgnoreCase))
				?? throw new InvalidOperationException("kernel32.dll is not loaded in the current process.");
			return GetProcAddress(kernel32.BaseAddress, exportName);
		}

		static ManagedRuntimeType GetRuntimeType(DbgRuntime runtime) => runtime.Name switch {
			"CLR v2.0.50727" => ManagedRuntimeType.FrameworkV2,
			"CLR v4.0.30319" => ManagedRuntimeType.FrameworkV4,
			"Unity" => ManagedRuntimeType.Unity,
			"CoreCLR" => ManagedRuntimeType.NetCore,
			_ => ManagedRuntimeType.Unknown
		};

		static string CopyInjectedDllToTemp(string path) {
			string dir = Path.Combine(Path.GetTempPath(), "dnSpy.MCP.Server", "InjectedDlls");
			Directory.CreateDirectory(dir);
			string newPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + Path.GetExtension(path));
			File.Copy(path, newPath, overwrite: true);
			return newPath;
		}

		static (string Namespace, string Name) SplitTypeName(string typeName) {
			int index = typeName.LastIndexOf('.');
			return index < 0
				? (string.Empty, typeName)
				: (typeName.Substring(0, index), typeName.Substring(index + 1));
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

		static int RequireInt(Dictionary<string, object>? arguments, string name, int minValue, int maxValue) =>
			RequireInt(arguments, name, minValue, maxValue, null);

		static int RequireInt(Dictionary<string, object>? arguments, string name, int minValue, int maxValue, int? defaultValue) {
			var value = GetInt(arguments, name);
			if (!value.HasValue) {
				if (defaultValue.HasValue)
					return defaultValue.Value;
				throw new ArgumentException($"{name} is required");
			}
			if (value.Value < minValue || value.Value > maxValue)
				throw new ArgumentException($"{name} must be in range {minValue}..{maxValue}");
			return value.Value;
		}

		static int? GetInt(Dictionary<string, object>? arguments, string name) {
			if (arguments == null || !arguments.TryGetValue(name, out var value) || value == null)
				return null;
			if (value is JsonElement elem) {
				if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out var parsed))
					return parsed;
				if (elem.ValueKind == JsonValueKind.String && int.TryParse(elem.GetString(), out parsed))
					return parsed;
				return null;
			}
			return int.TryParse(value.ToString(), out var result) ? result : null;
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

		static ulong ParseAddress(Dictionary<string, object>? arguments, string name) {
			var value = RequireString(arguments, name).Trim();
			if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				return Convert.ToUInt64(value.Substring(2), 16);
			return Convert.ToUInt64(value, 10);
		}

		static string GetThreadKey(int processId, ulong threadId) => $"{processId}:{threadId}";

		static CallToolResult JsonResult(string json) => new CallToolResult {
			Content = new List<ToolContent> { new ToolContent { Text = json } }
		};

		const uint PROCESS_QUERY_INFORMATION = 0x0400;
		const uint PROCESS_CREATE_THREAD = 0x0002;
		const uint PROCESS_VM_OPERATION = 0x0008;
		const uint PROCESS_VM_READ = 0x0010;
		const uint PROCESS_VM_WRITE = 0x0020;
		const uint MEM_COMMIT = 0x1000;
		const uint MEM_RESERVE = 0x2000;
		const uint PAGE_READWRITE = 0x04;
		const uint PAGE_EXECUTE_READWRITE = 0x40;
		const uint INFINITE = 0xFFFFFFFF;

		[StructLayout(LayoutKind.Sequential)]
		struct PROCESS_BASIC_INFORMATION {
			public IntPtr Reserved1;
			public IntPtr PebBaseAddress;
			public IntPtr Reserved2_0;
			public IntPtr Reserved2_1;
			public IntPtr UniqueProcessId;
			public IntPtr InheritedFromUniqueProcessId;
		}

		sealed class FormatterOutputString : FormatterOutput {
			readonly StringBuilder sb = new StringBuilder();
			public override void Write(string text, FormatterTextKind kind) => sb.Append(text);
			public override string ToString() => sb.ToString();
			public void Reset() => sb.Clear();
		}

		sealed class CodeWriterImpl : CodeWriter {
			readonly List<byte> bytes = new List<byte>();
			public override void WriteByte(byte value) => bytes.Add(value);
			public byte[] ToArray() => bytes.ToArray();
		}

		enum ManagedRuntimeType {
			Unknown,
			FrameworkV2,
			FrameworkV4,
			NetCore,
			Unity
		}

		[StructLayout(LayoutKind.Sequential)]
		struct ImageExportDirectory {
			public uint Characteristics;
			public uint TimeDateStamp;
			public ushort MajorVersion;
			public ushort MinorVersion;
			public uint Name;
			public uint Base;
			public uint NumberOfFunctions;
			public uint NumberOfNames;
			public uint AddressOfFunctions;
			public uint AddressOfNames;
			public uint AddressOfNameOrdinals;
		}

		sealed class NativePatchRecord {
			public NativePatchRecord(string patchId, int processId, string processName, string moduleName, string modulePath, string functionName, ulong address, byte[] originalBytes, byte[] patchedBytes, DateTimeOffset appliedAtUtc) {
				PatchId = patchId;
				ProcessId = processId;
				ProcessName = processName;
				ModuleName = moduleName;
				ModulePath = modulePath;
				FunctionName = functionName;
				Address = address;
				OriginalBytes = originalBytes;
				PatchedBytes = patchedBytes;
				AppliedAtUtc = appliedAtUtc;
			}

			public string PatchId { get; }
			public int ProcessId { get; }
			public string ProcessName { get; }
			public string ModuleName { get; }
			public string ModulePath { get; }
			public string FunctionName { get; }
			public ulong Address { get; }
			public byte[] OriginalBytes { get; }
			public byte[] PatchedBytes { get; }
			public DateTimeOffset AppliedAtUtc { get; }
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool VirtualProtectEx(IntPtr processHandle, IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool ReadProcessMemory(IntPtr processHandle, IntPtr baseAddress, [Out] byte[] buffer, IntPtr size, out IntPtr bytesRead);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern IntPtr VirtualAllocEx(IntPtr processHandle, IntPtr address, UIntPtr size, uint allocationType, uint protection);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool WriteProcessMemory(IntPtr processHandle, IntPtr address, [In] byte[] buffer, UIntPtr size, out UIntPtr bytesWritten);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern IntPtr CreateRemoteThread(IntPtr processHandle, IntPtr threadAttributes, UIntPtr stackSize, IntPtr startAddress, IntPtr parameter, uint creationFlags, IntPtr threadId);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool GetExitCodeThread(IntPtr thread, out IntPtr exitCode);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool CloseHandle(IntPtr handle);

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
		static extern IntPtr GetProcAddress(IntPtr moduleHandle, string procName);

		[DllImport("ntdll.dll")]
		static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, out PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);
	}
}
