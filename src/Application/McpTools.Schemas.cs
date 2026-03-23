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
using System.Linq;
using dnSpy.MCP.Server.Contracts;

namespace dnSpy.MCP.Server.Application
{
    public sealed class ToolCatalogFilter
    {
        public bool IncludeHiddenByDefault { get; set; }
    }

    public sealed partial class McpTools
    {
        // ── Aggregator ────────────────────────────────────────────────────────────
        public List<ToolInfo> GetAvailableTools(ToolCatalogFilter? filter = null)
        {
            var tools = new List<ToolInfo>();
            AddToolRange(tools, GetAssemblyToolSchemas(), "core-analysis");
            AddToolRange(tools, GetTypeToolSchemas(), "core-analysis");
            AddToolRange(tools, GetMethodILToolSchemas(), "core-analysis");
            AddToolRange(tools, GetAnalysisToolSchemas(), "core-analysis");
            AddToolRange(tools, GetControlFlowToolSchemas(), "core-analysis");
            AddToolRange(tools, GetEditToolSchemas(), "editing");
            AddToolRange(tools, GetResourceToolSchemas(), "editing");
            AddToolRange(tools, GetDebugToolSchemas(), "debug-runtime");
            AddToolRange(tools, GetMemoryToolSchemas(), "debug-runtime");
            AddToolRange(tools, GetDeobfuscationToolSchemas(), "editing");
            AddToolRange(tools, GetSkillsToolSchemas(), "admin");
            AddToolRange(tools, GetScriptingToolSchemas(), "admin");
            AddToolRange(tools, GetWindowToolSchemas(), "admin");
            AddToolRange(tools, GetSourceMapToolSchemas(), "core-analysis");
            AddToolRange(tools, GetNativeRuntimeToolSchemas(), "debug-runtime");
            AddToolRange(tools, GetUtilityToolSchemas(), "admin");
            return FilterCatalogTools(tools, filter);
        }

