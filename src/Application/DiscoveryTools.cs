using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.MCP.Server.Contracts;

namespace dnSpy.MCP.Server.Application
{
    [Export(typeof(DiscoveryTools))]
    public sealed class DiscoveryTools
    {
        readonly IDocumentTreeView documentTreeView;

        [ImportingConstructor]
        public DiscoveryTools(IDocumentTreeView documentTreeView)
        {
            this.documentTreeView = documentTreeView;
        }

        public CallToolResult SearchTypes(Dictionary<string, object>? arguments)
        {
            var query = RequireString(arguments, "query");

            string? cursor = null;
            if (arguments?.TryGetValue("cursor", out var cursorObj) == true)
                cursor = cursorObj?.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            Regex? regex = null;
            if (query.Contains("*", StringComparison.Ordinal))
            {
                var regexPattern = "^" + Regex.Escape(query).Replace("\\*", ".*") + "$";
                regex = new Regex(regexPattern, RegexOptions.IgnoreCase);
            }

            var results = documentTreeView.GetAllModuleNodes()
                .SelectMany(m => m.Document?.AssemblyDef != null
                    ? m.Document.AssemblyDef.Modules.SelectMany(mod => mod.Types)
                    : Enumerable.Empty<TypeDef>())
                .Where(t => regex != null
                    ? regex.IsMatch(t.FullName)
                    : t.FullName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(t => new
                {
                    FullName = t.FullName,
                    Namespace = t.Namespace.String,
                    Name = t.Name.String,
                    AssemblyName = t.Module.Assembly?.Name.String ?? "Unknown"
                })
                .ToList();

            return CreatePaginatedJsonResponse(results, offset, pageSize);
        }

        public CallToolResult FindWhoCallsMethod(Dictionary<string, object>? arguments)
        {
            var asmName = RequireString(arguments, "assembly_name");
            var typeName = RequireString(arguments, "type_full_name");
            var methodName = RequireString(arguments, "method_name");

            var assembly = FindAssemblyByName(asmName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {asmName}");

            var type = FindTypeInAssembly(assembly, typeName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeName}");

            var targetMethod = type.Methods.FirstOrDefault(m => m.Name.String == methodName);
            if (targetMethod == null)
                throw new ArgumentException($"Method not found: {methodName}");

            var targetFullName = targetMethod.FullName;
            var assemblies = System.Windows.Application.Current.Dispatcher.Invoke(() =>
                documentTreeView.GetAllModuleNodes()
                    .Select(m => m.Document?.AssemblyDef)
                    .Where(a => a != null)
                    .ToList());

            var callers = assemblies
                .SelectMany(a => a!.Modules)
                .SelectMany(GetAllTypesRecursive)
                .SelectMany(t => t.Methods)
                .Where(m => m.Body?.Instructions != null)
                .SelectMany(m => m.Body.Instructions
                    .Where(instr =>
                        (instr.OpCode.Code == Code.Call || instr.OpCode.Code == Code.Callvirt) &&
                        instr.Operand is MethodDef calledDef && calledDef.FullName == targetFullName)
                    .Select(_ => new
                    {
                        MethodName = m.Name.String,
                        DeclaringType = m.DeclaringType?.FullName ?? "Unknown",
                        AssemblyName = m.DeclaringType?.Module?.Assembly?.Name.String ?? "Unknown"
                    }))
                .OrderBy(c => c.AssemblyName)
                .ThenBy(c => c.DeclaringType)
                .ThenBy(c => c.MethodName)
                .ToList();

            var resultJson = JsonSerializer.Serialize(new
            {
                TargetMethod = targetFullName,
                CallerCount = callers.Count,
                Callers = callers
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = resultJson } }
            };
        }

        public CallToolResult AnalyzeTypeInheritance(Dictionary<string, object>? arguments)
        {
            var asmName = RequireString(arguments, "assembly_name");
            var typeName = RequireString(arguments, "type_full_name");

            var assembly = FindAssemblyByName(asmName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {asmName}");

            var type = FindTypeInAssembly(assembly, typeName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeName}");

            var baseClasses = new List<string>();
            var currentType = type.BaseType;
            while (currentType != null && currentType.FullName != "System.Object")
            {
                baseClasses.Add(currentType.FullName);
                var typeDef = currentType.ResolveTypeDef();
                currentType = typeDef?.BaseType;
            }

            var interfaces = type.Interfaces.Select(i => i.Interface.FullName).ToList();

            var result = JsonSerializer.Serialize(new
            {
                Type = type.FullName,
                BaseClasses = baseClasses,
                Interfaces = interfaces
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = result } }
            };
        }

        AssemblyDef? FindAssemblyByName(string name) =>
            documentTreeView.GetAllModuleNodes()
                .Select(m => m.Document?.AssemblyDef)
                .FirstOrDefault(a => a?.Name.String.Equals(name, StringComparison.OrdinalIgnoreCase) == true);

        static TypeDef? FindTypeInAssembly(AssemblyDef assembly, string typeFullName) =>
            assembly.Modules
                .SelectMany(m => m.Types)
                .FirstOrDefault(t => t.FullName.Equals(typeFullName, StringComparison.OrdinalIgnoreCase));

        static IEnumerable<TypeDef> GetAllTypesRecursive(ModuleDef module)
        {
            foreach (var type in module.Types)
            {
                yield return type;
                foreach (var nested in GetAllNestedTypesRecursive(type))
                    yield return nested;
            }
        }

        static IEnumerable<TypeDef> GetAllNestedTypesRecursive(TypeDef type)
        {
            foreach (var nested in type.NestedTypes)
            {
                yield return nested;
                foreach (var deep in GetAllNestedTypesRecursive(nested))
                    yield return deep;
            }
        }

        static (int offset, int pageSize) DecodeCursor(string? cursor)
        {
            if (string.IsNullOrEmpty(cursor))
                return (0, 50);

            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
                var parts = decoded.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out var offset) && int.TryParse(parts[1], out var pageSize))
                    return (offset, pageSize);
            }
            catch
            {
            }

            return (0, 10);
        }

        static string EncodeCursor(int offset, int pageSize) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{offset}:{pageSize}"));

        static CallToolResult CreatePaginatedJsonResponse<T>(List<T> items, int offset, int pageSize)
        {
            var pagedItems = items.Skip(offset).Take(pageSize).ToList();
            var hasMore = offset + pageSize < items.Count;

            var result = new Dictionary<string, object>
            {
                ["items"] = pagedItems,
                ["total_count"] = items.Count,
                ["returned_count"] = pagedItems.Count,
                ["offset"] = offset
            };

            if (hasMore)
                result["nextCursor"] = EncodeCursor(offset + pageSize, pageSize);

            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = json } }
            };
        }

        static string RequireString(Dictionary<string, object>? args, string key)
        {
            if (args == null || !args.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value?.ToString()))
                throw new ArgumentException($"{key} is required");
            return value!.ToString()!;
        }
    }
}
