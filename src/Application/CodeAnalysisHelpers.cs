using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Documents.TreeView;

namespace dnSpy.MCP.Server.Application
{
    /// <summary>
    /// Phase 5: Code Analysis Helpers
    /// Provides utilities for analyzing call graphs, dependencies, data flow, and dead code.
    /// </summary>
    public sealed class CodeAnalysisHelpers
    {
        private readonly IDocumentTreeView documentTreeView;
        private readonly UsageFindingCommandTools usageTools;

        public CodeAnalysisHelpers(IDocumentTreeView documentTreeView, UsageFindingCommandTools usageTools)
        {
            this.documentTreeView = documentTreeView ?? throw new ArgumentNullException(nameof(documentTreeView));
            this.usageTools = usageTools ?? throw new ArgumentNullException(nameof(usageTools));
        }

        /// <summary>
        /// Build a call graph for a specific method, showing all methods it calls recursively.
        /// </summary>
        public Dictionary<string, object> BuildCallGraph(MethodDef targetMethod, int maxDepth = 5)
        {
            var visited = new HashSet<string>();
            var callGraph = BuildCallGraphRecursive(targetMethod, maxDepth, 0, visited);
            return callGraph;
        }

        private Dictionary<string, object> BuildCallGraphRecursive(MethodDef method, int maxDepth, int currentDepth, HashSet<string> visited)
        {
            if (method == null || currentDepth >= maxDepth)
                return new Dictionary<string, object> { ["error"] = "Max depth reached or null method" };

            string methodFullName = method.FullName;
            if (visited.Contains(methodFullName))
                return new Dictionary<string, object> { ["recursive"] = true };

            visited.Add(methodFullName);

            var callsTo = new List<Dictionary<string, object>>();

            // Find all methods called by this method
            if (method.Body?.Instructions != null)
            {
                foreach (var instr in method.Body.Instructions)
                {
                    if ((instr.OpCode.Code == dnlib.DotNet.Emit.Code.Call ||
                         instr.OpCode.Code == dnlib.DotNet.Emit.Code.Callvirt) &&
                        instr.Operand is MethodDef calledMethod)
                    {
                        if (!visited.Contains(calledMethod.FullName))
                        {
                            var subGraph = BuildCallGraphRecursive(calledMethod, maxDepth, currentDepth + 1, visited);
                            callsTo.Add(new Dictionary<string, object>
                            {
                                ["method"] = calledMethod.FullName,
                                ["type"] = calledMethod.DeclaringType?.FullName ?? "Unknown",
                                ["assembly"] = calledMethod.DeclaringType?.Module?.Assembly?.Name.String ?? "Unknown",
                                ["subgraph"] = subGraph
                            });
                        }
                    }
                }
            }

            return new Dictionary<string, object>
            {
                ["method"] = methodFullName,
                ["type"] = method.DeclaringType?.FullName ?? "Unknown",
                ["assembly"] = method.DeclaringType?.Module?.Assembly?.Name.String ?? "Unknown",
                ["depth"] = currentDepth,
                ["calls_to"] = callsTo,
                ["call_count"] = callsTo.Count
            };
        }

        /// <summary>
        /// Find all dependency paths between two types using BFS.
        /// </summary>
        public List<List<string>> FindDependencyPaths(TypeDef sourceType, TypeDef targetType, int maxPathLength = 10)
        {
            if (sourceType == null || targetType == null)
                return new List<List<string>>();

            var paths = new List<List<string>>();
            var queue = new Queue<(TypeDef current, List<string> path)>();
            var visited = new HashSet<string> { sourceType.FullName };

            queue.Enqueue((sourceType, new List<string> { sourceType.FullName }));

            while (queue.Count > 0)
            {
                var (currentType, path) = queue.Dequeue();

                if (path.Count > maxPathLength)
                    continue;

                // Find types that currentType references
                var referencedTypes = GetTypeDependencies(currentType);

                foreach (var refType in referencedTypes)
                {
                    if (refType.FullName == targetType.FullName)
                    {
                        var completePath = new List<string>(path) { refType.FullName };
                        paths.Add(completePath);
                    }
                    else if (!visited.Contains(refType.FullName))
                    {
                        visited.Add(refType.FullName);
                        var newPath = new List<string>(path) { refType.FullName };
                        queue.Enqueue((refType, newPath));
                    }
                }
            }

            return paths;
        }

        /// <summary>
        /// Get all types that a given type directly depends on.
        /// </summary>
        private List<TypeDef> GetTypeDependencies(TypeDef type)
        {
            var dependencies = new HashSet<TypeDef>();

            // Base type
            if (type.BaseType?.ResolveTypeDef() is TypeDef baseDef)
                dependencies.Add(baseDef);

            // Interfaces
            foreach (var iface in type.Interfaces)
            {
                if (iface.Interface?.ResolveTypeDef() is TypeDef ifaceDef)
                    dependencies.Add(ifaceDef);
            }

            // Field types
            foreach (var field in type.Fields)
            {
                if (field.FieldType?.ToTypeDefOrRef()?.ResolveTypeDef() is TypeDef fieldTypeDef)
                    dependencies.Add(fieldTypeDef);
            }

            // Method parameter and return types
            foreach (var method in type.Methods)
            {
                if (method.ReturnType?.ToTypeDefOrRef()?.ResolveTypeDef() is TypeDef returnTypeDef)
                    dependencies.Add(returnTypeDef);

                foreach (var param in method.Parameters)
                {
                    if (param.Type?.ToTypeDefOrRef()?.ResolveTypeDef() is TypeDef paramTypeDef)
                        dependencies.Add(paramTypeDef);
                }
            }

            return dependencies.ToList();
        }

