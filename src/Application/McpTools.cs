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
using System.Reflection;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.MCP.Server.Contracts;
using dnSpy.MCP.Server.Helper;

namespace dnSpy.MCP.Server.Application
{
    [Export(typeof(McpTools))]
    public sealed partial class McpTools
    {
        readonly Lazy<AssemblyTools> assemblyTools;
        readonly Lazy<TypeTools> typeTools;
        readonly Lazy<EditTools> editTools;
        readonly Lazy<DebugTools> debugTools;
        readonly Lazy<DumpTools> dumpTools;
        readonly Lazy<MemoryInspectTools> memoryInspectTools;
        readonly Lazy<UsageFindingCommandTools> usageFindingTools;
        readonly Lazy<CodeAnalysisHelpers> codeAnalysisTools;
        readonly Lazy<ControlFlowTools> controlFlowTools;
        readonly Lazy<DiscoveryTools> discoveryTools;
        readonly Lazy<De4dotExeTool> de4dotExeTool;
        readonly Lazy<De4dotTools> de4dotTools;
        readonly Lazy<SkillsTools> skillsTools;
        readonly Lazy<ScriptTools> scriptTools;
        readonly Lazy<WindowTools> windowTools;

        [ImportingConstructor]
        public McpTools(
            Lazy<AssemblyTools> assemblyTools,
            Lazy<TypeTools> typeTools,
            Lazy<EditTools> editTools,
            Lazy<DebugTools> debugTools,
            Lazy<DumpTools> dumpTools,
            Lazy<MemoryInspectTools> memoryInspectTools,
            Lazy<UsageFindingCommandTools> usageFindingTools,
            Lazy<CodeAnalysisHelpers> codeAnalysisTools,
            Lazy<ControlFlowTools> controlFlowTools,
            Lazy<DiscoveryTools> discoveryTools,
            Lazy<De4dotExeTool> de4dotExeTool,
            Lazy<De4dotTools> de4dotTools,
            Lazy<SkillsTools> skillsTools,
            Lazy<ScriptTools> scriptTools,
            Lazy<WindowTools> windowTools
            )
        {
            this.assemblyTools = assemblyTools;
            this.typeTools = typeTools;
            this.editTools = editTools;
            this.debugTools = debugTools;
            this.dumpTools = dumpTools;
            this.memoryInspectTools = memoryInspectTools;
            this.usageFindingTools = usageFindingTools;
            this.codeAnalysisTools = codeAnalysisTools;
            this.controlFlowTools = controlFlowTools;
            this.discoveryTools = discoveryTools;
            this.de4dotExeTool = de4dotExeTool;
            this.de4dotTools = de4dotTools;
            this.skillsTools = skillsTools;
            this.scriptTools = scriptTools;
            this.windowTools = windowTools;
        }

        // GetAvailableTools() is defined in McpTools.Schemas.cs (partial class)

