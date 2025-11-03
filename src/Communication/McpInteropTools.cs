using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.IO;
using dnlib.DotNet;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.MCP.Server.Contracts;
using dnSpy.MCP.Server.Helper;

namespace dnSpy.MCP.Server.Communication
{
    /// <summary>
    /// Herramientas específicas para interoperabilidad nativa / PInvoke / C++/CLI.
    /// Contiene análisis de firmas P/Invoke y layout/marshalling heurístico.
    /// </summary>
    [Export(typeof(IMcpInteropTools))]
    public sealed class McpInteropTools : IMcpInteropTools
    {
        readonly IDocumentTreeView documentTreeView;

        [ImportingConstructor]
        public McpInteropTools(IDocumentTreeView documentTreeView)
        {
            this.documentTreeView = documentTreeView;
        }

        /// <summary>
        /// Encuentra todas las firmas P/Invoke (DllImport) en el ensamblado solicitado.
        /// Parámetros: { "assembly_name": string, "filter": string (opcional) }
        /// </summary>
        public CallToolResult FindPInvokeSignatures(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var filter = arguments.TryGetValue("filter", out var f) ? (f?.ToString() ?? string.Empty) : string.Empty;

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var results = new List<object>();

            foreach (var type in assembly.Modules.SelectMany(m => m.Types))
            {
                foreach (var method in type.Methods)
                {
                    bool hasPInvoke = false;
                    string dllName = string.Empty;
                    string entryPoint = string.Empty;
                    string callingConvention = string.Empty;
                    string charSet = string.Empty;

                    // Some dnlib versions expose explicit PInvoke metadata; to keep this code
                    // compatible across dnlib versions we rely primarily on DllImportAttribute
                    // detection below. Keep this placeholder for future dnlib-specific checks.

                    // Buscar atributo DllImportAttribute en CustomAttributes
                    try
                    {
                        foreach (var ca in method.CustomAttributes)
                        {
                            var at = ca.AttributeType.FullName;
                            if (!string.IsNullOrEmpty(at) && at.EndsWith("DllImportAttribute"))
                            {
                                hasPInvoke = true;
                                if (ca.ConstructorArguments.Count > 0)
                                {
                                    dllName = ca.ConstructorArguments[0].Value?.ToString() ?? dllName;
                                }
                                foreach (var named in ca.NamedArguments)
                                {
                                    if (named.Name == "EntryPoint")
                                        entryPoint = named.Argument.Value?.ToString() ?? entryPoint;
                                    if (named.Name == "CharSet")
                                        charSet = named.Argument.Value?.ToString() ?? charSet;
                                    if (named.Name == "CallingConvention")
                                        callingConvention = named.Argument.Value?.ToString() ?? callingConvention;
                                }
                            }
                        }
                    }
                    catch { }

                    if (hasPInvoke)
                    {
                        if (!string.IsNullOrEmpty(filter) &&
                            !(type.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                              dllName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                              method.Name.String.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            continue;
                        }

                        results.Add(new
                        {
                            Type = type.FullName,
                            Method = method.Name.String,
                            Signature = method.FullName,
                            Dll = dllName,
                            EntryPoint = string.IsNullOrEmpty(entryPoint) ? method.Name.String : entryPoint,
                            CharSet = charSet,
                            CallingConvention = callingConvention,
                            Parameters = method.Parameters.Select(p => new { Name = p.Name, Type = p.Type.FullName }).ToList(),
                            ReturnType = method.ReturnType?.FullName ?? "void",
                            HasBody = method.HasBody
                        });
                    }
                }
            }

            var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = json } }
            };
        }

        /// <summary>
        /// Analiza StructLayout y patrones de marshalling en tipos del ensamblado.
        /// Parámetros: { "assembly_name": string, "type_filter": string (opt) }
        /// Devuelve un resumen heurístico indicando si el tipo parece blittable y detalles de campos.
        /// </summary>
        public CallToolResult AnalyzeMarshallingAndLayout(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeFilter = arguments.TryGetValue("type_filter", out var tf) ? (tf?.ToString() ?? string.Empty) : string.Empty;

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var results = new List<object>();

            foreach (var type in assembly.Modules.SelectMany(m => m.Types))
            {
                if (!string.IsNullOrEmpty(typeFilter) && type.FullName.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                // Heurística: interesa principalmente value-types/structs o tipos con StructLayout
                bool hasStructLayout = type.CustomAttributes.Any(ca => ca.AttributeType.FullName.EndsWith("StructLayoutAttribute"));
                if (!hasStructLayout && !type.IsValueType)
                {
                    // skip classes sin StructLayout salvo que el usuario haya pedido filtro específico
                    if (string.IsNullOrEmpty(typeFilter))
                        continue;
                }

                var fieldInfos = new List<object>();
                bool isBlittable = true;
                foreach (var field in type.Fields)
                {
                    var fieldTypeName = field.FieldType.FullName;
                    bool fieldHasMarshalAs = field.CustomAttributes.Any(ca => ca.AttributeType.FullName.Contains("MarshalAsAttribute"));

                    fieldInfos.Add(new
                    {
                        Name = field.Name.String,
                        Type = fieldTypeName,
                        Attributes = field.Attributes.ToString(),
                        HasMarshalAs = fieldHasMarshalAs
                    });

                    // Heurística simplificada: strings, object, delegates, arrays y referencias no son blittable
                    if (fieldTypeName.Contains("System.String") ||
                        fieldTypeName.Contains("System.Object") ||
                        fieldTypeName.Contains("[]") ||
                        fieldTypeName.Contains("System.Delegate") ||
                        fieldTypeName.Contains("System.IntPtr") && fieldHasMarshalAs == false && fieldTypeName.Contains("System.IntPtr") == false)
                    {
                        // conservador
                        isBlittable = false;
                    }

                    try
                    {
                        var resolved = field.FieldType.ToTypeDefOrRef()?.ResolveTypeDef();
                        if (resolved != null && resolved.IsClass)
                            isBlittable = false;
                    }
                    catch { }
                }

                // Extract StructLayout info (simple)
                string layoutKind = "Auto";
                int pack = 0;
                foreach (var ca in type.CustomAttributes)
                {
                    if (ca.AttributeType.FullName.EndsWith("StructLayoutAttribute"))
                    {
                        // Named arguments or constructor args may contain layout info; use NamedArguments when present
                        foreach (var named in ca.NamedArguments)
                        {
                            if (named.Name == "Pack" && named.Argument.Value is int pi)
                                pack = pi;
                            if (named.Name == "Value")
                                layoutKind = named.Argument.Value?.ToString() ?? layoutKind;
                        }
                    }
                }

                results.Add(new
                {
                    Type = type.FullName,
                    IsValueType = type.IsValueType,
                    HasStructLayout = hasStructLayout,
                    LayoutKind = layoutKind,
                    Pack = pack,
                    IsBlittable = isBlittable,
                    Fields = fieldInfos
                });
            }

            var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = json } }
            };
        }

        /// <summary>
        /// List native modules referenced by managed metadata (DllImport attributes) in the given assembly.
        /// Parameters: { "assembly_name": string }
        /// </summary>
        public CallToolResult ListNativeModules(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var modules = new Dictionary<string, HashSet<object>>(StringComparer.OrdinalIgnoreCase);

            foreach (var type in assembly.Modules.SelectMany(m => m.Types))
            {
                foreach (var method in type.Methods)
                {
                    try
                    {
                        foreach (var ca in method.CustomAttributes)
                        {
                            var at = ca.AttributeType.FullName;
                            if (!string.IsNullOrEmpty(at) && at.EndsWith("DllImportAttribute"))
                            {
                                var dllName = ca.ConstructorArguments.Count > 0 ? ca.ConstructorArguments[0].Value?.ToString() ?? string.Empty : string.Empty;
                                if (string.IsNullOrEmpty(dllName))
                                    continue;

                                if (!modules.TryGetValue(dllName, out var set))
                                {
                                    set = new HashSet<object>();
                                    modules[dllName] = set;
                                }

                                set.Add(new { Type = type.FullName, Method = method.Name.String });
                            }
                        }
                    }
                    catch { /* tolerate metadata issues */ }
                }
            }

            var list = modules.Select(kvp => new
            {
                ModuleName = kvp.Key,
                PathHint = string.Empty,
                ImportedBy = kvp.Value.ToList()
            }).ToList();

            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = json } } };
        }

        /// <summary>
        /// Extract exported symbols from a native PE file.
        /// Parameters: { "path": string } - path to the native DLL on disk.
        /// </summary>
        public CallToolResult ExtractNativeExports(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("path", out var pathObj))
                throw new ArgumentException("path is required");

            var path = pathObj.ToString() ?? string.Empty;
            if (!File.Exists(path))
                throw new ArgumentException($"File not found: {path}");

            try
            {
                using var fs = File.OpenRead(path);
                using var br = new BinaryReader(fs);

                // DOS header
                fs.Seek(0x3C, SeekOrigin.Begin);
                int e_lfanew = br.ReadInt32();
                fs.Seek(e_lfanew, SeekOrigin.Begin);

                // PE signature
                var sig = br.ReadUInt32();
                if (sig != 0x00004550) // "PE\0\0"
                    throw new ArgumentException("Not a PE file");

                // COFF header
                ushort machine = br.ReadUInt16();
                ushort numberOfSections = br.ReadUInt16();
                br.BaseStream.Seek(12, SeekOrigin.Current); // skip time/date, ptr sym, num symbols
                ushort sizeOfOptionalHeader = br.ReadUInt16();
                br.BaseStream.Seek(2, SeekOrigin.Current); // characteristics

                // Optional header magic
                long optionalHeaderStart = br.BaseStream.Position;
                ushort magic = br.ReadUInt16();
                bool isPE32Plus = magic == 0x20b;
                br.BaseStream.Seek(optionalHeaderStart + sizeOfOptionalHeader, SeekOrigin.Begin);

                // Read section headers
                fs.Seek(e_lfanew + 4 + 20 + sizeOfOptionalHeader, SeekOrigin.Begin);
                var sections = new List<(uint VirtualAddress, uint VirtualSize, uint PointerToRawData, uint SizeOfRawData)>();
                for (int i = 0; i < numberOfSections; i++)
                {
                    var nameBytes = br.ReadBytes(8);
                    uint virtualSize = br.ReadUInt32();
                    uint virtualAddress = br.ReadUInt32();
                    uint sizeOfRawData = br.ReadUInt32();
                    uint pointerToRawData = br.ReadUInt32();
                    br.BaseStream.Seek(16, SeekOrigin.Current); // skip remaining section header fields
                    sections.Add((virtualAddress, virtualSize, pointerToRawData, sizeOfRawData));
                }

                // Re-read optional header to find DataDirectory for exports
                fs.Seek(optionalHeaderStart, SeekOrigin.Begin);
                br.ReadUInt16(); // magic
                br.BaseStream.Seek(isPE32Plus ? 222 : 206, SeekOrigin.Current); // offset to data directories varies; this is an approximation that works for common headers
                uint exportRva = br.ReadUInt32();
                uint exportSize = br.ReadUInt32();

                if (exportRva == 0 || exportSize == 0)
                {
                    return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = "No export directory found" } } };
                }

                // Helper: map RVA -> file offset
                long RvaToOffset(uint rva)
                {
                    foreach (var s in sections)
                    {
                        if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData))
                        {
                            return (long)(s.PointerToRawData + (rva - s.VirtualAddress));
                        }
                    }
                    throw new ArgumentException("RVA not contained in any section");
                }

                // Read IMAGE_EXPORT_DIRECTORY (40 bytes)
                fs.Seek(RvaToOffset(exportRva), SeekOrigin.Begin);
                uint Characteristics = br.ReadUInt32();
                uint TimeDateStamp = br.ReadUInt32();
                ushort MajorVersion = br.ReadUInt16();
                ushort MinorVersion = br.ReadUInt16();
                uint NameRVA = br.ReadUInt32();
                uint Base = br.ReadUInt32();
                uint NumberOfFunctions = br.ReadUInt32();
                uint NumberOfNames = br.ReadUInt32();
                uint AddressOfFunctions = br.ReadUInt32();
                uint AddressOfNames = br.ReadUInt32();
                uint AddressOfNameOrdinals = br.ReadUInt32();

                var exports = new List<object>();
                for (uint i = 0; i < NumberOfNames; i++)
                {
                    var nameRvaOffset = RvaToOffset(AddressOfNames + i * 4);
                    fs.Seek(nameRvaOffset, SeekOrigin.Begin);
                    uint nameRva = br.ReadUInt32();
                    var nameOffset = RvaToOffset(nameRva);
                    fs.Seek(nameOffset, SeekOrigin.Begin);
                    var sb = new StringBuilder();
                    int b;
                    while ((b = fs.ReadByte()) != 0 && b != -1)
                        sb.Append((char)b);
                    string exportName = sb.ToString();

                    var ordinalOffset = RvaToOffset(AddressOfNameOrdinals + i * 2);
                    fs.Seek(ordinalOffset, SeekOrigin.Begin);
                    ushort ordinal = br.ReadUInt16();
                    var functionRvaOffset = RvaToOffset(AddressOfFunctions + ((uint)ordinal * 4));
                    fs.Seek(functionRvaOffset, SeekOrigin.Begin);
                    uint functionRva = br.ReadUInt32();

                    exports.Add(new { Name = exportName, Ordinal = Base + ordinal, RVA = functionRva });
                }

                var json = JsonSerializer.Serialize(new { Path = path, Exports = exports }, new JsonSerializerOptions { WriteIndented = true });
                return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = json } } };
            }
            catch (Exception ex)
            {
                McpLogger.Exception(ex, "ExtractNativeExports failed");
                return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = $"Error extracting exports: {ex.Message}" } }, IsError = true };
            }
        }

        AssemblyDef? FindAssemblyByName(string name)
        {
            return documentTreeView.GetAllModuleNodes()
                .Select(m => m.Document?.AssemblyDef)
                .FirstOrDefault(a => a != null && a.Name.String.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
    }
}