        /// <summary>
        /// Compute a dependency matrix for all loaded assemblies.
        /// Returns a dictionary: assembly -> list of assemblies it depends on.
        /// </summary>
        public Dictionary<string, List<string>> ComputeAssemblyDependencies()
        {
            var result = new Dictionary<string, List<string>>();
            var assemblies = documentTreeView.GetAllModuleNodes()
                .Select(m => m.Document?.AssemblyDef)
                .Where(a => a != null)
                .Distinct()
                .ToList();

            foreach (var assembly in assemblies)
            {
                var deps = new HashSet<string>();
                var assemblyName = assembly!.Name.String;

                foreach (var module in assembly.Modules)
                {
                    foreach (var type in GetAllTypesRecursive(module))
                    {
                        // Check base type assembly
                        if (type.BaseType?.Module?.Assembly != null &&
                            type.BaseType.Module.Assembly.Name.String != assemblyName)
                            deps.Add(type.BaseType.Module.Assembly.Name.String);

                        // Check interface assemblies
                        foreach (var iface in type.Interfaces)
                        {
                            if (iface.Interface?.Module?.Assembly != null &&
                                iface.Interface.Module.Assembly.Name.String != assemblyName)
                                deps.Add(iface.Interface.Module.Assembly.Name.String);
                        }

                        // Check field type assemblies
                        foreach (var field in type.Fields)
                        {
                            if (field.FieldType?.Module?.Assembly != null &&
                                field.FieldType.Module.Assembly.Name.String != assemblyName)
                                deps.Add(field.FieldType.Module.Assembly.Name.String);
                        }

                        // Check method assemblies
                        foreach (var method in type.Methods)
                        {
                            if (method.ReturnType?.Module?.Assembly != null &&
                                method.ReturnType.Module.Assembly.Name.String != assemblyName)
                                deps.Add(method.ReturnType.Module.Assembly.Name.String);

                            foreach (var param in method.Parameters)
                            {
                                if (param.Type?.Module?.Assembly != null &&
                                    param.Type.Module.Assembly.Name.String != assemblyName)
                                    deps.Add(param.Type.Module.Assembly.Name.String);
                            }
                        }
                    }
                }

                result[assemblyName] = deps.ToList();
            }

            return result;
        }

        /// <summary>
        /// Identify dead code (methods and types never called/referenced).
        /// </summary>
        public (List<string> DeadMethods, List<string> DeadTypes) IdentifyDeadCode(AssemblyDef assembly, bool includePrivate = true)
        {
            var deadMethods = new List<string>();
            var deadTypes = new List<string>();

            var allMethods = new HashSet<string>();
            var calledMethods = new HashSet<string>();
            var allTypes = new HashSet<string>();
            var usedTypes = new HashSet<string>();

            // Collect all methods and types
            foreach (var module in assembly.Modules)
            {
                foreach (var type in GetAllTypesRecursive(module))
                {
                    allTypes.Add(type.FullName);

                    foreach (var method in type.Methods)
                    {
                        if (includePrivate || method.IsPublic || method.IsFamily)
                            allMethods.Add(method.FullName);
                    }
                }
            }

            // Find which methods are called
            foreach (var module in assembly.Modules)
            {
                foreach (var type in GetAllTypesRecursive(module))
                {
                    foreach (var method in type.Methods)
                    {
                        if (method.Body?.Instructions != null)
                        {
                            foreach (var instr in method.Body.Instructions)
                            {
                                if ((instr.OpCode.Code == dnlib.DotNet.Emit.Code.Call ||
                                     instr.OpCode.Code == dnlib.DotNet.Emit.Code.Callvirt) &&
                                    instr.Operand is MethodDef calledMethod)
                                {
                                    calledMethods.Add(calledMethod.FullName);
                                }
                            }
                        }
                    }
                }
            }

            // Find dead methods
            deadMethods = allMethods.Except(calledMethods).ToList();

            // Find dead types (not referenced)
            foreach (var type in allTypes)
            {
                // This is a simplified check - in reality would need full reference analysis
                // For now, check if type is used as base, interface, field, or parameter
                if (IsTypeReferenced(type, assembly))
                    usedTypes.Add(type);
            }

            deadTypes = allTypes.Except(usedTypes).ToList();

            return (deadMethods, deadTypes);
        }

        /// <summary>
        /// Check if a type is referenced anywhere in the assembly.
        /// </summary>
        private bool IsTypeReferenced(string typeFullName, AssemblyDef assembly)
        {
            foreach (var module in assembly.Modules)
            {
                foreach (var type in GetAllTypesRecursive(module))
                {
                    // Check base type
                    if (type.BaseType?.FullName == typeFullName)
                        return true;

                    // Check interfaces
                    foreach (var iface in type.Interfaces)
                    {
                        if (iface.Interface?.FullName == typeFullName)
                            return true;
                    }

                    // Check fields
                    foreach (var field in type.Fields)
                    {
                        if (field.FieldType.FullName == typeFullName)
                            return true;
                    }

                    // Check methods
                    foreach (var method in type.Methods)
                    {
                        if (method.ReturnType.FullName == typeFullName)
                            return true;

                        foreach (var param in method.Parameters)
                        {
                            if (param.Type.FullName == typeFullName)
                                return true;
                        }
                    }
                }
            }

            return false;
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
    }
}
