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
using dnlib.DotNet;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.MCP.Server.Contracts;
using dnSpy.MCP.Server.Helper;
using System.Text.Json.Serialization;

namespace dnSpy.MCP.Server.Application
{
    /// <summary>
    /// Phase 4: Usage Finding Commands
    /// Provides IL analysis for tracking method calls and field access patterns.
    /// </summary>
    [Export(typeof(UsageFindingCommandTools))]
    public sealed class UsageFindingCommandTools
    {
        private readonly IDocumentTreeView documentTreeView;

        [ImportingConstructor]
        public UsageFindingCommandTools(IDocumentTreeView documentTreeView)
        {
            this.documentTreeView = documentTreeView ?? throw new ArgumentNullException(nameof(documentTreeView));
        }

        /// <summary>
        /// Helper: Get all types including nested types from a module.
        /// </summary>
        private IEnumerable<TypeDef> GetAllTypesRecursive(ModuleDef module)
        {
            foreach (var type in module.Types)
            {
                yield return type;
                foreach (var nested in GetNestedTypesRecursive(type))
                    yield return nested;
            }
        }

        /// <summary>
        /// Helper: Get all nested types recursively.
        /// </summary>
        private IEnumerable<TypeDef> GetNestedTypesRecursive(TypeDef type)
        {
            foreach (var nested in type.NestedTypes)
            {
                yield return nested;
                foreach (var deepNested in GetNestedTypesRecursive(nested))
                    yield return deepNested;
            }
        }

        /// <summary>
        /// Helper: Get all method definitions from loaded assemblies.
        /// </summary>
        private IEnumerable<MethodDef> GetAllMethodDefinitions()
        {
            var assemblies = UiThreadHelper.Invoke(() =>
                documentTreeView.GetAllModuleNodes()
                    .Select(m => m.Document?.AssemblyDef)
                    .Where(a => a != null)
                    .ToList());

            return assemblies
                .SelectMany(a => a!.Modules)
                .SelectMany(m => GetAllTypesRecursive(m))
                .SelectMany(t => t.Methods)
                .Where(m => m.Body != null);
        }

        /// <summary>
        /// Helper: Find all methods that call a specific target method.
        /// Analyzes IL instructions for CALL and CALLVIRT opcodes.
        /// </summary>
        private List<(string MethodName, string TypeName, string AssemblyName)> FindMethodCallersInIL(MethodDef targetMethod)
        {
            var callers = new List<(string, string, string)>();

            foreach (var method in GetAllMethodDefinitions())
            {
                if (method.Body?.Instructions == null)
                    continue;

                foreach (var instr in method.Body.Instructions)
                {
                    if ((instr.OpCode.Code == dnlib.DotNet.Emit.Code.Call ||
                         instr.OpCode.Code == dnlib.DotNet.Emit.Code.Callvirt) &&
                        instr.Operand is MethodDef calledMethod)
                    {
                        if (calledMethod.FullName == targetMethod.FullName)
                        {
                            var assemblyName = method.DeclaringType?.Module?.Assembly?.Name.String ?? "Unknown";
                            callers.Add((method.Name.String, method.DeclaringType?.FullName ?? "Unknown", assemblyName));
                        }
                    }
                }
            }

            return callers;
        }

        /// <summary>
        /// Helper: Find all methods that read a specific field.
        /// Analyzes IL instructions for LDFLD and LDSFLD opcodes.
        /// </summary>
        private List<(string MethodName, string TypeName, string AssemblyName)> FindFieldReadersInIL(FieldDef targetField)
        {
            var readers = new List<(string, string, string)>();

            foreach (var method in GetAllMethodDefinitions())
            {
                if (method.Body?.Instructions == null)
                    continue;

                foreach (var instr in method.Body.Instructions)
                {
                    if ((instr.OpCode.Code == dnlib.DotNet.Emit.Code.Ldfld ||
                         instr.OpCode.Code == dnlib.DotNet.Emit.Code.Ldsfld) &&
                        instr.Operand is FieldDef readField)
                    {
                        if (readField.FullName == targetField.FullName)
                        {
                            var assemblyName = method.DeclaringType?.Module?.Assembly?.Name.String ?? "Unknown";
                            readers.Add((method.Name.String, method.DeclaringType?.FullName ?? "Unknown", assemblyName));
                        }
                    }
                }
            }

            return readers;
        }

