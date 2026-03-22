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
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.MCP.Server.Contracts;
using Echo.ControlFlow;
using Echo.Platforms.Dnlib;

namespace dnSpy.MCP.Server.Application
{
    [Export(typeof(ControlFlowTools))]
    public sealed class ControlFlowTools
    {
        readonly IDocumentTreeView documentTreeView;

        [ImportingConstructor]
        public ControlFlowTools(IDocumentTreeView documentTreeView)
        {
            this.documentTreeView = documentTreeView ?? throw new ArgumentNullException(nameof(documentTreeView));
        }

        public CallToolResult GetControlFlowGraph(Dictionary<string, object>? arguments)
        {
            var target = ResolveMethodTarget(arguments);
            var graph = BuildGraph(target.Method);
            var blockMap = BuildBlockMaps(graph);
            var edges = graph.GetEdges()
                .Select(edge => new EdgeInfo
                {
                    From = blockMap.IdByOffset[edge.Origin.Offset],
                    To = blockMap.IdByOffset[edge.Target.Offset],
                    Kind = NormalizeEdgeKind(edge.Type)
                })
                .OrderBy(edge => edge.From, StringComparer.Ordinal)
                .ThenBy(edge => edge.To, StringComparer.Ordinal)
                .ThenBy(edge => edge.Kind, StringComparer.Ordinal)
                .ToList();

            var exitBlocks = blockMap.Blocks.Count(block => block.IsExit);
            var hasLoops = edges.Any(edge =>
                blockMap.SortKeyById.TryGetValue(edge.From, out var fromKey) &&
                blockMap.SortKeyById.TryGetValue(edge.To, out var toKey) &&
                toKey <= fromKey);

            var payload = new
            {
                method = CreateMethodDescriptor(target),
                entry_block = blockMap.EntryBlockId,
                blocks = blockMap.Blocks,
                edges,
                metrics = new
                {
                    block_count = blockMap.Blocks.Count,
                    edge_count = edges.Count,
                    exit_blocks = exitBlocks,
                    has_loops = hasLoops
                }
            };

            return Serialize(payload);
        }

        public CallToolResult GetBasicBlocks(Dictionary<string, object>? arguments)
        {
            var target = ResolveMethodTarget(arguments);
            var graph = BuildGraph(target.Method);
            var blockMap = BuildBlockMaps(graph);

            var edgesByOrigin = graph.GetEdges()
                .GroupBy(edge => edge.Origin.Offset)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(edge => blockMap.IdByOffset[edge.Target.Offset])
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToList());

            var payload = new
            {
                method = CreateMethodDescriptor(target),
                blocks = blockMap.Blocks.Select(block => new
                {
                    id = block.Id,
                    il_start = block.IlStart,
                    il_end = block.IlEnd,
                    instruction_count = block.InstructionCount,
                    successors = edgesByOrigin.TryGetValue(blockMap.OffsetById[block.Id], out var successors)
                        ? successors
                        : new List<string>()
                }).ToList()
            };

            return Serialize(payload);
        }

        MethodTarget ResolveMethodTarget(Dictionary<string, object>? arguments)
        {
            var assemblyName = RequireString(arguments, "assembly_name");
            var typeFullName = RequireString(arguments, "type_full_name");
            var methodName = RequireString(arguments, "method_name");

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeFullName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeFullName}");

            var matches = type.Methods
                .Where(method => string.Equals(method.Name.String, methodName, StringComparison.Ordinal))
                .ToList();

            if (matches.Count == 0)
                throw new ArgumentException($"Method not found: {methodName}");
            if (matches.Count > 1)
                throw new ArgumentException($"Multiple methods found named '{methodName}' in type '{typeFullName}'. A unique method name is required.");

            var method = matches[0];
            if (method.MethodBody is not CilBody)
                throw new ArgumentException($"Method does not have a CIL body: {methodName}");