        public CallToolResult ExecuteTool(string toolName, Dictionary<string, object>? arguments)
        {
            McpLogger.Info($"Executing tool: {toolName}");

            try
            {
                var result = toolName switch
                {
                    "list_tools" => ListTools(arguments),
                    "list_assemblies"      => InvokeLazy(assemblyTools, "ListAssemblies",      null),
                    "select_assembly"      => InvokeLazy(assemblyTools, "SelectAssembly",      arguments),
                    "close_assembly"       => InvokeLazy(assemblyTools, "CloseAssembly",       arguments),
                    "close_all_assemblies" => InvokeLazy(assemblyTools, "CloseAllAssemblies",  null),
                    "get_assembly_info"    => InvokeLazy(assemblyTools, "GetAssemblyInfo",     arguments),
                    "list_types" => InvokeLazy(assemblyTools, "ListTypes", arguments),
                    "get_type_info" => InvokeLazy(typeTools, "GetTypeInfo", arguments),
                    "decompile_method" => InvokeLazy(typeTools, "DecompileMethod", arguments),
                    "list_methods_in_type" => InvokeLazy(typeTools, "ListMethodsInType", arguments),
                    "list_properties_in_type" => InvokeLazy(typeTools, "ListPropertiesInType", arguments),
                    "get_method_signature" => InvokeLazy(typeTools, "GetMethodSignature", arguments),
                    "search_types" => InvokeLazy(discoveryTools, "SearchTypes", arguments),
                    "find_who_calls_method" => InvokeLazy(discoveryTools, "FindWhoCallsMethod", arguments),
                    "analyze_type_inheritance" => InvokeLazy(discoveryTools, "AnalyzeTypeInheritance", arguments),
                    "get_method_il" => InvokeLazy(typeTools, "GetMethodIL", arguments),
                    "get_method_il_bytes" => InvokeLazy(typeTools, "GetMethodILBytes", arguments),
                    "get_method_exception_handlers" => InvokeLazy(typeTools, "GetMethodExceptionHandlers", arguments),

                    // Edit tools
                    "decompile_type" => InvokeLazy(editTools, "DecompileType", arguments),
                    "change_member_visibility" => InvokeLazy(editTools, "ChangeVisibility", arguments),
                    "rename_member" => InvokeLazy(editTools, "RenameMember", arguments),
                    "save_assembly" => InvokeLazy(editTools, "SaveAssembly", arguments),
                    "get_assembly_metadata" => InvokeLazy(editTools, "GetAssemblyMetadata", arguments),
                    "edit_assembly_metadata" => InvokeLazy(editTools, "EditAssemblyMetadata", arguments),
                    "list_assembly_attributes"  => InvokeLazy(editTools, "ListAssemblyAttributes",  arguments),
                    "remove_assembly_attribute" => InvokeLazy(editTools, "RemoveAssemblyAttribute", arguments),
                    "set_assembly_flags" => InvokeLazy(editTools, "SetAssemblyFlags", arguments),
                    "list_assembly_references"  => InvokeLazy(editTools, "ListAssemblyReferences",  arguments),
                    "add_assembly_reference"    => InvokeLazy(editTools, "AddAssemblyReference",    arguments),
                    "remove_assembly_reference" => InvokeLazy(editTools, "RemoveAssemblyReference", arguments),
                    "list_resources"            => InvokeLazy(editTools, "ListResources",            arguments),
                    "get_resource"              => InvokeLazy(editTools, "GetResource",              arguments),
                    "add_resource"              => InvokeLazy(editTools, "AddResource",              arguments),
                    "remove_resource"           => InvokeLazy(editTools, "RemoveResource",           arguments),
                    "extract_costura"           => InvokeLazy(editTools, "ExtractCostura",           arguments),
                    "inject_type_from_dll"      => InvokeLazy(editTools, "InjectTypeFromDll",        arguments),
                    "list_pinvoke_methods" => InvokeLazy(editTools, "ListPInvokeMethods", arguments),
                    "patch_method_to_ret" => InvokeLazy(editTools, "PatchMethodToRet", arguments),
                    "list_events_in_type" => InvokeLazy(editTools, "ListEventsInType", arguments),
                    "get_custom_attributes" => InvokeLazy(editTools, "GetCustomAttributes", arguments),
                    "list_nested_types" => InvokeLazy(editTools, "ListNestedTypes", arguments),

                    // Previously-hidden TypeTools
                    "get_type_fields" => InvokeLazy(typeTools, "GetTypeFields", arguments),
                    "get_type_property" => InvokeLazy(typeTools, "GetTypeProperty", arguments),
                    "find_path_to_type" => InvokeLazy(typeTools, "FindPathToType", arguments),
                    "list_native_modules" => InvokeLazy(assemblyTools, "ListNativeModules", arguments),

                    // Memory dump tools
                    "list_runtime_modules" => InvokeLazy(dumpTools, "ListRuntimeModules", arguments),
                    "dump_module_from_memory" => InvokeLazy(dumpTools, "DumpModuleFromMemory", arguments),
                    "read_process_memory"  => InvokeLazy(dumpTools, "ReadProcessMemory",  arguments),
                    "write_process_memory" => InvokeLazy(dumpTools, "WriteProcessMemory", arguments),
                    "get_pe_sections" => InvokeLazy(dumpTools, "GetPeSections", arguments),
                    "dump_pe_section" => InvokeLazy(dumpTools, "DumpPeSection", arguments),
                    "dump_module_unpacked" => InvokeLazy(dumpTools, "DumpModuleUnpacked", arguments),
                    "dump_memory_to_file" => InvokeLazy(dumpTools, "DumpMemoryToFile", arguments),

                    // Memory inspect / runtime variable tools
                    "get_local_variables" => InvokeLazy(memoryInspectTools, "GetLocalVariables", arguments),
                    "eval_expression"     => InvokeLazy(memoryInspectTools, "EvalExpression",    arguments),

                    // Usage finding tools
                    "find_who_uses_type"   => InvokeLazy(usageFindingTools, "FindWhoUsesTypeArgs",   arguments),
                    "find_who_reads_field" => InvokeLazy(usageFindingTools, "FindWhoReadsFieldArgs", arguments),
                    "find_who_writes_field" => InvokeLazy(usageFindingTools, "FindWhoWritesFieldArgs", arguments),

                    // Code analysis tools
                    "analyze_call_graph"                    => InvokeLazy(codeAnalysisTools, "AnalyzeCallGraphArgs",                    arguments),
                    "find_dependency_chain"                 => InvokeLazy(codeAnalysisTools, "FindDependencyChainArgs",                 arguments),
                    "analyze_cross_assembly_dependencies"   => InvokeLazy(codeAnalysisTools, "AnalyzeCrossAssemblyDependenciesArgs",   arguments),
                    "find_dead_code"                        => InvokeLazy(codeAnalysisTools, "FindDeadCodeArgs",                        arguments),
                    "get_control_flow_graph"                => InvokeLazy(controlFlowTools, "GetControlFlowGraph",                     arguments),
                    "get_basic_blocks"                      => InvokeLazy(controlFlowTools, "GetBasicBlocks",                          arguments),

                    // PE / string scanning tools
                    "scan_pe_strings" => InvokeLazy(assemblyTools, "ScanPeStrings", arguments),

                    // Assembly loading
                    "load_assembly"    => InvokeLazy(assemblyTools, "LoadAssembly",    arguments),

                    // Process launch / attach / unpack tools
                    "start_debugging"    => InvokeLazy(debugTools, "StartDebugging",  arguments),
                    "attach_to_process"  => InvokeLazy(debugTools, "AttachToProcess", arguments),
                    "unpack_from_memory" => InvokeLazy(dumpTools,  "UnpackFromMemory", arguments),
                    "dump_cordbg_il"     => InvokeLazy(dumpTools,  "DumpCordbgIL",    arguments),

                    // Debug tools
                    "get_debugger_state" => InvokeLazy(debugTools, "GetDebuggerState", arguments),
                    "list_breakpoints" => InvokeLazy(debugTools, "ListBreakpoints", arguments),
                    "set_breakpoint" => InvokeLazy(debugTools, "SetBreakpoint", arguments),
                    "remove_breakpoint" => InvokeLazy(debugTools, "RemoveBreakpoint", arguments),
                    "clear_all_breakpoints" => InvokeLazy(debugTools, "ClearAllBreakpoints", arguments),
                    "continue_debugger" => InvokeLazy(debugTools, "ContinueDebugger", arguments),
                    "break_debugger" => InvokeLazy(debugTools, "BreakDebugger", arguments),
                    "stop_debugging" => InvokeLazy(debugTools, "StopDebugging", arguments),
                    "get_call_stack" => InvokeLazy(debugTools, "GetCallStack", arguments),

                    "step_over"            => InvokeLazy(debugTools, "StepOver",           arguments),
                    "step_into"            => InvokeLazy(debugTools, "StepInto",           arguments),
                    "step_out"             => InvokeLazy(debugTools, "StepOut",            arguments),
                    "get_current_location" => InvokeLazy(debugTools, "GetCurrentLocation", arguments),
                    "wait_for_pause"       => InvokeLazy(debugTools, "WaitForPause",       arguments),

                    "set_exception_breakpoint"    => InvokeLazy(debugTools, "SetExceptionBreakpoint",    arguments),
                    "remove_exception_breakpoint" => InvokeLazy(debugTools, "RemoveExceptionBreakpoint", arguments),
                    "list_exception_breakpoints"  => InvokeLazy(debugTools, "ListExceptionBreakpoints",  arguments),

                    "run_de4dot"            => InvokeLazy(de4dotExeTool, "RunDe4dot",            arguments),

                    // Config management
                    "get_mcp_config"    => HandleGetMcpConfig(),
                    "reload_mcp_config" => HandleReloadMcpConfig(),

                    // de4dot deobfuscation tools
                    "list_deobfuscators"    => InvokeLazy(de4dotTools, "ListDeobfuscators",    arguments),
                    "detect_obfuscator"     => InvokeLazy(de4dotTools, "DetectObfuscator",     arguments),
                    "deobfuscate_assembly"  => InvokeLazy(de4dotTools, "DeobfuscateAssembly",  arguments),
                    "save_deobfuscated"     => InvokeLazy(de4dotTools, "SaveDeobfuscated",     arguments),

                    // Skills knowledge base
                    "list_skills"   => InvokeLazy(skillsTools, "ListSkills",   arguments),
                    "get_skill"     => InvokeLazy(skillsTools, "GetSkill",     arguments),
                    "save_skill"    => InvokeLazy(skillsTools, "SaveSkill",    arguments),
                    "search_skills" => InvokeLazy(skillsTools, "SearchSkills", arguments),
                    "delete_skill"  => InvokeLazy(skillsTools, "DeleteSkill",  arguments),

                    // Roslyn scripting
                    "run_script" => InvokeLazy(scriptTools, "RunScript", arguments),

                    // Window / dialog management
                    "list_dialogs" => InvokeLazy(windowTools, "ListDialogs", arguments),
                    "close_dialog" => InvokeLazy(windowTools, "CloseDialog", arguments),

                    _ => new CallToolResult
                    {
                        Content = new List<ToolContent> {
                            new ToolContent { Text = $"Unknown tool: {toolName}" }
                        },
                        IsError = true
                    }
                };

                return result;
            }
            catch (Exception ex)
            {
                McpLogger.Exception(ex, $"Error executing tool {toolName}");
                return new CallToolResult
                {
                    Content = new List<ToolContent> {
                        new ToolContent { Text = $"Error executing tool {toolName}: {ex.Message}" }
                    },
                    IsError = true
                };
            }
        }