        /// <summary>
        /// Helper: Find all methods that write to a specific field.
        /// Analyzes IL instructions for STFLD and STSFLD opcodes.
        /// </summary>
        private List<(string MethodName, string TypeName, string AssemblyName)> FindFieldWritersInIL(FieldDef targetField)
        {
            var writers = new List<(string, string, string)>();

            foreach (var method in GetAllMethodDefinitions())
            {
                if (method.Body?.Instructions == null)
                    continue;

                foreach (var instr in method.Body.Instructions)
                {
                    if ((instr.OpCode.Code == dnlib.DotNet.Emit.Code.Stfld ||
                         instr.OpCode.Code == dnlib.DotNet.Emit.Code.Stsfld) &&
                        instr.Operand is FieldDef writtenField)
                    {
                        if (writtenField.FullName == targetField.FullName)
                        {
                            var assemblyName = method.DeclaringType?.Module?.Assembly?.Name.String ?? "Unknown";
                            writers.Add((method.Name.String, method.DeclaringType?.FullName ?? "Unknown", assemblyName));
                        }
                    }
                }
            }

            return writers;
        }

        /// <summary>
        /// Helper: Build a reference graph showing where a type is used.
        /// Checks type references in method signatures, fields, and type hierarchy.
        /// </summary>
        private List<(string Context, string UsageLocus, string AssemblyName)> BuildTypeReferenceGraph(TypeDef targetType)
        {
            var usages = new List<(string, string, string)>();

            var loadedAssemblies = UiThreadHelper.Invoke(() =>
                documentTreeView.GetAllModuleNodes()
                    .Select(m => m.Document?.AssemblyDef)
                    .Where(a => a != null)
                    .ToList());

            foreach (var assembly in loadedAssemblies)
            {
                var assemblyName = assembly!.Name.String;

                foreach (var module in assembly.Modules)
                {
                    foreach (var type in GetAllTypesRecursive(module))
                    {
                        if (type.BaseType?.FullName == targetType.FullName && type.FullName != targetType.FullName)
                            usages.Add(("BaseType", type.FullName, assemblyName));

                        foreach (var iface in type.Interfaces)
                        {
                            if (iface.Interface?.FullName == targetType.FullName)
                                usages.Add(("Interface", type.FullName, assemblyName));
                        }

                        foreach (var field in type.Fields)
                        {
                            if (field.FieldType.FullName == targetType.FullName)
                                usages.Add(("FieldType", $"{type.FullName}.{field.Name}", assemblyName));
                        }

                        foreach (var method in type.Methods)
                        {
                            if (method.ReturnType.FullName == targetType.FullName)
                                usages.Add(("ReturnType", method.FullName, assemblyName));

                            foreach (var param in method.Parameters)
                            {
                                if (param.Type.FullName == targetType.FullName)
                                    usages.Add(("ParameterType", method.FullName, assemblyName));
                            }
                        }
                    }
                }
            }

            return usages;
        }

        /// <summary>
        /// Find all types, methods, and fields that reference a specific type.
        /// </summary>
        public CallToolResult FindWhoUsesType(AssemblyDef? assembly, TypeDef? targetType)
        {
            if (targetType == null)
                throw new ArgumentException("Target type not found");

            var usages = BuildTypeReferenceGraph(targetType);

            var groupedUsages = usages
                .GroupBy(u => u.Context)
                .OrderBy(g => g.Key)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(u => new
                    {
                        Location = u.UsageLocus,
                        AssemblyName = u.AssemblyName
                    }).OrderBy(x => x.AssemblyName).ThenBy(x => x.Location).ToList()
                );