        // ── Assembly tools ────────────────────────────────────────────────────────
        List<ToolInfo> GetAssemblyToolSchemas() => new List<ToolInfo> {
            new ToolInfo {
                Name = "list_assemblies",
                Description = "List all loaded assemblies in dnSpy",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>(),
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "get_assembly_info",
                Description = "Get detailed information about a specific assembly",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Name of the assembly"
                        },
                        ["cursor"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Optional cursor for pagination"
                        }
                    },
                    ["required"] = new List<string> { "assembly_name" }
                }
            },
            new ToolInfo {
                Name = "list_types",
                Description = "List types in an assembly or namespace. Supports glob (System.* or *Controller) and regex (^System\\..*Controller$) via name_pattern.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Name of the assembly"
                        },
                        ["namespace"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Optional exact namespace filter"
                        },
                        ["name_pattern"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Optional name filter: glob (* and ?) or regex (use ^/$). Matches against type short name and full name."
                        },
                        ["cursor"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Pagination cursor from previous response nextCursor"
                        }
                    },
                    ["required"] = new List<string> { "assembly_name" }
                }
            },
            new ToolInfo {
                Name = "load_assembly",
                Description = "Load a .NET assembly into dnSpy from disk or from a running process. Mode 1: provide 'file_path' (absolute path). Mode 2: provide 'pid' to dump from a running process (requires active debug session).",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["file_path"]     = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Absolute path to a .NET assembly (.dll/.exe) or a saved memory dump on disk" },
                        ["memory_layout"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Set true when file_path points to a raw memory-layout dump (VA instead of file offsets). Default false." },
                        ["pid"]           = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "PID of a running .NET process. Dumps the main module (or the module matching 'module_name') from process memory and loads it." },
                        ["module_name"]   = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Optional module name filter when using 'pid' (e.g. 'MyApp.dll'). Defaults to the first EXE module." },
                        ["process_id"]    = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Alias for 'pid'" }
                    },
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "select_assembly",
                Description = "Select an assembly in the dnSpy document tree view and open it in the active tab. This changes the 'current' assembly context for the decompiler and for all subsequent MCP operations that target the selected assembly. Call this after load_assembly to switch focus to the newly loaded file. Use 'file_path' to disambiguate when multiple assemblies share the same short name.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Short name of the assembly to select (e.g. 'BigBearTuning_unpacked'). Use list_assemblies to see loaded names." },
                        ["file_path"]     = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional: absolute path of the loaded file (FilePath from list_assemblies). Use this to pick the correct one when multiple assemblies share the same name." }
                    },
                    ["required"] = new List<string> { "assembly_name" }
                }
            },
            new ToolInfo {
                Name = "close_assembly",
                Description = "Close (remove) a specific assembly from dnSpy. If multiple assemblies share the same name, use 'file_path' (from list_assemblies) to target a specific one.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Short name of the assembly to close." },
                        ["file_path"]     = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional: absolute path (FilePath from list_assemblies) to close a specific copy when names collide." }
                    },
                    ["required"] = new List<string> { "assembly_name" }
                }
            },
            new ToolInfo {
                Name = "close_all_assemblies",
                Description = "Close all assemblies currently loaded in dnSpy, clearing the document tree.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>(),
                    ["required"] = new List<string>()
                }
            },
        };

        // ── Type inspection tools ─────────────────────────────────────────────────
        List<ToolInfo> GetTypeToolSchemas() => new List<ToolInfo> {
            new ToolInfo {
                Name = "get_type_info",
                Description = "Get a summary view of a specific type. Best first call for type discovery before using detail tools like list_methods_in_type, list_properties_in_type, get_type_fields, or get_type_property.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Name of the assembly"
                        },
                        ["type_full_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Full name of the type"
                        },
                        ["cursor"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Optional cursor for pagination"
                        }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name" }
                }
            },
            new ToolInfo {
                Name = "search_types",
                Description = "Search for types by name across all loaded assemblies. Supports glob wildcards (*IService*) and regex (^My\\..*Repository$).",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["query"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Search query: plain substring, glob (* and ?), or regex (use ^/$). Matched against FullName."
                        },
                        ["cursor"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Pagination cursor from previous response nextCursor"
                        }
                    },
                    ["required"] = new List<string> { "query" }
                }
            },
            new ToolInfo {
                Name = "list_methods_in_type",
                Description = "List methods in a type. Prefer get_type_info first for summary discovery, then use this tool for detailed or filtered method enumeration.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Name of the assembly"
                        },
                        ["type_full_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Full name of the type"
                        },
                        ["visibility"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Optional visibility filter: public, private, protected, or internal"
                        },
                        ["name_pattern"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Optional name filter: glob (* and ?) or regex (use ^/$). E.g. 'Get*', '^On[A-Z]', 'Async$'"
                        },
                        ["cursor"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Pagination cursor from previous response nextCursor"
                        }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name" }
                }
            },
            new ToolInfo {
                Name = "list_properties_in_type",
                Description = "List all properties in a type. Prefer get_type_info first for a summary view, then use this tool when you need the dedicated property list.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Name of the assembly"
                        },
                        ["type_full_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Full name of the type"
                        }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name" }
                }
            },
            new ToolInfo {
                Name = "get_method_signature",
                Description = "Get detailed method signature",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Name of the assembly"
                        },
                        ["type_full_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Full name of the type"
                        },
                        ["method_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Name of the method"
                        }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                }
            },
            new ToolInfo {
                Name = "analyze_type_inheritance",
                Description = "Analyze complete inheritance chain of a type",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Name of the assembly"
                        },
                        ["type_full_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Full name of the type"
                        }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name" }
                }
            },
            new ToolInfo {
                Name = "get_type_fields",
                Description = "List fields in a type matching a glob/regex pattern. Supports * and ? wildcards.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                        ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type" },
                        ["pattern"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Field name pattern: glob (* ?) or regex (^/$). Use * to list all." },
                        ["cursor"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Pagination cursor" }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name", "pattern" }
                }
            },
            new ToolInfo {
                Name = "get_type_property",
                Description = "Get detailed information about a single property, including getter/setter info and custom attributes.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                        ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type" },
                        ["property_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the property" }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name", "property_name" }
                }
            },
            new ToolInfo {
                Name = "find_path_to_type",
                Description = "Find property/field reference paths from one type to another via BFS traversal of the object graph.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                        ["from_type"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the starting type" },
                        ["to_type"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name (or substring) of the target type" },
                        ["max_depth"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Maximum BFS depth (default 5)" }
                    },
                    ["required"] = new List<string> { "assembly_name", "from_type", "to_type" }
                }
            },
            new ToolInfo {
                Name = "list_native_modules",
                Description = "List all native DLLs imported via P/Invoke (DllImport) in an assembly, grouped by DLL name.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" }
                    },
                    ["required"] = new List<string> { "assembly_name" }
                }
            },
            new ToolInfo {
                Name = "list_events_in_type",
                Description = "List all events defined in a type",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                        ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type" }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name" }
                }
            },
            new ToolInfo {
                Name = "get_custom_attributes",
                Description = "Get custom attributes on a type or one of its members. Omit member_name to get the type's own attributes.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                        ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type" },
                        ["member_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the member (optional; omit for type-level attributes)" },
                        ["member_kind"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Kind of member: method, field, property, or event (optional; helps disambiguation)" }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name" }
                }
            },
            new ToolInfo {
                Name = "list_nested_types",
                Description = "List all nested types inside a type, recursively",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                        ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the containing type" }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name" }
                }
            },
        };

        // ── Method / IL tools ─────────────────────────────────────────────────────
        List<ToolInfo> GetMethodILToolSchemas() => new List<ToolInfo> {
            new ToolInfo {
                Name = "decompile_method",
                Description = "Decompile a specific method to C# code. Preferred over decompile_type for large types (avoids OOM). Use file_path to disambiguate when multiple assemblies share the same name.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                        ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the declaring type (e.g. 'AA9A3FB8' or 'MyNamespace.MyClass')" },
                        ["method_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the method (e.g. 'Main', '.ctor', or obfuscated names like '3392BA2B')" },
                        ["file_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full path of the assembly file (optional; used to disambiguate when multiple assemblies share the same name)" },
                        ["signature"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full method signature to select a specific overload (optional, e.g. 'System.Void AA9A3FB8::3392BA2B(System.Object,System.Int32)')" }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                }
            },
            new ToolInfo {
                Name = "get_method_il",
                Description = "Get IL instructions of a method",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Name of the assembly"
                        },
                        ["type_full_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Full name of the type"
                        },
                        ["method_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Name of the method"
                        }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                }
            },
            new ToolInfo {
                Name = "get_method_il_bytes",
                Description = "Get raw IL bytes of a method",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Name of the assembly"
                        },
                        ["type_full_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Full name of the type"
                        },
                        ["method_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Name of the method"
                        }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                }
            },
            new ToolInfo {
                Name = "get_method_exception_handlers",
                Description = "Get exception handlers (try-catch-finally) of a method",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Name of the assembly"
                        },
                        ["type_full_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Full name of the type"
                        },
                        ["method_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Name of the method"
                        }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                }
            },
            new ToolInfo {
                Name = "dump_cordbg_il",
                Description = "For each MethodDef in the paused module, reads ICorDebugFunction.ILCode.Address and ILCode.Size via the CorDebug API (through reflection). Reports whether IL addresses fall inside the PE image (mapped encrypted stubs) or outside (hook-decrypted CLR-internal buffers). Requires an active paused debug session. Useful for ConfuserEx JIT-hook analysis.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["module_name"]   = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Module name or filename filter (default: first exe module)" },
                        ["output_path"]   = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Optional path to save full JSON results to disk" },
                        ["max_methods"]   = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Max number of MethodDef tokens to scan (default 10000)" },
                        ["include_bytes"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "If true, include base64-encoded IL bytes (from addr-12) for each method (default false)" }
                    },
                    ["required"] = new List<string>()
                }
            },
        };

        // ── Static analysis tools ─────────────────────────────────────────────────
        List<ToolInfo> GetAnalysisToolSchemas() => new List<ToolInfo> {
            new ToolInfo {
                Name = "find_who_calls_method",
                Description = "Find all methods that call a specific method",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Name of the assembly"
                        },
                        ["type_full_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Full name of the type"
                        },
                        ["method_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Name of the method"
                        }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                }
            },
            new ToolInfo {
                Name = "find_who_uses_type",
                Description = "Find all types, methods, and fields that reference a specific type (as base class, interface, field type, parameter, or return type). Searches across all loaded assemblies.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"]  = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Assembly containing the target type" },
                        ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type to search for (e.g. MyNamespace.MyClass)" }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name" }
                }
            },
            new ToolInfo {
                Name = "find_who_reads_field",
                Description = "Find all methods that read a specific field via IL LDFLD/LDSFLD instructions. Searches across all loaded assemblies.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"]  = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Assembly containing the type that declares the field" },
                        ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type that declares the field" },
                        ["field_name"]     = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the field" }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name", "field_name" }
                }
            },
            new ToolInfo {
                Name = "find_who_writes_field",
                Description = "Find all methods that write to a specific field via IL STFLD/STSFLD instructions. Searches across all loaded assemblies.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"]  = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Assembly containing the type that declares the field" },
                        ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type that declares the field" },
                        ["field_name"]     = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the field" }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name", "field_name" }
                }
            },
            new ToolInfo {
                Name = "analyze_call_graph",
                Description = "Build a recursive call graph for a method, showing all methods it calls down to a configurable depth. Useful for understanding execution flow.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"]  = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                        ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type containing the method" },
                        ["method_name"]    = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the method to analyze" },
                        ["max_depth"]      = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Maximum recursion depth (default 5)" }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                }
            },
            new ToolInfo {
                Name = "find_dependency_chain",
                Description = "Find all dependency paths (via base types, interfaces, fields, parameters, return types) between two types using BFS traversal.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly to search in" },
                        ["from_type"]     = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the starting type" },
                        ["to_type"]       = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name (or simple name) of the target type" },
                        ["max_length"]    = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Maximum path length (default 10)" }
                    },
                    ["required"] = new List<string> { "assembly_name", "from_type", "to_type" }
                }
            },
            new ToolInfo {
                Name = "analyze_cross_assembly_dependencies",
                Description = "Compute a dependency matrix for all loaded assemblies, showing which assemblies each assembly depends on (via type references).",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>(),
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "find_dead_code",
                Description = "Identify methods and types in an assembly that are never called or referenced (static analysis approximation; virtual dispatch and reflection are not tracked).",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"]  = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly to analyze" },
                        ["include_private"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Include private members in dead code detection (default true)" }
                    },
                    ["required"] = new List<string> { "assembly_name" }
                }
            },
            new ToolInfo {
                Name = "scan_pe_strings",
                Description = "Scan the raw PE file bytes for printable ASCII and UTF-16 strings. Useful for finding URLs, API keys, IP addresses, file paths, and other plaintext data embedded in obfuscated or packed assemblies. " +
                              "Use file_path when two assemblies share the same internal name (e.g. packed original + unpacked copy) to avoid scanning the wrong file.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"]    = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Name of the loaded assembly to scan. Required unless file_path is provided." },
                        ["file_path"]        = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Direct absolute path to the PE file on disk. Takes priority over assembly_name. Use this when multiple assemblies share the same internal name." },
                        ["min_length"]       = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Minimum string length to include (default 5)" },
                        ["include_utf16"]    = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Also scan for UTF-16 LE strings (default true)" },
                        ["filter_pattern"]   = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Optional regex to filter results (e.g. 'https?://' to find only URLs)" }
                    },
                    ["required"] = new List<string> { }
                }
            },
        };

        List<ToolInfo> GetControlFlowToolSchemas() => new List<ToolInfo> {
            new ToolInfo {
                Name = "get_control_flow_graph",
                Description = "Construct a serializable control-flow graph for a managed CIL method using Echo.Platforms.Dnlib.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Name of the loaded assembly"
                        },
                        ["type_full_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Full name of the declaring type"
                        },
                        ["method_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Method name to analyze"
                        }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                }
            },
            new ToolInfo {
                Name = "get_basic_blocks",
                Description = "Return a simplified basic-block view for a managed CIL method using Echo.Platforms.Dnlib. This is a reduced view of get_control_flow_graph.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Name of the loaded assembly"
                        },
                        ["type_full_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Full name of the declaring type"
                        },
                        ["method_name"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Method name to analyze"
                        }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                }
            }
        };

        // ── Edit tools ────────────────────────────────────────────────────────────
        List<ToolInfo> GetEditToolSchemas() => new List<ToolInfo> {
            new ToolInfo {
                Name = "decompile_type",
                Description = "Decompile an entire type (class/struct/interface/enum) to C# source code. Use file_path to disambiguate when multiple assemblies share the same name.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                        ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type (e.g. MyNamespace.MyClass)" },
                        ["file_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full file path to the assembly (optional; used to disambiguate when multiple assemblies share the same name)" }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name" }
                }
            },
            new ToolInfo {
                Name = "change_member_visibility",
                Description = "Change the visibility/access modifier of a type or its members (method, field, property, event). Changes are in-memory until save_assembly is called.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                        ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the containing type (or the type itself when member_kind=type)" },
                        ["member_kind"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Kind of member: type, method, field, property, or event" },
                        ["member_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the member (ignored when member_kind=type)" },
                        ["new_visibility"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "New visibility: public, private, protected, internal, protected_internal, private_protected" }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name", "member_kind", "new_visibility" }
                }
            },
            new ToolInfo {
                Name = "rename_member",
                Description = "Rename a type or one of its members (method, field, property, event). Changes are in-memory until save_assembly is called.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                        ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type" },
                        ["member_kind"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Kind of member: type, method, field, property, or event" },
                        ["old_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Current name of the member" },
                        ["new_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "New name for the member" }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name", "member_kind", "old_name", "new_name" }
                }
            },
            new ToolInfo {
                Name = "save_assembly",
                Description = "Save a (possibly modified) assembly to disk. Persists all in-memory changes made by rename_member, change_member_visibility, edit_assembly_metadata, etc.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly to save" },
                        ["output_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Output file path. Defaults to the original file location." }
                    },
                    ["required"] = new List<string> { "assembly_name" }
                }
            },
            new ToolInfo {
                Name = "get_assembly_metadata",
                Description = "Read assembly-level metadata: name, version, culture, public key, flags, hash algorithm, module count, and custom attributes.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" }
                    },
                    ["required"] = new List<string> { "assembly_name" }
                }
            },
            new ToolInfo {
                Name = "edit_assembly_metadata",
                Description = "Edit assembly-level metadata fields: name, version, culture, or hash algorithm. Changes are in-memory until save_assembly is called.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"]   = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly to edit" },
                        ["new_name"]        = new Dictionary<string, object> { ["type"] = "string", ["description"] = "New assembly name (optional)" },
                        ["version"]         = new Dictionary<string, object> { ["type"] = "string", ["description"] = "New version as major.minor.build.revision (optional, e.g. '2.0.0.0')" },
                        ["culture"]         = new Dictionary<string, object> { ["type"] = "string", ["description"] = "New culture string, e.g. '' (neutral), 'en-US' (optional)" },
                        ["hash_algorithm"]  = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Hash algorithm: SHA1, MD5, or None (optional)" }
                    },
                    ["required"] = new List<string> { "assembly_name" }
                }
            },
            new ToolInfo {
                Name = "list_assembly_attributes",
                Description = "List all custom attributes declared at assembly level ([assembly: ...] in C#). Legacy compatibility view; prefer get_assembly_metadata for the primary metadata summary.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly to inspect" }
                    },
                    ["required"] = new List<string> { "assembly_name" }
                }
            },
            new ToolInfo {
                Name = "remove_assembly_attribute",
                Description = "Remove one or more custom attributes from the assembly manifest ([assembly: ...] in C#). Example: remove SuppressIldasmAttribute to re-enable ildasm on the saved file. Changes are in-memory until save_assembly is called.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"]      = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly to modify" },
                        ["attribute_type_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Simple or fully-qualified name of the attribute to remove, e.g. 'SuppressIldasmAttribute' or 'System.Runtime.CompilerServices.SuppressIldasmAttribute'" },
                        ["index"]              = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional 0-based index among matching attributes to remove a specific one. Omit to remove ALL matching attributes." }
                    },
                    ["required"] = new List<string> { "assembly_name", "attribute_type_name" }
                }
            },
            new ToolInfo {
                Name = "set_assembly_flags",
                Description = "Set or clear an individual assembly attribute flag. Changes are in-memory until save_assembly is called.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                        ["flag_name"]     = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Flag to change: PublicKey | Retargetable | DisableJITOptimizer | EnableJITTracking | WindowsRuntime | ProcessorArchitecture"
                        },
                        ["value"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "true/false for boolean flags; architecture name for ProcessorArchitecture (AnyCPU | x86 | AMD64 | ARM | ARM64 | IA64)"
                        }
                    },
                    ["required"] = new List<string> { "assembly_name", "flag_name", "value" }
                }
            },
            new ToolInfo {
                Name = "list_assembly_references",
                Description = "List all assembly references (AssemblyRef table entries) in the manifest module.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" }
                    },
                    ["required"] = new List<string> { "assembly_name" }
                }
            },
            new ToolInfo {
                Name = "add_assembly_reference",
                Description = "Add an assembly reference (AssemblyRef) by loading a DLL from disk. A TypeForwarder is created to anchor the reference so it persists when saved. Changes are in-memory until save_assembly is called.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the target assembly to add the reference to" },
                        ["dll_path"]      = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Absolute path to the DLL to reference" },
                        ["type_name"]     = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Specific public type to use as the TypeForwarder anchor (optional; defaults to first public type)" }
                    },
                    ["required"] = new List<string> { "assembly_name", "dll_path" }
                }
            },
            new ToolInfo {
                Name = "remove_assembly_reference",
                Description = "Remove an AssemblyRef entry and all associated TypeForwarder (ExportedType) entries that target it. If the reference is still used by TypeRefs in code, a warning is returned — those usages must also be removed before the reference disappears from the saved file. Changes are in-memory until save_assembly is called.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"]  = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Assembly to modify" },
                        ["reference_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Short name of the assembly reference to remove (e.g. System.Drawing)" }
                    },
                    ["required"] = new List<string> { "assembly_name", "reference_name" }
                }
            },
            new ToolInfo {
                Name = "inject_type_from_dll",
                Description = "Deep-clone a type (fields, methods with IL, properties, events) from an external DLL file into the target assembly. Changes are in-memory until save_assembly is called.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"]    = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the target assembly" },
                        ["dll_path"]         = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Absolute path to the source DLL" },
                        ["source_type"]      = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name (or simple name) of the type to inject" },
                        ["target_namespace"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Namespace for the injected type in the target assembly (optional; defaults to source namespace)" },
                        ["overwrite"]        = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Replace existing type with same name/namespace if present (default false)" }
                    },
                    ["required"] = new List<string> { "assembly_name", "dll_path", "source_type" }
                }
            },
            new ToolInfo {
                Name = "list_pinvoke_methods",
                Description = "List all P/Invoke (DllImport) declarations in a type or the entire assembly. Returns managed name, token, DLL name, and native function name, grouped by DLL when scanning the full assembly. Omit type_full_name to scan all types.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"]  = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly to inspect" },
                        ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional: full name of a specific type to inspect. Omit to scan all types in the assembly." }
                    },
                    ["required"] = new List<string> { "assembly_name" }
                }
            },
            new ToolInfo {
                Name = "patch_method_to_ret",
                Description = "Replace a method's IL body with a minimal return stub (nop + ret) to neutralize it. Ideal for disabling anti-debug, anti-tamper, or other unwanted routines. Changes are in-memory until save_assembly is called.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"]   = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly containing the method" },
                        ["type_full_name"]  = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type containing the method (including namespace)" },
                        ["method_name"]     = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Simple name of the method to patch" },
                        ["method_token"]    = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional metadata token (hex like 0x06001234 or decimal) to disambiguate overloaded methods" }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                }
            },
        };

        // ── Resource tools ────────────────────────────────────────────────────────
        List<ToolInfo> GetResourceToolSchemas() => new List<ToolInfo> {
            new ToolInfo {
                Name = "list_resources",
                Description = "List all ManifestResource entries in an assembly: embedded resources, linked file references, and assembly-linked resources. Flags Costura.Fody-embedded assemblies (resources starting with 'costura.').",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Assembly to inspect" }
                    },
                    ["required"] = new List<string> { "assembly_name" }
                }
            },
            new ToolInfo {
                Name = "get_resource",
                Description = "Extract an embedded ManifestResource by name. Returns the raw bytes as Base64 (up to 4 MB inline) and optionally saves to disk. Use skip_base64=true when saving large resources to disk.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Assembly containing the resource" },
                        ["resource_name"] = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Exact resource name (use list_resources to find it)" },
                        ["output_path"]   = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Optional absolute path to save the raw resource bytes to disk" },
                        ["skip_base64"]   = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Omit Base64 payload from the response (default false)" }
                    },
                    ["required"] = new List<string> { "assembly_name", "resource_name" }
                }
            },
            new ToolInfo {
                Name = "add_resource",
                Description = "Embed a file from disk as a new EmbeddedResource (ManifestResource) in an assembly. Changes are in-memory until save_assembly is called.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Target assembly" },
                        ["resource_name"] = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Name for the new resource (e.g. MyApp.config or costura.foo.dll.compressed)" },
                        ["file_path"]     = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Absolute path to the file to embed" },
                        ["is_public"]     = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Resource visibility: true = Public (default), false = Private" }
                    },
                    ["required"] = new List<string> { "assembly_name", "resource_name", "file_path" }
                }
            },
            new ToolInfo {
                Name = "remove_resource",
                Description = "Remove a ManifestResource entry from an assembly by name. Changes are in-memory until save_assembly is called.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Assembly containing the resource" },
                        ["resource_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Exact resource name to remove (use list_resources to find it)" }
                    },
                    ["required"] = new List<string> { "assembly_name", "resource_name" }
                }
            },
            new ToolInfo {
                Name = "extract_costura",
                Description = "Detect and extract assemblies embedded by Costura.Fody. Costura stores them as EmbeddedResources named 'costura.{name}.dll.compressed' (gzip-compressed) or 'costura.{name}.dll' (uncompressed). Also handles .pdb files. Writes each extracted file to the output directory.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"]    = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Assembly that uses Costura.Fody (use list_resources to confirm costura.* resources exist)" },
                        ["output_directory"] = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Directory where extracted DLLs and PDBs will be written (created if it does not exist)" },
                        ["decompress"]       = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Decompress gzip-compressed resources (default true)" }
                    },
                    ["required"] = new List<string> { "assembly_name", "output_directory" }
                }
            },
        };

        // ── Debugger tools ────────────────────────────────────────────────────────
        List<ToolInfo> GetDebugToolSchemas() => new List<ToolInfo> {
            new ToolInfo {
                Name = "get_debugger_state",
                Description = "Get the current debugger state: whether debugging is active, running or paused, and process information",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>(),
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "list_breakpoints",
                Description = "List all code breakpoints currently registered in dnSpy",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>(),
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "set_breakpoint",
                Description = "Set a breakpoint at a method entry point (or specific IL offset). The breakpoint persists across debug sessions. Supports safe aliases like $arg0, $local0, arg(0), local(0), field(\"...\") and memberByToken(\"0x...\") in conditions when they can be mapped to a valid evaluator expression. Use file_path to select the right assembly when multiple share the same name.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                        ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type (supports nested types, e.g. 'AA9A3FB8')" },
                        ["method_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the method" },
                        ["il_offset"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "IL offset within the method body (default 0 = method entry)" },
                        ["file_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full path to the assembly file (optional; disambiguates when multiple assemblies share the same name)" },
                        ["condition"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional C# condition expression. Supports aliases like $arg0, $local0, arg(0), local(0), field(\"...\") and memberByToken(\"0x...\") when resolvable to a valid evaluator expression." }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                }
            },
            new ToolInfo {
                Name = "set_breakpoint_ex",
                Description = "Extended breakpoint setter. Same behavior as set_breakpoint, kept as a dedicated entry point for clients that want the alias-aware runtime workflow explicitly, including field(\"...\") and memberByToken(\"0x...\") helpers.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                        ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type" },
                        ["method_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the method" },
                        ["il_offset"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional IL offset (default 0)." },
                        ["file_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional assembly path to disambiguate duplicates." },
                        ["condition"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional C# condition expression with alias support such as $arg0, $local0, arg(0), local(0), field(\"...\") and memberByToken(\"0x...\")." }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                }
            },
            new ToolInfo {
                Name = "batch_breakpoints",
                Description = "Create multiple breakpoints in one call to reduce round-trips. Each item uses the same shape as set_breakpoint and supports alias-aware conditions.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["items"] = new Dictionary<string, object> {
                            ["type"] = "array",
                            ["description"] = "Array of breakpoint definitions. Each item requires assembly_name, type_full_name, and method_name; optional il_offset, file_path, and condition are also supported."
                        }
                    },
                    ["required"] = new List<string> { "items" }
                }
            },
            new ToolInfo {
                Name = "get_method_by_token",
                Description = "Resolve a managed MethodDef token to metadata and best-effort runtime load information. Use this when token-oriented reversing is more reliable than names.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Assembly containing the MethodDef token." },
                        ["token"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "MethodDef token as hex (0x06001234) or decimal." }
                    },
                    ["required"] = new List<string> { "assembly_name", "token" }
                }
            },
            new ToolInfo {
                Name = "trace_method",
                Description = "Create a persistent managed method trace interceptor backed by a dnSpy breakpoint plus an in-memory hit log. It does not pause execution; instead it records each hit until removed or max_calls is reached.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Assembly containing the target method." },
                        ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full type name. Required unless token is provided." },
                        ["method_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Method name. Required unless token is provided." },
                        ["token"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional MethodDef token as hex (0x06001234) or decimal. When supplied it is used instead of type_full_name/method_name." },
                        ["il_offset"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional IL offset inside the method body. Default 0 (method entry)." },
                        ["condition"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional alias-aware C# condition. Supports $argN, $localN, arg(N), local(N), field(\"...\"), and memberByToken(\"0x...\")." },
                        ["file_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional assembly path used to disambiguate duplicate loaded names." },
                        ["max_calls"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional auto-stop count. The interceptor removes itself after this many hits." },
                        ["max_log_entries"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Maximum number of in-memory hit records to retain. Default 256." }
                    },
                    ["required"] = new List<string> { "assembly_name" }
                }
            },
            new ToolInfo {
                Name = "hook_function",
                Description = "Create a persistent managed method interception session. action=break pauses on each hit, action=log traces without pausing, and action=count only keeps hit accounting. This is the managed interception entry point; modify_return is not implemented yet.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Assembly containing the target method." },
                        ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full type name. Required unless token is provided." },
                        ["method_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Method name. Required unless token is provided." },
                        ["token"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional MethodDef token as hex (0x06001234) or decimal." },
                        ["action"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Interception action: break, log, or count. Default break." },
                        ["il_offset"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional IL offset inside the method body. Default 0." },
                        ["condition"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional alias-aware C# condition expression." },
                        ["file_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional assembly path used to disambiguate duplicate loaded names." },
                        ["max_calls"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional auto-stop count. The interceptor removes itself after this many hits." },
                        ["max_log_entries"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Maximum number of hit records retained in memory. Default 256." }
                    },
                    ["required"] = new List<string> { "assembly_name" }
                }
            },
            new ToolInfo {
                Name = "list_active_interceptors",
                Description = "List currently active MCP-managed interception sessions. include_inactive=true also shows removed or exhausted sessions still kept for log retrieval.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["include_inactive"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Include inactive sessions whose breakpoint has already been removed." }
                    },
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "get_interceptor_log",
                Description = "Return the in-memory hit log for a persistent trace/hook session. Use list_active_interceptors first to discover session ids.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["session_id"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Interceptor session id returned by trace_method or hook_function." }
                    },
                    ["required"] = new List<string> { "session_id" }
                }
            },
            new ToolInfo {
                Name = "remove_interceptor",
                Description = "Remove a persistent interception session and delete its underlying dnSpy breakpoint.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["session_id"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Interceptor session id returned by trace_method or hook_function." }
                    },
                    ["required"] = new List<string> { "session_id" }
                }
            },
            new ToolInfo {
                Name = "remove_breakpoint",
                Description = "Remove a breakpoint from a specific method and IL offset",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                        ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type" },
                        ["method_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the method" },
                        ["il_offset"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "IL offset of the breakpoint (default 0)" }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                }
            },
            new ToolInfo {
                Name = "clear_all_breakpoints",
                Description = "Remove all visible breakpoints",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>(),
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "continue_debugger",
                Description = "Resume execution of all paused debugged processes",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>(),
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "break_debugger",
                Description = "Pause all currently running debugged processes. safe_pause=true documents the intent to avoid Debugger.Break()-style interruption; dnSpy MCP uses DbgManager.BreakAll() in both modes.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["safe_pause"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Prefer a debugger-engine pause strategy. dnSpy MCP uses BreakAll and does not call Debugger.Break()." }
                    },
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "stop_debugging",
                Description = "Stop all active debug sessions",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>(),
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "get_call_stack",
                Description = "Get the call stack of the current thread when the debugger is paused. Use break_debugger to pause first.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>(),
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "start_debugging",
                Description = "Launch an EXE under the dnSpy debugger. By default breaks at the entry point (after the module initializer has run, so ConfuserEx-decrypted method bodies are already in RAM). Use get_debugger_state to poll until paused.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["exe_path"]          = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Absolute path to the .NET Framework EXE to debug" },
                        ["arguments"]         = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Command-line arguments passed to the process (optional)" },
                        ["working_directory"] = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Working directory (default: EXE directory)" },
                        ["break_kind"]        = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Where to break: EntryPoint (default) | ModuleCctorOrEntryPoint | CreateProcess | DontBreak" }
                    },
                    ["required"] = new List<string> { "exe_path" }
                }
            },
            new ToolInfo {
                Name = "attach_to_process",
                Description = "Attach the dnSpy debugger to a running .NET process by its PID. Queries all installed debug engine providers for compatible CLR runtimes in the target process.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Process ID (PID) of the target process" }
                    },
                    ["required"] = new List<string> { "process_id" }
                }
            },
            new ToolInfo {
                Name = "step_over",
                Description = "Step over the current statement. Debugger must be paused. Waits for the step to complete (up to timeout_seconds) and returns the new execution location.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["process_id"]      = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: PID to target when multiple processes are debugged" },
                        ["thread_id"]       = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: specific thread ID to step (default: current/first paused thread)" },
                        ["timeout_seconds"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Max seconds to wait for step completion (default 30)" }
                    },
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "step_into",
                Description = "Step into the current statement (enters called methods). Debugger must be paused. Waits for the step to complete and returns the new execution location.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["process_id"]      = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: PID to target when multiple processes are debugged" },
                        ["thread_id"]       = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: specific thread ID to step (default: current/first paused thread)" },
                        ["timeout_seconds"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Max seconds to wait for step completion (default 30)" }
                    },
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "step_out",
                Description = "Step out of the current method (runs until the caller resumes). Debugger must be paused. Waits for the step to complete and returns the new execution location.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["process_id"]      = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: PID to target when multiple processes are debugged" },
                        ["thread_id"]       = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: specific thread ID to step (default: current/first paused thread)" },
                        ["timeout_seconds"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Max seconds to wait for step completion (default 30)" }
                    },
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "get_current_location",
                Description = "Return the current execution location (top frame) of the current or first paused thread. Debugger must be paused.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: PID to target when multiple processes are debugged" },
                        ["thread_id"]  = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: specific thread ID (default: current/first paused thread)" }
                    },
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "wait_for_pause",
                Description = "Poll until any debugged process becomes paused (e.g. after continue_debugger and a breakpoint hits). Returns process info once paused, or throws TimeoutException.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["timeout_seconds"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Max seconds to wait for a pause (default 30)" }
                    },
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "set_exception_breakpoint",
                Description = "Configure the debugger to pause when a specific exception type is thrown. Useful for catching Anti-Tamper crashes (TypeInitializationException), decryption faults, etc. Category defaults to 'DotNet' for all managed exceptions.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["exception_type"]  = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Full CLR exception type name, e.g. 'System.TypeInitializationException' or 'System.AccessViolationException'" },
                        ["first_chance"]    = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Break on first-chance (before the exception propagates). Default: true" },
                        ["second_chance"]   = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Break on second-chance (unhandled exception). Default: false" },
                        ["category"]        = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Exception category (default 'DotNet'). Use 'MDA' for Managed Debugging Assistants." }
                    },
                    ["required"] = new List<string> { "exception_type" }
                }
            },
            new ToolInfo {
                Name = "remove_exception_breakpoint",
                Description = "Remove an exception breakpoint previously set with set_exception_breakpoint.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["exception_type"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full CLR exception type name (must match the name used in set_exception_breakpoint)" },
                        ["category"]       = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Exception category (default 'DotNet')" }
                    },
                    ["required"] = new List<string> { "exception_type" }
                }
            },
            new ToolInfo {
                Name = "list_exception_breakpoints",
                Description = "List all exception breakpoints that currently have StopFirstChance or StopSecondChance enabled.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>(),
                    ["required"] = new List<string>()
                }
            },
        };

        // ── Memory / PE dump tools ────────────────────────────────────────────────
        List<ToolInfo> GetMemoryToolSchemas() => new List<ToolInfo> {
            new ToolInfo {
                Name = "list_runtime_modules",
                Description = "List all .NET modules loaded in the currently debugged processes. Requires an active debug session.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: filter by process ID" },
                        ["name_filter"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional: filter by module name (glob or regex)" }
                    },
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "dump_module_from_memory",
                Description = "Dump a loaded .NET module from process memory to a file. Uses IDbgDotNetRuntime for .NET modules (best quality), falling back to raw ReadMemory. Requires paused or active debug session.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["module_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Module name, filename, or basename (e.g. MyApp.dll)" },
                        ["output_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Absolute path to write the dumped module (e.g. C:\\dump\\MyApp_dumped.dll)" },
                        ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: target process ID when multiple processes are debugged" }
                    },
                    ["required"] = new List<string> { "module_name", "output_path" }
                }
            },
            new ToolInfo {
                Name = "read_process_memory",
                Description = "Read raw bytes from a debugged process address and return a formatted hex dump. Requires active debug session.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["address"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Memory address as hex string (0x7FF000) or decimal" },
                        ["size"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Number of bytes to read (1-65536)" },
                        ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: target process ID" }
                    },
                    ["required"] = new List<string> { "address", "size" }
                }
            },
            new ToolInfo {
                Name = "write_process_memory",
                Description = "Write bytes to a debugged process address (hot-patching / live memory editing). Useful for disabling checks or patching instructions without modifying the binary on disk. Requires an active debug session. Use read_process_memory to verify after writing. auto_virtual_protect can temporarily change protection with VirtualProtectEx and restore it after the patch.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["address"]      = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Target address as hex (0x7FF000) or decimal" },
                        ["bytes_base64"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Bytes to write as base64 (use this or hex_bytes)" },
                        ["hex_bytes"]    = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Bytes to write as hex string, e.g. \"90 90 FF\" or \"9090FF\" (use this or bytes_base64)" },
                        ["process_id"]   = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: target process ID when multiple processes are being debugged" },
                        ["auto_virtual_protect"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Temporarily switch page protection with VirtualProtectEx before writing and restore the original protection afterward." }
                    },
                    ["required"] = new List<string> { "address" }
                }
            },
            new ToolInfo {
                Name = "get_pe_sections",
                Description = "List PE sections (headers) of a module loaded in the debugged process memory. Returns section names, virtual addresses, sizes, and characteristics. Requires active debug session.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["module_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Module name, filename, or basename (e.g. MyApp.dll)" },
                        ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: target process ID" }
                    },
                    ["required"] = new List<string> { "module_name" }
                }
            },
            new ToolInfo {
                Name = "dump_pe_section",
                Description = "Extract a specific PE section (e.g. .text, .data, .rsrc) from a module in process memory. Writes to file and/or returns base64-encoded bytes. Requires active debug session.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["module_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Module name, filename, or basename (e.g. MyApp.dll)" },
                        ["section_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "PE section name, e.g. .text, .data, .rsrc, .rdata" },
                        ["output_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional: absolute path to write the section bytes to disk" },
                        ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: target process ID" }
                    },
                    ["required"] = new List<string> { "module_name", "section_name" }
                }
            },
            new ToolInfo {
                Name = "dump_module_unpacked",
                Description = "Dump a full module from process memory with memory-to-file layout conversion. Produces a valid PE file suitable for loading in dnSpy/IDA. Handles .NET, native, and mixed-mode modules. Requires active debug session.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["module_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Module name, filename, or basename (e.g. MyApp.dll)" },
                        ["output_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Absolute path to write the dumped PE file" },
                        ["try_fix_pe_layout"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Convert memory layout to file layout (section VA→PointerToRawData remapping). Default true." },
                        ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: target process ID" }
                    },
                    ["required"] = new List<string> { "module_name", "output_path" }
                }
            },
            new ToolInfo {
                Name = "dump_memory_to_file",
                Description = "Save a contiguous range of process memory directly to a file. Supports large ranges up to 256 MB. Useful for dumping unpacked payloads or large data buffers. Requires active debug session.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["address"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Start address as hex (0x7FF000) or decimal" },
                        ["size"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Number of bytes to dump (1 to 268435456 / 256 MB)" },
                        ["output_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Absolute path to write the memory dump" },
                        ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: target process ID" }
                    },
                    ["required"] = new List<string> { "address", "size", "output_path" }
                }
            },
            new ToolInfo {
                Name = "get_local_variables",
                Description = "Read local variables and parameters from a paused debug session stack frame. Returns primitive values, strings, and addresses for complex objects. Requires the debugger to be paused at a breakpoint.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["frame_index"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Stack frame index (0 = innermost/current frame, default 0)" },
                        ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: target process ID when multiple processes are being debugged" }
                    },
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "eval_expression",
                Description = "Evaluate a C# expression in the context of the current paused stack frame, equivalent to the Watch window in dnSpy. Returns the value with type information. Supports field/property access, method calls (with func_eval), arithmetic, casts, and safe aliases like $arg0, $local0, arg(0), local(0), field(\"...\") and memberByToken(\"0x...\") when they can be mapped to valid evaluator expressions. Requires the debugger to be paused.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["expression"]                = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "C# expression to evaluate. Supports aliases like $arg0, $local0, arg(0), local(0), field(\"...\") and memberByToken(\"0x...\") when resolvable by the dnSpy evaluator." },
                        ["frame_index"]               = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Stack frame index (0 = innermost/current, default 0)" },
                        ["process_id"]                = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional: target process ID when multiple processes are being debugged" },
                        ["func_eval_timeout_seconds"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Timeout for function evaluation calls in the debuggee (default 5s). Increase if the evaluated expression involves slow methods." }
                    },
                    ["required"] = new List<string> { "expression" }
                }
            },
            new ToolInfo {
                Name = "eval_expression_ex",
                Description = "Extended expression evaluation entry point for obfuscated/debugger-heavy workflows. Same evaluator behavior as eval_expression, including alias-aware arguments, locals, field(\"...\") and memberByToken(\"0x...\") helpers.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["expression"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "C# expression to evaluate. Supports $argN/$localN, arg(N)/local(N), field(\"...\") and memberByToken(\"0x...\") helpers." },
                        ["frame_index"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Stack frame index (0 = innermost/current, default 0)." },
                        ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional process ID when multiple processes are debugged." },
                        ["func_eval_timeout_seconds"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Timeout for debugger func-eval operations. Default 5 seconds." }
                    },
                    ["required"] = new List<string> { "expression" }
                }
            },
            new ToolInfo {
                Name = "unpack_from_memory",
                Description = "All-in-one unpacker for ConfuserEx and similar packers: launches the EXE under the debugger pausing at EntryPoint (after the module .cctor has decrypted method bodies), dumps the main module with PE-layout fix, and optionally stops the session. The output file contains readable IL and can be deobfuscated with deobfuscate_assembly.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["exe_path"]       = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Absolute path to the packed/protected .NET Framework EXE" },
                        ["output_path"]    = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Absolute path to write the unpacked EXE (directories are created automatically)" },
                        ["timeout_ms"]     = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Max milliseconds to wait for the process to pause at entry point (default 30000)" },
                        ["stop_after_dump"]= new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Stop the debug session after dumping (default true)" },
                        ["module_name"]    = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Override module name to search for (default: EXE filename). Use list_runtime_modules to discover names if auto-detect fails." }
                    },
                    ["required"] = new List<string> { "exe_path", "output_path" }
                }
            },
        };

        // ── Deobfuscation tools ───────────────────────────────────────────────────
        List<ToolInfo> GetDeobfuscationToolSchemas() => new List<ToolInfo> {
            new ToolInfo {
                Name = "run_de4dot",
                Description = "Run de4dot.exe as an external process to deobfuscate a .NET assembly. Preferred deobfuscation path because it supports the broadest de4dot feature set across builds.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["file_path"]        = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Path to the input .NET assembly to deobfuscate." },
                        ["output_path"]      = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Output path for the cleaned assembly (default: input + .deobfuscated.exe)." },
                        ["obfuscator_type"]  = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "de4dot type code to force: cr (ConfuserEx), un (unknown/auto), an, bl, co, df, dr3, dr4, ef, etc. Leave empty for auto-detection." },
                        ["dont_rename"]      = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "If true, don't rename obfuscated symbols (default false)." },
                        ["no_cflow_deob"]    = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "If true, skip control-flow deobfuscation (default false)." },
                        ["string_decrypter"] = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "String decrypter mode: none, default, static, delegate, emulate." },
                        ["extra_args"]       = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Any additional de4dot command-line arguments passed verbatim." },
                        ["de4dot_path"]      = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Override path to de4dot.exe. If omitted, uses well-known search paths." },
                        ["timeout_ms"]       = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Maximum time to wait for de4dot to finish (default 120000 ms)." }
                    },
                    ["required"] = new List<string> { "file_path" }
                }
            },
            new ToolInfo {
                Name = "list_deobfuscators",
                Description = "List all obfuscator types supported by the integrated de4dot engine (e.g. ConfuserEx, Dotfuscator, SmartAssembly, etc.).",
                InputSchema = new Dictionary<string, object> {
                    ["type"]       = "object",
                    ["properties"] = new Dictionary<string, object>(),
                    ["required"]   = new List<string>()
                }
            },
            new ToolInfo {
                Name = "detect_obfuscator",
                Description = "Detect which obfuscator was applied to a .NET assembly file on disk. Uses de4dot's heuristic detection engine.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["file_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Absolute path to the target DLL or EXE" }
                    },
                    ["required"] = new List<string> { "file_path" }
                }
            },
            new ToolInfo {
                Name = "deobfuscate_assembly",
                Description = "Deobfuscate a .NET assembly using in-process de4dot integration. Kept for compatibility; prefer run_de4dot as the primary deobfuscation path.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["file_path"]             = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Absolute path to the obfuscated DLL or EXE" },
                        ["output_path"]           = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Output path for the cleaned file (default: <name>-cleaned<ext> next to input)" },
                        ["method"]                = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Force a specific deobfuscator by Type, Name, or TypeLong (e.g. 'cr' for ConfuserEx). Auto-detected if omitted." },
                        ["rename_symbols"]        = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Rename obfuscated symbols (default true)" },
                        ["control_flow"]          = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Deobfuscate control flow (default true)" },
                        ["keep_obfuscator_types"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Keep obfuscator-internal types in the output (default false)" },
                        ["string_decrypter"]      = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "String decrypter mode: none | static | delegate | emulate (default static)" },
                        ["timeout_seconds"]       = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Max seconds to wait for deobfuscation (default 120, minimum 10)." }
                    },
                    ["required"] = new List<string> { "file_path" }
                }
            },
            new ToolInfo {
                Name = "save_deobfuscated",
                Description = "Return a previously deobfuscated file as a Base64-encoded blob. Useful when the output file cannot be accessed directly.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["file_path"]   = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Absolute path to the already-deobfuscated file" },
                        ["max_size_mb"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Reject files larger than this many megabytes (default 50)" }
                    },
                    ["required"] = new List<string> { "file_path" }
                }
            },
        };

        // ── Skills knowledge base ─────────────────────────────────────────────────
        List<ToolInfo> GetSkillsToolSchemas() => new List<ToolInfo> {
            new ToolInfo {
                Name = "list_skills",
                Description = "List all reverse-engineering skills/procedures in the knowledge base. Each skill has a Markdown narrative and a JSON technical record stored in %APPDATA%\\dnSpy\\dnSpy.MCPServer\\skills\\.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["tag"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional tag filter (case-insensitive substring match, e.g. 'packer')" }
                    },
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "get_skill",
                Description = "Retrieve the full content (Markdown narrative + JSON technical record) of a skill by ID.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["skill_id"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Skill ID slug (e.g. 'confuserex-unpacking'). Use list_skills to see available IDs." }
                    },
                    ["required"] = new List<string> { "skill_id" }
                }
            },
            new ToolInfo {
                Name = "save_skill",
                Description = "Create or update a skill in the knowledge base. Writes a Markdown narrative and/or JSON technical record with step-by-step procedures, magic values, crypto keys, prompts, and findings. Use merge=true to append new findings without overwriting existing data.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["skill_id"]    = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Skill ID (will be slugified). Use a descriptive name like 'confuserex-unpacking'." },
                        ["name"]        = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Human-readable name for the skill" },
                        ["description"] = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Short description of what this skill covers" },
                        ["tags"]        = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Comma-separated or JSON array of tags (e.g. 'packer,confuserex,unpacking')" },
                        ["targets"]     = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Comma-separated or JSON array of target assembly names / binary hashes this skill applies to" },
                        ["markdown"]    = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Markdown narrative: what to do, why, key observations, and procedure steps in prose" },
                        ["json_data"]   = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "JSON string with technical details: procedure steps (tool+prompt+expected), magic_values, crypto_keys, algorithms, offsets, findings, generic prompts (identify/apply/verify/troubleshoot)" },
                        ["merge"]       = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "If true, deep-merge json_data into the existing record instead of replacing it. Use to add new findings without losing old ones (default false)." }
                    },
                    ["required"] = new List<string> { "skill_id" }
                }
            },
            new ToolInfo {
                Name = "search_skills",
                Description = "Full-text search across all skill Markdown and JSON files. Returns matching skills with context snippets. Provide query, tag, or both.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["query"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Keyword or phrase to search for in skill Markdown and JSON content" },
                        ["tag"]   = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional tag filter (combined with query if both provided)" }
                    },
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "delete_skill",
                Description = "Permanently delete a skill (both Markdown and JSON files) from the knowledge base.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["skill_id"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Skill ID to delete. Use list_skills to see available IDs." }
                    },
                    ["required"] = new List<string> { "skill_id" }
                }
            },
        };

        // ── Scripting tools ───────────────────────────────────────────────────────
        List<ToolInfo> GetScriptingToolSchemas() => new List<ToolInfo> {
            new ToolInfo {
                Name = "run_script",
                Description = "Execute arbitrary C# code via Roslyn inside dnSpy's process. " +
                    "Globals available in scripts: `module` (ModuleDef? — currently selected assembly, or null), " +
                    "`allModules` (IReadOnlyList<ModuleDef> — all loaded assemblies), " +
                    "`docService` (IDsDocumentService), `dbgManager` (DbgManager? — null when no debug session), " +
                    "`print(value)` / `print(fmt, args)` — capture output lines. " +
                    "Pre-imported namespaces: System, System.Linq, System.IO, System.Text, System.Collections.Generic, " +
                    "System.Reflection, dnlib.DotNet, dnlib.DotNet.Emit, dnlib.DotNet.Writer. " +
                    "Return value (if any) is appended to output as 'Return: <value>'. " +
                    "Requires 'enableRunScript': true in mcp-config.json. Disabled by default.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["code"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "C# code to execute inside dnSpy's process. Has full access to all dnSpy APIs and loaded assemblies."
                        },
                        ["timeout_seconds"] = new Dictionary<string, object> {
                            ["type"] = "integer",
                            ["description"] = "Maximum execution time in seconds. Default: 30."
                        }
                    },
                    ["required"] = new List<string> { "code" }
                }
            },
        };

        // ── Window / dialog management ────────────────────────────────────────────
        List<ToolInfo> GetWindowToolSchemas() => new List<ToolInfo> {
            new ToolInfo {
                Name = "list_dialogs",
                Description = "List active dialog/message-box windows in the dnSpy process. " +
                    "Returns title, HWND, message text and available button labels for each dialog.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>(),
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "close_dialog",
                Description = "Close a dialog/message-box window by clicking a button. " +
                    "If no HWND given, closes the first active dialog found. " +
                    "Button matching is case-insensitive and supports English and Spanish: " +
                    "ok/aceptar, yes/sí, no, cancel/cancelar, retry/reintentar, ignore/omitir.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["hwnd"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Hex HWND of specific dialog (from list_dialogs). Optional."
                        },
                        ["button"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Button to click: ok (default), yes, no, cancel, retry, ignore."
                        }
                    },
                    ["required"] = new List<string>()
                }
            },
        };

        // ── SourceMap tools ──────────────────────────────────────────────────────
        List<ToolInfo> GetSourceMapToolSchemas() => new List<ToolInfo> {
            new ToolInfo {
                Name = "get_source_map_name",
                Description = "Resolve the current SourceMap name for a type/member without requiring the HoLLy UI decompiler. Constructors map to their declaring type, matching HoLLy's behavior.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Assembly that contains the target member." },
                        ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full type name. If member_kind/member_name are omitted, resolves the type mapping itself." },
                        ["member_kind"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional member kind: type, method, field, or property." },
                        ["member_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional member name. Required when member_kind is provided." }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name" }
                }
            },
            new ToolInfo {
                Name = "set_source_map_name",
                Description = "Set or update a SourceMap entry for a type/member and persist it to the MCP cache. Constructors map to their declaring type, matching HoLLy's SourceMap rules.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Assembly that contains the target member." },
                        ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full type name for the target." },
                        ["member_kind"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional member kind: type, method, field, or property." },
                        ["member_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional member name. Required when member_kind is provided." },
                        ["mapped_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Display name to store in the SourceMap." }
                    },
                    ["required"] = new List<string> { "assembly_name", "type_full_name", "mapped_name" }
                }
            },
            new ToolInfo {
                Name = "list_source_map_entries",
                Description = "List all cached SourceMap entries for an assembly. Use this as the detail view after save/load operations.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Assembly whose SourceMap cache should be listed." }
                    },
                    ["required"] = new List<string> { "assembly_name" }
                }
            },
            new ToolInfo {
                Name = "save_source_map",
                Description = "Save the current SourceMap cache for an assembly to disk. Defaults to the MCP cache directory under %APPDATA%\\dnSpy\\dnSpy.MCPServer\\sourcemaps\\.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Assembly whose SourceMap cache should be written." },
                        ["output_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional explicit output path. Defaults to the MCP SourceMap cache path." }
                    },
                    ["required"] = new List<string> { "assembly_name" }
                }
            },
            new ToolInfo {
                Name = "load_source_map",
                Description = "Load a SourceMap XML file for an assembly and mirror it into the MCP cache, without requiring HoLLy's menu commands.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Assembly that the SourceMap applies to." },
                        ["input_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Path to the SourceMap XML file to load." }
                    },
                    ["required"] = new List<string> { "assembly_name", "input_path" }
                }
            },
        };

        // ── Native runtime tools ────────────────────────────────────────────────
        List<ToolInfo> GetNativeRuntimeToolSchemas() => new List<ToolInfo> {
            new ToolInfo {
                Name = "get_proc_address",
                Description = "Resolve the exported address of a native function from a loaded module by parsing its PE export table and combining the export RVA with the mapped base address.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["module"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Loaded module name, filename, or full path (e.g. kernel32.dll)." },
                        ["function"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Exported function name to resolve." },
                        ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional target process ID when multiple debugged processes are present." }
                    },
                    ["required"] = new List<string> { "module", "function" }
                }
            },
            new ToolInfo {
                Name = "patch_native_function",
                Description = "Patch a loaded native export in memory by resolving its address from the PE export table and writing bytes at the mapped address. auto_virtual_protect defaults to true and restores the original protection after patching.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["module"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Loaded module name, filename, or full path." },
                        ["function"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Exported function name to patch." },
                        ["hex_bytes"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Patch bytes as hex, e.g. \"33 C0 C3\"." },
                        ["bytes"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Alias of hex_bytes for compatibility with patching workflows." },
                        ["bytes_base64"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Patch bytes as base64. Use this or the hex form." },
                        ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional target process ID when multiple debugged processes are present." },
                        ["auto_virtual_protect"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Temporarily switch memory protection with VirtualProtectEx before patching and restore it afterwards. Default true." }
                    },
                    ["required"] = new List<string> { "module", "function" }
                }
            },
            new ToolInfo {
                Name = "disassemble_native_function",
                Description = "Disassemble a loaded native export directly from process memory using Iced. This is the richer symbol-oriented native disassembly entry point over the lower-level read_native_memory(format=disasm).",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["module"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Loaded module name, filename, or full path." },
                        ["function"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Exported function name to disassemble." },
                        ["size"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Number of bytes to decode from the function start. Default 128." },
                        ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional target process ID when multiple debugged processes are present." }
                    },
                    ["required"] = new List<string> { "module", "function" }
                }
            },
            new ToolInfo {
                Name = "inject_native_dll",
                Description = "Inject a native DLL into the target process by writing the DLL path to remote memory and calling LoadLibraryW through CreateRemoteThread.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["dll_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Absolute path to the native DLL to inject." },
                        ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional target process ID." }
                    },
                    ["required"] = new List<string> { "dll_path" }
                }
            },
            new ToolInfo {
                Name = "inject_managed_dll",
                Description = "Inject a managed DLL and invoke a method inside the target process. For CLR targets it uses ExecuteInDefaultAppDomain; for Unity/Mono it uses mono_runtime_invoke. No UI dependency.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["dll_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Absolute path to the managed DLL to inject." },
                        ["type_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full type name containing the entry method. For Unity, namespace and type are derived from this value." },
                        ["method_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Managed method name to invoke." },
                        ["argument"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional string argument passed to the invoked method." },
                        ["copy_to_temp"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Copy the DLL to a temporary location before injection. Default false." },
                        ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional target process ID." }
                    },
                    ["required"] = new List<string> { "dll_path", "type_name", "method_name" }
                }
            },
            new ToolInfo {
                Name = "revert_patch",
                Description = "Revert a previously tracked native patch by patch_id and optionally restore protection with VirtualProtectEx.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["patch_id"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Patch identifier returned by patch_native_function." },
                        ["auto_virtual_protect"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Temporarily switch protection before restoring original bytes. Default true." }
                    },
                    ["required"] = new List<string> { "patch_id" }
                }
            },
            new ToolInfo {
                Name = "list_active_patches",
                Description = "List tracked runtime patches created through patch_native_function, including original bytes and timestamps.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>(),
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "read_native_memory",
                Description = "Read native memory from the current debugged process with formatting optimized for reversing workflows. format can be hex, ascii, or disasm (Iced-based x86/x64 decode).",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["address"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Target address as hex (0x7FFE1234) or decimal." },
                        ["size"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Number of bytes to read (1-65536)." },
                        ["format"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Output format: hex, ascii, or disasm. Default hex." },
                        ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional target process ID." }
                    },
                    ["required"] = new List<string> { "address", "size" }
                }
            },
            new ToolInfo {
                Name = "suspend_threads",
                Description = "Suspend all threads in the current debugged process or a selected subset. Uses dnSpy's thread Freeze() API and tracks only suspensions made through this tool.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional target process ID." },
                        ["thread_ids"] = new Dictionary<string, object> { ["type"] = "array", ["description"] = "Optional array of thread IDs to suspend. If omitted, all process threads are targeted." }
                    },
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "resume_threads",
                Description = "Resume threads previously frozen through suspend_threads. This only thaws suspensions tracked by the MCP tool to avoid touching unrelated debugger state.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional target process ID." },
                        ["thread_ids"] = new Dictionary<string, object> { ["type"] = "array", ["description"] = "Optional array of thread IDs to resume. If omitted, all tracked frozen threads in the process are targeted." }
                    },
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "get_peb",
                Description = "Read best-effort PEB anti-debug fields from the current debugged process, including BeingDebugged, NtGlobalFlag, and heap flags when available.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["process_id"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Optional target process ID." }
                    },
                    ["required"] = new List<string>()
                }
            },
        };

        // ── Utility tools ─────────────────────────────────────────────────────────
        List<ToolInfo> GetUtilityToolSchemas() => new List<ToolInfo> {
            new ToolInfo {
                Name = "list_tools",
                Description = "List MCP tools with catalog metadata. Default mode hides tools marked hidden_by_default; pass mode='full' or include_hidden=true to see the complete catalog.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object> {
                        ["mode"] = new Dictionary<string, object> {
                            ["type"] = "string",
                            ["description"] = "Catalog listing mode: 'default' hides hidden_by_default tools, 'full' returns the complete catalog."
                        },
                        ["include_hidden"] = new Dictionary<string, object> {
                            ["type"] = "boolean",
                            ["description"] = "Optional explicit override. When true, include tools marked hidden_by_default."
                        }
                    },
                    ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "get_mcp_config",
                Description = "Return the current MCP server configuration and the path to mcp-config.json. Use this to find where to edit the config file.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object", ["properties"] = new Dictionary<string, object>(), ["required"] = new List<string>()
                }
            },
            new ToolInfo {
                Name = "reload_mcp_config",
                Description = "Reload mcp-config.json from disk without restarting dnSpy. Call this after editing the config file to apply changes immediately.",
                InputSchema = new Dictionary<string, object> {
                    ["type"] = "object", ["properties"] = new Dictionary<string, object>(), ["required"] = new List<string>()
                }
            },
        };

        static void AddToolRange(List<ToolInfo> destination, List<ToolInfo> tools, string category)
        {
            foreach (var tool in tools)
            {
                tool.Catalog = BuildCatalogMetadata(tool.Name, category);
                destination.Add(tool);
            }
        }

        static List<ToolInfo> FilterCatalogTools(List<ToolInfo> tools, ToolCatalogFilter? filter)
        {
            if (filter == null || filter.IncludeHiddenByDefault)
                return tools;

            return tools
                .Where(t => t.Catalog?.HiddenByDefault != true)
                .ToList();
        }

        static ToolCatalogMetadata BuildCatalogMetadata(string toolName, string defaultCategory)
        {
            var metadata = new ToolCatalogMetadata
            {
                Category = ResolveCategory(toolName, defaultCategory)
            };

            switch (toolName)
            {
            case "list_native_modules":
            case "dump_module_from_memory":
            case "close_all_assemblies":
            case "reload_mcp_config":
            case "list_dialogs":
            case "close_dialog":
                metadata.HiddenByDefault = true;
                break;
            }

            switch (toolName)
            {
            case "list_assembly_attributes":
                metadata.IsLegacy = true;
                metadata.PreferredReplacement = "get_assembly_metadata";
                metadata.Notes = "Assembly attribute listing is available through get_assembly_metadata. Use this tool only when you need the legacy attribute-only shape.";
                break;
            case "deobfuscate_assembly":
                metadata.IsLegacy = true;
                metadata.PreferredReplacement = "run_de4dot";
                metadata.Notes = "Compatibility path for in-process deobfuscation. Prefer run_de4dot for the primary and broader-featured workflow.";
                break;
            case "run_de4dot":
                metadata.Notes = "Primary deobfuscation entry point. Prefer this over deobfuscate_assembly for new clients.";
                break;
            case "get_basic_blocks":
                metadata.Notes = "Reduced CFG view. Use get_control_flow_graph when you need edges, metrics, and entry/exit semantics.";
                break;
            case "get_control_flow_graph":
                metadata.Notes = "Full CFG view. Prefer this over get_basic_blocks when you need edge kinds, metrics, or loop hints.";
                break;
            case "eval_expression_ex":
                metadata.Notes = "Extended entry point for debugger-heavy workflows. Prefer eval_expression for normal use and this variant when a client wants the explicit obfuscation-aware contract.";
                break;
            case "set_breakpoint_ex":
                metadata.Notes = "Extended entry point for alias-aware breakpoint workflows. Prefer set_breakpoint for standard use.";
                break;
            case "trace_method":
                metadata.Notes = "Persistent managed tracing without pausing execution. Prefer this over hook_function when you only need hit logs.";
                break;
            case "hook_function":
                metadata.Notes = "Persistent managed interception built on dnSpy breakpoints. Use action=break for pause-on-hit, action=log for tracing, and action=count for lightweight hit accounting.";
                break;
            case "list_active_interceptors":
                metadata.Notes = "Summary view of all MCP-managed interception sessions. Use get_interceptor_log for the detailed per-hit view.";
                break;
            case "get_interceptor_log":
                metadata.Notes = "Detail hit log for one interception session. Use list_active_interceptors first to discover session ids.";
                break;
            case "read_native_memory":
                metadata.Notes = "Native reversing view over process memory. Prefer this over read_process_memory when you want ascii or disassembly formatting.";
                break;
            case "disassemble_native_function":
                metadata.Notes = "Symbol-oriented Iced disassembly. Prefer this over read_native_memory when you already know the module and export name.";
                break;
            case "inject_managed_dll":
                metadata.Notes = "Managed code injection without UI. Uses CLR ExecuteInDefaultAppDomain for classic CLR and mono_runtime_invoke for Unity.";
                break;
            case "inject_native_dll":
                metadata.Notes = "Native DLL injection via LoadLibraryW and CreateRemoteThread.";
                break;
            case "get_type_info":
                metadata.Notes = "Summary-first type inspection. Prefer this before calling detail tools for methods, properties, or fields.";
                break;
            case "list_methods_in_type":
                metadata.Notes = "Detail method enumeration. Prefer get_type_info first when exploring a type.";
                break;
            case "list_properties_in_type":
                metadata.Notes = "Detail property enumeration. Prefer get_type_info first when exploring a type.";
                break;
            case "list_tools":
                metadata.Notes = "Default mode hides tools marked hidden_by_default. Use mode='full' or include_hidden=true for the complete catalog.";
                break;
            }

            return metadata;
        }

        static string ResolveCategory(string toolName, string defaultCategory) => toolName switch
        {
            "load_assembly" => "admin",
            "select_assembly" => "admin",
            "close_assembly" => "admin",
            "close_all_assemblies" => "admin",
            "trace_method" => "debug-runtime",
            "hook_function" => "debug-runtime",
            "list_active_interceptors" => "debug-runtime",
            "get_interceptor_log" => "debug-runtime",
            "remove_interceptor" => "debug-runtime",
            _ => defaultCategory
        };
    }
}