        CallToolResult ListTools(Dictionary<string, object>? arguments)
        {
            var tools = GetAvailableTools(CreateCatalogFilter(arguments));
            var json = JsonSerializer.Serialize(tools, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = json } }
            };
        }

        CallToolResult InvokeLazy<T>(Lazy<T> lazy, string methodName, Dictionary<string, object>? arguments) where T : class
        {
            if (lazy == null)
                throw new ArgumentNullException(nameof(lazy));
            try
            {
                object? instance;
                try
                {
                    instance = lazy.Value;
                }
                catch (Exception ex)
                {
                    McpLogger.Exception(ex, "InvokeLazy: failed to construct lazy.Value");
                    return new CallToolResult
                    {
                        Content = new List<ToolContent> { new ToolContent { Text = $"InvokeLazy construction error: {ex.Message}" } },
                        IsError = true
                    };
                }

                if (instance == null)
                {
                    return new CallToolResult
                    {
                        Content = new List<ToolContent> { new ToolContent { Text = "InvokeLazy: instance is null" } },
                        IsError = true
                    };
                }

                var type = instance.GetType();
                var mi = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (mi == null)
                {
                    return new CallToolResult
                    {
                        Content = new List<ToolContent> { new ToolContent { Text = $"Method not found on {type.FullName}: {methodName}" } },
                        IsError = true
                    };
                }

                var parameters = mi.GetParameters();
                object?[] invokeArgs;

                if (parameters.Length == 0) {
                    invokeArgs = Array.Empty<object?>();
                }
                else {
                    // Pass arguments (null or empty dict) — the callee validates required params itself
                    invokeArgs = new object?[] { arguments };
                }

                var res = mi.Invoke(instance, invokeArgs);
                if (res is CallToolResult ctr)
                    return ctr;

                var text = JsonSerializer.Serialize(res, new JsonSerializerOptions { WriteIndented = true });
                return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = text } } };
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                McpLogger.Exception(inner, $"InvokeLazy error in {methodName}");
                return new CallToolResult
                {
                    Content = new List<ToolContent> { new ToolContent { Text = inner is ArgumentException
                        ? $"Parameter error: {inner.Message}"
                        : $"Tool error: {inner.Message}" } },
                    IsError = true
                };
            }
            catch (Exception ex)
            {
                McpLogger.Exception(ex, "InvokeLazy failed");
                return new CallToolResult
                {
                    Content = new List<ToolContent> { new ToolContent { Text = $"InvokeLazy error: {ex.Message}" } },
                    IsError = true
                };
            }
        }

        static string? OptionalString(Dictionary<string, object>? args, string key, string? def = null)
        {
            if (args == null || !args.TryGetValue(key, out var v)) return def;
            return v?.ToString() ?? def;
        }

        static ToolCatalogFilter CreateCatalogFilter(Dictionary<string, object>? arguments)
        {
            var mode = OptionalString(arguments, "mode", "default");
            var includeHidden = string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase);

            if (arguments != null && arguments.TryGetValue("include_hidden", out var includeHiddenValue))
            {
                if (includeHiddenValue is bool hiddenBool)
                    includeHidden = hiddenBool;
                else if (includeHiddenValue is JsonElement hiddenElement)
                    includeHidden = hiddenElement.ValueKind == JsonValueKind.True;
                else if (bool.TryParse(includeHiddenValue?.ToString(), out var parsed))
                    includeHidden = parsed;
            }

            return new ToolCatalogFilter
            {
                IncludeHiddenByDefault = includeHidden
            };
        }

        // ── Config management handlers ────────────────────────────────────────

        CallToolResult HandleGetMcpConfig()
        {
            var cfg = Configuration.McpConfig.Instance;
            var resolvedDe4dot = cfg.ResolveDe4dotExe();
            var json = JsonSerializer.Serialize(new {
                ConfigFilePath     = Configuration.McpConfig.ConfigFilePath,
                ConfigFileExists   = System.IO.File.Exists(Configuration.McpConfig.ConfigFilePath),
                De4dotExePath      = cfg.De4dotExePath,
                De4dotSearchPaths  = cfg.De4dotSearchPaths,
                ResolvedDe4dotExe  = resolvedDe4dot,
                De4dotFound        = resolvedDe4dot != null
            }, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = json } } };
        }

        CallToolResult HandleReloadMcpConfig()
        {
            var cfg = Configuration.McpConfig.Reload();
            var resolvedDe4dot = cfg.ResolveDe4dotExe();
            var json = JsonSerializer.Serialize(new {
                Status             = "reloaded",
                ConfigFilePath     = Configuration.McpConfig.ConfigFilePath,
                De4dotExePath      = cfg.De4dotExePath,
                De4dotSearchPaths  = cfg.De4dotSearchPaths,
                ResolvedDe4dotExe  = resolvedDe4dot,
                De4dotFound        = resolvedDe4dot != null
            }, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = json } } };
        }
    }
}