            var result = JsonSerializer.Serialize(new
            {
                TargetType = targetType.FullName,
                TotalUsages = usages.Count,
                UsagesByContext = groupedUsages,
                AllUsages = usages.Select(u => new
                {
                    Context = u.Context,
                    Location = u.UsageLocus,
                    AssemblyName = u.AssemblyName
                }).OrderBy(x => x.AssemblyName).ThenBy(x => x.Context).ThenBy(x => x.Location).ToList()
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = result } }
            };
        }

        /// <summary>
        /// Find all methods that call a specific method.
        /// </summary>
        public CallToolResult FindWhoCallsMethod(MethodDef? targetMethod)
        {
            if (targetMethod == null)
                throw new ArgumentException("Target method not found");

            var callers = FindMethodCallersInIL(targetMethod);

            var result = JsonSerializer.Serialize(new
            {
                TargetMethod = targetMethod.FullName,
                CallerCount = callers.Count,
                Callers = callers.Select(c => new
                {
                    MethodName = c.MethodName,
                    DeclaringType = c.TypeName,
                    AssemblyName = c.AssemblyName
                }).OrderBy(x => x.AssemblyName).ThenBy(x => x.DeclaringType).ThenBy(x => x.MethodName).ToList()
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = result } }
            };
        }

        /// <summary>
        /// Find all methods that read a specific field.
        /// </summary>
        public CallToolResult FindWhoReadsField(FieldDef? targetField)
        {
            if (targetField == null)
                throw new ArgumentException("Target field not found");

            var readers = FindFieldReadersInIL(targetField);

            var result = JsonSerializer.Serialize(new
            {
                TargetField = targetField.FullName,
                ReaderCount = readers.Count,
                Readers = readers.Select(r => new
                {
                    MethodName = r.MethodName,
                    DeclaringType = r.TypeName,
                    AssemblyName = r.AssemblyName
                }).OrderBy(x => x.AssemblyName).ThenBy(x => x.DeclaringType).ThenBy(x => x.MethodName).ToList()
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = result } }
            };
        }

        /// <summary>
        /// Find all methods that write to a specific field.
        /// </summary>
        public CallToolResult FindWhoWritesField(FieldDef? targetField)
        {
            if (targetField == null)
                throw new ArgumentException("Target field not found");

            var writers = FindFieldWritersInIL(targetField);

            var result = JsonSerializer.Serialize(new
            {
                TargetField = targetField.FullName,
                WriterCount = writers.Count,
                Writers = writers.Select(w => new
                {
                    MethodName = w.MethodName,
                    DeclaringType = w.TypeName,
                    AssemblyName = w.AssemblyName
                }).OrderBy(x => x.AssemblyName).ThenBy(x => x.DeclaringType).ThenBy(x => x.MethodName).ToList()
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = result } }
            };
        }

        // ── MCP-compatible wrappers (accept Dictionary<string,object>) ───────────

        /// <summary>MCP wrapper for find_who_uses_type.</summary>
        public CallToolResult FindWhoUsesTypeArgs(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var asmObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeObj))
                throw new ArgumentException("type_full_name is required");

            var asmName  = asmObj.ToString()  ?? "";
            var typeName = typeObj.ToString() ?? "";

            var assembly = FindAssemblyByName(asmName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {asmName}");

            var targetType = GetAllAssemblyTypes(assembly)
                .FirstOrDefault(t => t.FullName.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            if (targetType == null)
                throw new ArgumentException($"Type not found: {typeName}");

            return FindWhoUsesType(assembly, targetType);
        }

        /// <summary>MCP wrapper for find_who_reads_field.</summary>
        public CallToolResult FindWhoReadsFieldArgs(Dictionary<string, object>? arguments)
        {
            var (assembly, type, fieldName) = ResolveFieldArgs(arguments, "find_who_reads_field");
            var field = type.Fields.FirstOrDefault(f => f.Name.String.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
            if (field == null)
                throw new ArgumentException($"Field not found: {fieldName}");
            return FindWhoReadsField(field);
        }

        /// <summary>MCP wrapper for find_who_writes_field.</summary>
        public CallToolResult FindWhoWritesFieldArgs(Dictionary<string, object>? arguments)
        {
            var (assembly, type, fieldName) = ResolveFieldArgs(arguments, "find_who_writes_field");
            var field = type.Fields.FirstOrDefault(f => f.Name.String.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
            if (field == null)
                throw new ArgumentException($"Field not found: {fieldName}");
            return FindWhoWritesField(field);
        }

        // ── Private lookup helpers ───────────────────────────────────────────────

        private AssemblyDef? FindAssemblyByName(string name) =>
            UiThreadHelper.Invoke(() =>
                documentTreeView.GetAllModuleNodes()
                    .Select(m => m.Document?.AssemblyDef)
                    .FirstOrDefault(a => a?.Name.String.Equals(name, StringComparison.OrdinalIgnoreCase) == true));

        private IEnumerable<TypeDef> GetAllAssemblyTypes(AssemblyDef asm) =>
            asm.Modules.SelectMany(m => GetAllTypesRecursive(m));

        private (AssemblyDef asm, TypeDef type, string fieldName) ResolveFieldArgs(
            Dictionary<string, object>? arguments, string toolName)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var asmObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeObj))
                throw new ArgumentException("type_full_name is required");
            if (!arguments.TryGetValue("field_name", out var fieldObj))
                throw new ArgumentException("field_name is required");

            var asmName   = asmObj.ToString()   ?? "";
            var typeName  = typeObj.ToString()  ?? "";
            var fieldName = fieldObj.ToString() ?? "";

            var assembly = FindAssemblyByName(asmName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {asmName}");

            var type = GetAllAssemblyTypes(assembly)
                .FirstOrDefault(t => t.FullName.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            if (type == null)
                throw new ArgumentException($"Type not found: {typeName}");

            return (assembly, type, fieldName);
        }
    }
}