            return new MethodTarget(assembly, type, method);
        }

        ControlFlowGraph<Instruction> BuildGraph(MethodDef method)
        {
            try
            {
                return method.ConstructStaticFlowGraph();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Echo CFG construction failed for method '{method.FullName}': {ex.Message}", ex);
            }
        }

        BlockMap BuildBlockMaps(ControlFlowGraph<Instruction> graph)
        {
            var orderedNodes = graph.Nodes
                .OrderBy(node => node.Offset)
                .ToList();

            var idByOffset = new Dictionary<long, string>();
            var offsetById = new Dictionary<string, long>(StringComparer.Ordinal);
            var sortKeyById = new Dictionary<string, int>(StringComparer.Ordinal);
            var blocks = new List<BlockInfo>(orderedNodes.Count);

            for (var i = 0; i < orderedNodes.Count; i++)
            {
                var node = orderedNodes[i];
                var blockId = $"B{i}";
                idByOffset[node.Offset] = blockId;
                offsetById[blockId] = node.Offset;
                sortKeyById[blockId] = i;
            }

            foreach (var node in orderedNodes)
            {
                var instructions = node.Contents.Instructions;
                var firstInstruction = instructions.FirstOrDefault();
                var lastInstruction = instructions.LastOrDefault();
                var startOffset = firstInstruction != null ? (int)firstInstruction.Offset : (int)node.Offset;
                var endOffset = lastInstruction != null
                    ? (int)(lastInstruction.Offset + lastInstruction.GetSize())
                    : startOffset;

                blocks.Add(new BlockInfo
                {
                    Id = idByOffset[node.Offset],
                    IlStart = FormatOffset(startOffset),
                    IlEnd = FormatOffset(endOffset),
                    InstructionOffsets = instructions.Select(instr => FormatOffset((int)instr.Offset)).ToList(),
                    InstructionCount = instructions.Count,
                    IsEntry = ReferenceEquals(graph.Entrypoint, node),
                    IsExit = node.OutDegree == 0
                });
            }

            return new BlockMap(
                blocks,
                idByOffset,
                offsetById,
                sortKeyById,
                idByOffset[graph.Entrypoint.Offset]);
        }

        object CreateMethodDescriptor(MethodTarget target) => new
        {
            assembly_name = target.Assembly.Name.String,
            type_full_name = target.Type.FullName,
            method_name = target.Method.Name.String
        };

        CallToolResult Serialize(object payload)
        {
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = json } }
            };
        }

        AssemblyDef? FindAssemblyByName(string name) =>
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                documentTreeView.GetAllModuleNodes()
                    .Select(node => node.Document?.AssemblyDef)
                    .FirstOrDefault(assembly => assembly != null &&
                        assembly.Name.String.Equals(name, StringComparison.OrdinalIgnoreCase)));

        TypeDef? FindTypeInAssembly(AssemblyDef assembly, string fullName) =>
            assembly.Modules
                .SelectMany(module => GetAllTypesRecursive(module.Types))
                .FirstOrDefault(type => type.FullName.Equals(fullName, StringComparison.Ordinal));

        static IEnumerable<TypeDef> GetAllTypesRecursive(IEnumerable<TypeDef> types)
        {
            foreach (var type in types)
            {
                yield return type;
                foreach (var nested in GetAllTypesRecursive(type.NestedTypes))
                    yield return nested;
            }
        }

        static string RequireString(Dictionary<string, object>? arguments, string name)
        {
            if (arguments == null || !arguments.TryGetValue(name, out var value))
                throw new ArgumentException($"{name} is required");

            var text = value as string ?? value?.ToString();
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException($"{name} is required");
            return text ?? throw new ArgumentException($"{name} is required");
        }

        static string NormalizeEdgeKind(ControlFlowEdgeType edgeType) => edgeType switch
        {
            ControlFlowEdgeType.Conditional => "conditional",
            ControlFlowEdgeType.Unconditional => "unconditional",
            ControlFlowEdgeType.FallThrough => "fallthrough",
            ControlFlowEdgeType.Abnormal => "abnormal",
            _ => "none"
        };

        static string FormatOffset(int offset) => $"0x{offset:X4}";

        sealed class MethodTarget
        {
            public MethodTarget(AssemblyDef assembly, TypeDef type, MethodDef method)
            {
                Assembly = assembly;
                Type = type;
                Method = method;
            }

            public AssemblyDef Assembly { get; }
            public TypeDef Type { get; }
            public MethodDef Method { get; }
        }

        sealed class BlockMap
        {
            public BlockMap(
                List<BlockInfo> blocks,
                Dictionary<long, string> idByOffset,
                Dictionary<string, long> offsetById,
                Dictionary<string, int> sortKeyById,
                string entryBlockId)
            {
                Blocks = blocks;
                IdByOffset = idByOffset;
                OffsetById = offsetById;
                SortKeyById = sortKeyById;
                EntryBlockId = entryBlockId;
            }

            public List<BlockInfo> Blocks { get; }
            public Dictionary<long, string> IdByOffset { get; }
            public Dictionary<string, long> OffsetById { get; }
            public Dictionary<string, int> SortKeyById { get; }
            public string EntryBlockId { get; }
        }

        sealed class BlockInfo
        {
            public string Id { get; set; } = string.Empty;
            public string IlStart { get; set; } = string.Empty;
            public string IlEnd { get; set; } = string.Empty;
            public List<string> InstructionOffsets { get; set; } = new List<string>();
            public int InstructionCount { get; set; }
            public bool IsEntry { get; set; }
            public bool IsExit { get; set; }
        }

        sealed class EdgeInfo
        {
            public string From { get; set; } = string.Empty;
            public string To { get; set; } = string.Empty;
            public string Kind { get; set; } = string.Empty;
        }
    }
}
