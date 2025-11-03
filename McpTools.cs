using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Text;

namespace dnSpy.MCP.Server
{
    /// <summary>
    /// Implements MCP tools for analyzing .NET assemblies and generating code.
    /// Provides tools for listing assemblies, inspecting types, decompiling methods, and generating BepInEx plugins.
    /// </summary>
    [Export(typeof(McpTools))]
    public sealed class McpTools
    {
        readonly IDocumentTreeView documentTreeView;
        readonly IDecompilerService decompilerService;
        readonly Lazy<AssemblyTools> assemblyTools;
        readonly Lazy<TypeTools> typeTools;
        readonly Lazy<UsageFindingCommandTools> usageFindingCommandTools;

        /// <summary>
        /// Initializes the MCP tools with dnSpy services and delegates.
        /// </summary>
        [ImportingConstructor]
        public McpTools(IDocumentTreeView documentTreeView, IDecompilerService decompilerService, Lazy<AssemblyTools> assemblyTools, Lazy<TypeTools> typeTools, Lazy<UsageFindingCommandTools> usageFindingCommandTools)
        {
            this.documentTreeView = documentTreeView;
            this.decompilerService = decompilerService;
            this.assemblyTools = assemblyTools;
            this.typeTools = typeTools;
            this.usageFindingCommandTools = usageFindingCommandTools;
        }

        #region Section A: Tool Registry & Dispatch

        /// <summary>
        /// Gets the list of available MCP tools with their schemas.
        /// </summary>
        public List<ToolInfo> GetAvailableTools()
        {
            return new List<ToolInfo> {
                new ToolInfo {
                    Name = "find_who_uses_type",
                    Description = "Find all types, methods, and fields that reference a specific type. Shows where a type is used throughout loaded assemblies.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly containing the type" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type to find usages for" },
                            ["include_derived"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Include derived types and implementations (default: true)" }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name" }
                    }
                },
                new ToolInfo {
                    Name = "find_who_calls_method",
                    Description = "Find all methods that call a specific method. Analyzes IL code to identify method invocations.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly containing the method" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type containing the method" },
                            ["method_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the method to find callers for" }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                    }
                },
                new ToolInfo {
                    Name = "find_who_reads_field",
                    Description = "Find all methods that read a specific field. Identifies LDFLD and LDSFLD IL instructions.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly containing the field" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type containing the field" },
                            ["field_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the field to find readers for" }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "field_name" }
                    }
                },
                new ToolInfo {
                    Name = "find_who_writes_field",
                    Description = "Find all methods that write to a specific field. Identifies STFLD and STSFLD IL instructions.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly containing the field" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type containing the field" },
                            ["field_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the field to find writers for" }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "field_name" }
                    }
                },
                new ToolInfo {
                    Name = "find_dependency_chain",
                    Description = "Build complete dependency chain from one type to another. Shows all intermediate types and dependencies.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["from_assembly"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Source assembly name" },
                            ["from_type"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Starting type full name" },
                            ["to_assembly"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Target assembly name (optional, if different)" },
                            ["to_type"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Target type full name" },
                            ["max_depth"] = new Dictionary<string, object> { ["type"] = "number", ["description"] = "Maximum depth to search (default: 10)" }
                        },
                        ["required"] = new List<string> { "from_assembly", "from_type", "to_type" }
                    }
                },
                new ToolInfo {
                    Name = "analyze_call_graph",
                    Description = "Analyze complete call graph for a method. Shows all methods it calls and their callers recursively.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type" },
                            ["method_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the method" },
                            ["direction"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Search direction: 'calls' (what it calls), 'called_by' (who calls it), 'both' (default: calls)" }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                    }
                },
                new ToolInfo {
                    Name = "find_exposed_interfaces",
                    Description = "Find all public methods/properties that expose internal types through their signatures.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                            ["internal_type"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the internal type to find exposed in public APIs" }
                        },
                        ["required"] = new List<string> { "assembly_name", "internal_type" }
                    }
                },
                new ToolInfo {
                    Name = "trace_data_flow",
                    Description = "Trace data flow through a method. Analyzes IL to show parameter usage and variable flow.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type" },
                            ["method_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the method to analyze" },
                            ["parameter_index"] = new Dictionary<string, object> { ["type"] = "number", ["description"] = "Optional: trace specific parameter (0-indexed)" }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                    }
                },
                new ToolInfo {
                    Name = "find_dead_code",
                    Description = "Find unused types, methods, and fields that are never referenced.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly to analyze" },
                            ["include_private"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Include private members (default: true)" },
                            ["include_internal"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Include internal members (default: true)" }
                        },
                        ["required"] = new List<string> { "assembly_name" }
                    }
                },
                new ToolInfo {
                    Name = "analyze_cross_assembly_dependencies",
                    Description = "Analyze all dependencies between assemblies. Shows which assemblies depend on which others.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["focus_assembly"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional: focus on specific assembly and its dependencies" }
                        },
                        ["required"] = new List<string>()
                    }
                },
                new ToolInfo {
                    Name = "list_tools",
                    Description = "List all available MCP tools with their descriptions and schemas",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>(),
                        ["required"] = new List<string>()
                    }
                },
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
                    Description = "Get detailed information about a specific assembly. Supports pagination of namespaces with default page size of 10.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional cursor for pagination of namespaces (opaque token from previous response). Default page size: 10 namespaces."
                            }
                        },
                        ["required"] = new List<string> { "assembly_name" }
                    }
                },
                new ToolInfo {
                    Name = "list_types",
                    Description = "List all types in an assembly or namespace. Supports pagination with default page size of 10 types.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["namespace"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional namespace filter"
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional cursor for pagination (opaque token from previous response). Default page size: 10 types."
                            }
                        },
                        ["required"] = new List<string> { "assembly_name" }
                    }
                },
                new ToolInfo {
                    Name = "get_type_info",
                    Description = "Get detailed information about a specific type including its members. First request returns all fields/properties and paginated methods. Subsequent requests (with cursor) return only paginated methods to reduce token usage.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["type_full_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Full name of the type including namespace"
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional cursor for pagination of methods (opaque token from previous response). Default page size: 10 methods."
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name" }
                    }
                },
                new ToolInfo {
                    Name = "decompile_method",
                    Description = "Decompile a specific method to C# code",
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
                    Name = "search_types",
                    Description = "Search for types by name across all loaded assemblies. Supports pagination with default page size of 10 results.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["query"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Search query. Wildcards (*) match against FullName (namespace + type name). Recommended patterns: '*TypeName' for suffix (e.g., '*Controller' finds MyNamespace.PlayerController), '*.Keyword*' for types containing keyword, 'Full.Namespace.Path.*' for specific namespace. Without wildcards, performs case-insensitive substring matching (e.g., 'Controller' finds all types with 'Controller' in name)."
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional cursor for pagination (opaque token from previous response). Default page size: 10 results."
                            }
                        },
                        ["required"] = new List<string> { "query" }
                    }
                },
                new ToolInfo {
                    Name = "generate_bepinex_plugin",
                    Description = "Generate a BepInEx plugin template with hooks for specified methods",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["plugin_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the plugin"
                            },
                            ["plugin_guid"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "GUID for the plugin"
                            },
                            ["target_assembly"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Target assembly name"
                            },
                            ["hooks"] = new Dictionary<string, object> {
                                ["type"] = "array",
                                ["description"] = "Array of methods to hook",
                                ["items"] = new Dictionary<string, object> {
                                    ["type"] = "object",
                                    ["properties"] = new Dictionary<string, object> {
                                        ["type_name"] = new Dictionary<string, object> { ["type"] = "string" },
                                        ["method_name"] = new Dictionary<string, object> { ["type"] = "string" }
                                    }
                                }
                            }
                        },
                        ["required"] = new List<string> { "plugin_name", "plugin_guid", "target_assembly" }
                    }
                },
                new ToolInfo {
                    Name = "get_type_fields",
                    Description = "Get fields from a type matching a name pattern (supports wildcards like *Bonus*). Supports pagination with default page size of 10 fields.",
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
                            ["pattern"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Field name pattern (supports * wildcard)"
                            },
                            ["cursor"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional cursor for pagination of fields (opaque token from previous response). Default page size: 10 fields."
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "pattern" }
                    }
                },
                new ToolInfo {
                    Name = "get_type_property",
                    Description = "Get detailed information about a specific property from a type",
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
                            ["property_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the property"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "property_name" }
                    }
                },
                new ToolInfo {
                    Name = "find_path_to_type",
                    Description = "Find property/field chains connecting two types through their members. (e.g., PlayerState -> RpBonus)",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["from_type"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Starting type full name"
                            },
                            ["to_type"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Target type full name or partial name"
                            },
                            ["max_depth"] = new Dictionary<string, object> {
                                ["type"] = "number",
                                ["description"] = "Maximum search depth (default: 5)"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name", "from_type", "to_type" }
                    }
                },
                new ToolInfo {
                    Name = "get_method_signature",
                    Description = "Get detailed method signature including parameters, return type, and attributes",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type" },
                            ["method_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the method" }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                    }
                },
                new ToolInfo {
                    Name = "list_methods_in_type",
                    Description = "List all methods in a type with filtering by visibility (public/private/protected). Supports pagination.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type" },
                            ["visibility"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Filter by visibility (public, private, protected, internal). Omit for all." },
                            ["cursor"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional cursor for pagination" }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name" }
                    }
                },
                new ToolInfo {
                    Name = "list_properties_in_type",
                    Description = "List all properties in a type with read/write information. Supports pagination.",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type" },
                            ["cursor"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional cursor for pagination" }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name" }
                    }
                },
                new ToolInfo {
                    Name = "find_type_references",
                    Description = "Find all places where a type is referenced (used as field, parameter, return type, etc)",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type to find references for" },
                            ["cursor"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional cursor for pagination" }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name" }
                    }
                },
                new ToolInfo {
                    Name = "analyze_type_inheritance",
                    Description = "Analyze complete inheritance chain of a type (base classes and interfaces)",
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
                    Name = "get_constant_values",
                    Description = "Extract constant values defined in a type (literal fields and enums)",
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
                    Name = "generate_harmony_patch",
                    Description = "Generate a complete HarmonyX patch for a method with Prefix/Postfix/Transpiler templates",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the assembly" },
                            ["type_full_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full name of the type" },
                            ["method_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Name of the method to patch" },
                            ["patch_types"] = new Dictionary<string, object> { ["type"] = "array", ["items"] = new Dictionary<string, object> { ["type"] = "string" }, ["description"] = "Array of patch types: 'prefix', 'postfix', 'transpiler'" }
                        },
                        ["required"] = new List<string> { "assembly_name", "type_full_name", "method_name" }
                    }
                },
                new ToolInfo {
                    Name = "find_virtual_method_overrides",
                    Description = "Find all virtual methods and their overrides in a type hierarchy",
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
                    Name = "suggest_hook_points",
                    Description = "Suggest good hook points (methods/events) in a type for BepInEx plugin development",
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
                    Name = "find_pinvoke_signatures",
                    Description = "Find all P/Invoke (DllImport) signatures in the assembly",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["filter"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional filter applied to type/method/dll"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name" }
                    }
                },
                new ToolInfo {
                    Name = "analyze_marshalling_and_layout",
                    Description = "Analyze StructLayout and marshalling heuristics for types used in interop",
                    InputSchema = new Dictionary<string, object> {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["assembly_name"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Name of the assembly"
                            },
                            ["type_filter"] = new Dictionary<string, object> {
                                ["type"] = "string",
                                ["description"] = "Optional wildcard filter for types"
                            }
                        },
                        ["required"] = new List<string> { "assembly_name" }
                    }
                }
            };
        }

        /// <summary>
        /// Executes a specific MCP tool by name with the given arguments.
        /// </summary>
        /// <param name="toolName">The name of the tool to execute.</param>
        /// <param name="arguments">Tool-specific arguments.</param>
        /// <returns>The tool execution result.</returns>
        public CallToolResult ExecuteTool(string toolName, Dictionary<string, object>? arguments)
        {
            McpLogger.Info($"Executing tool: {toolName}");
            var startTime = DateTime.Now;

            try
            {
                var result = toolName switch
                {
                    "find_who_uses_type" => FindWhoUsesType(arguments),
                    "find_who_calls_method" => FindWhoCallsMethod(arguments),
                    "find_who_reads_field" => FindWhoReadsField(arguments),
                    "find_who_writes_field" => FindWhoWritesField(arguments),
                    "find_dependency_chain" => FindDependencyChain(arguments),
                    "analyze_call_graph" => AnalyzeCallGraph(arguments),
                    "find_exposed_interfaces" => FindExposedInterfaces(arguments),
                    "trace_data_flow" => TraceDataFlow(arguments),
                    "find_dead_code" => FindDeadCode(arguments),
                    "analyze_cross_assembly_dependencies" => AnalyzeCrossAssemblyDependencies(arguments),
                    "list_tools" => ListTools(),
                    // Assembly-focused commands delegated (reflection invocation)
                    "list_assemblies" => InvokeLazy(assemblyTools, "ListAssemblies", null),
                    "get_assembly_info" => InvokeLazy(assemblyTools, "GetAssemblyInfo", arguments),
                    "list_types" => InvokeLazy(assemblyTools, "ListTypes", arguments),
                    "list_native_modules" => InvokeLazy(assemblyTools, "ListNativeModules", arguments),
                    // Type-focused commands delegated (reflection)
                    "get_type_info" => InvokeLazy(typeTools, "GetTypeInfo", arguments),
                    "decompile_method" => InvokeLazy(typeTools, "DecompileMethod", arguments),
                    "list_methods_in_type" => InvokeLazy(typeTools, "ListMethodsInType", arguments),
                    "list_properties_in_type" => InvokeLazy(typeTools, "ListPropertiesInType", arguments),
                    "get_type_fields" => InvokeLazy(typeTools, "GetTypeFields", arguments),
                    "get_type_property" => InvokeLazy(typeTools, "GetTypeProperty", arguments),
                    "get_method_signature" => InvokeLazy(typeTools, "GetMethodSignature", arguments),
                    "get_constant_values" => InvokeLazy(typeTools, "GetConstantValues", arguments),
                    "search_types" => InvokeLazy(typeTools, "SearchTypes", arguments),
                    "find_pinvoke_signatures" => CallInterop("FindPInvokeSignatures", arguments),
                    "analyze_marshalling_and_layout" => CallInterop("AnalyzeMarshallingAndLayout", arguments),
                    "find_path_to_type" => InvokeLazy(typeTools, "FindPathToType", arguments),
                    "find_type_references" => FindTypeReferences(arguments),
                    "analyze_type_inheritance" => AnalyzeTypeInheritance(arguments),
                    "generate_bepinex_plugin" => GenerateBepInExPlugin(arguments),
                    "generate_harmony_patch" => GenerateHarmonyPatch(arguments),
                    "find_virtual_method_overrides" => FindVirtualMethodOverrides(arguments),
                    "suggest_hook_points" => SuggestHookPoints(arguments),
                    _ => new CallToolResult
                    {
                        Content = new List<ToolContent> {
                            new ToolContent { Text = $"Unknown tool: {toolName}" }
                        },
                        IsError = true
                    }
                };

                var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                McpLogger.Info($"Tool {toolName} completed in {elapsed:F2}ms");
                return result;
            }
            catch (Exception ex)
            {
                var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                McpLogger.Exception(ex, $"Error executing tool {toolName} (after {elapsed:F2}ms)");
                return new CallToolResult
                {
                    Content = new List<ToolContent> {
                        new ToolContent { Text = $"Error executing tool {toolName}: {ex.Message}" }
                    },
                    IsError = true
                };
            }
        }

        #endregion

        #region Section B: Assembly Commands

        CallToolResult ListAssemblies()
        {
            var assemblies = documentTreeView.GetAllModuleNodes()
                .Select(m => m.Document?.AssemblyDef)
                .Where(a => a != null)
                .Distinct()
                .Select(a => new
                {
                    Name = a!.Name.String,
                    Version = a.Version?.ToString() ?? "N/A",
                    FullName = a.FullName,
                    Culture = a.Culture ?? "neutral",
                    PublicKeyToken = a.PublicKeyToken?.ToString() ?? "null"
                })
                .ToList();

            McpLogger.Debug($"Found {assemblies.Count} assemblies");

            var result = JsonSerializer.Serialize(assemblies, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        CallToolResult GetAssemblyInfo(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            var modules = assembly.Modules.Select(m => new
            {
                Name = m.Name.String,
                Kind = m.Kind.ToString(),
                Architecture = m.Machine.ToString(),
                RuntimeVersion = m.RuntimeVersion
            }).ToList();

            var allNamespaces = assembly.Modules
                .SelectMany(m => m.Types)
                .Select(t => t.Namespace.String)
                .Distinct()
                .OrderBy(ns => ns)
                .ToList();

            var namespacesToReturn = allNamespaces.Skip(offset).Take(pageSize).ToList();
            var hasMore = offset + pageSize < allNamespaces.Count;

            var info = new Dictionary<string, object>
            {
                ["Name"] = assembly.Name.String,
                ["Version"] = assembly.Version?.ToString() ?? "N/A",
                ["FullName"] = assembly.FullName,
                ["Culture"] = assembly.Culture ?? "neutral",
                ["PublicKeyToken"] = assembly.PublicKeyToken?.ToString() ?? "null",
                ["Modules"] = modules,
                ["Namespaces"] = namespacesToReturn,
                ["NamespacesTotalCount"] = allNamespaces.Count,
                ["NamespacesReturnedCount"] = namespacesToReturn.Count,
                ["TypeCount"] = assembly.Modules.Sum(m => m.Types.Count)
            };

            if (hasMore)
            {
                info["nextCursor"] = EncodeCursor(offset + pageSize, pageSize);
            }

            var result = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        CallToolResult ListTypes(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            string? namespaceFilter = null;
            if (arguments.TryGetValue("namespace", out var nsObj))
                namespaceFilter = nsObj.ToString();

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            var types = assembly.Modules
                .SelectMany(m => m.Types)
                .Where(t => string.IsNullOrEmpty(namespaceFilter) || t.Namespace == namespaceFilter)
                .Select(t => new
                {
                    FullName = t.FullName,
                    Namespace = t.Namespace.String,
                    Name = t.Name.String,
                    IsPublic = t.IsPublic,
                    IsClass = t.IsClass,
                    IsInterface = t.IsInterface,
                    IsEnum = t.IsEnum,
                    IsValueType = t.IsValueType,
                    IsAbstract = t.IsAbstract,
                    IsSealed = t.IsSealed,
                    BaseType = t.BaseType?.FullName ?? "None"
                })
                .ToList();

            return CreatePaginatedResponse(types, offset, pageSize);
        }

        CallToolResult GetTypeInfo(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeFullName = typeNameObj.ToString() ?? string.Empty;

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeFullName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeFullName}");

            var allMethods = type.Methods.Select(m => new
            {
                Name = m.Name.String,
                Signature = m.FullName,
                IsPublic = m.IsPublic,
                IsStatic = m.IsStatic,
                IsVirtual = m.IsVirtual,
                IsAbstract = m.IsAbstract,
                ReturnType = m.ReturnType?.FullName ?? "void",
                Parameters = m.Parameters.Select(p => new
                {
                    Name = p.Name,
                    Type = p.Type.FullName
                }).ToList()
            }).ToList();

            var fields = type.Fields.Select(f => new
            {
                Name = f.Name.String,
                Type = f.FieldType.FullName,
                IsPublic = f.IsPublic,
                IsStatic = f.IsStatic,
                IsLiteral = f.IsLiteral
            }).ToList();

            var properties = type.Properties.Select(p => new
            {
                Name = p.Name.String,
                Type = p.PropertySig?.RetType?.FullName ?? "unknown",
                CanRead = p.GetMethod != null,
                CanWrite = p.SetMethod != null
            }).ToList();

            var methodsToReturn = allMethods.Skip(offset).Take(pageSize).ToList();
            var hasMore = offset + pageSize < allMethods.Count;
            var isFirstRequest = string.IsNullOrEmpty(cursor);

            var info = new Dictionary<string, object>
            {
                ["FullName"] = type.FullName,
                ["Namespace"] = type.Namespace.String,
                ["Name"] = type.Name.String,
                ["IsPublic"] = type.IsPublic,
                ["IsClass"] = type.IsClass,
                ["IsInterface"] = type.IsInterface,
                ["IsEnum"] = type.IsEnum,
                ["IsValueType"] = type.IsValueType,
                ["IsAbstract"] = type.IsAbstract,
                ["IsSealed"] = type.IsSealed,
                ["BaseType"] = type.BaseType?.FullName ?? "None",
                ["Interfaces"] = type.Interfaces.Select(i => i.Interface.FullName).ToList(),
                ["Methods"] = methodsToReturn,
                ["MethodsTotalCount"] = allMethods.Count,
                ["MethodsReturnedCount"] = methodsToReturn.Count
            };

            // Only include fields and properties on first request to reduce token usage
            // For subsequent paginated requests, only return methods
            if (isFirstRequest)
            {
                info["Fields"] = fields;
                info["FieldsCount"] = fields.Count;
                info["Properties"] = properties;
                info["PropertiesCount"] = properties.Count;
            }
            else
            {
                // For paginated requests, just include counts
                info["FieldsCount"] = fields.Count;
                info["PropertiesCount"] = properties.Count;
            }

            if (hasMore)
            {
                info["nextCursor"] = EncodeCursor(offset + pageSize, pageSize);
            }

            var result = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        CallToolResult DecompileMethod(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");
            if (!arguments.TryGetValue("method_name", out var methodNameObj))
                throw new ArgumentException("method_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeFullName = typeNameObj.ToString() ?? string.Empty;
            var methodName = methodNameObj.ToString() ?? string.Empty;

            McpLogger.Debug($"Decompiling method: {typeFullName}.{methodName} from {assemblyName}");

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeFullName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeFullName}");

            var method = type.Methods.FirstOrDefault(m => m.Name == methodName);
            if (method == null)
                throw new ArgumentException($"Method not found: {methodName}");

            // Decompile the method
            var decompiler = decompilerService.Decompiler;
            var output = new StringBuilderDecompilerOutput();
            var decompilationContext = new DecompilationContext
            {
                CancellationToken = System.Threading.CancellationToken.None
            };

            decompiler.Decompile(method, output, decompilationContext);
            McpLogger.Debug($"Decompilation completed - {output.Length} characters generated");

            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = output.ToString() }
                }
            };
        }

        CallToolResult SearchTypes(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("query", out var queryObj))
                throw new ArgumentException("query is required");

            var query = queryObj.ToString() ?? string.Empty;
            var queryLower = query.ToLowerInvariant();

            McpLogger.Debug($"Searching types with query: '{query}'");

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            // Check if query contains wildcards
            bool hasWildcard = query.Contains("*");
            System.Text.RegularExpressions.Regex? regex = null;

            if (hasWildcard)
            {
                // Convert wildcard pattern to regex
                var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(queryLower).Replace("\\*", ".*") + "$";
                regex = new System.Text.RegularExpressions.Regex(regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            var results = documentTreeView.GetAllModuleNodes()
                .SelectMany(m => m.Document?.ModuleDef?.Types ?? Enumerable.Empty<TypeDef>())
                .Where(t => hasWildcard ? regex!.IsMatch(t.FullName) : t.FullName.ToLowerInvariant().Contains(queryLower))
                .Select(t => new
                {
                    AssemblyName = t.Module?.Assembly?.Name.String ?? "Unknown",
                    FullName = t.FullName,
                    Namespace = t.Namespace.String,
                    Name = t.Name.String,
                    IsPublic = t.IsPublic
                })
                .ToList();

            McpLogger.Debug($"Search found {results.Count} matching types");

            return CreatePaginatedResponse(results, offset, pageSize);
        }

        #endregion

        #region Section C: Code Generation Commands

        CallToolResult GenerateBepInExPlugin(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("plugin_name", out var pluginNameObj))
                throw new ArgumentException("plugin_name is required");
            if (!arguments.TryGetValue("plugin_guid", out var pluginGuidObj))
                throw new ArgumentException("plugin_guid is required");
            if (!arguments.TryGetValue("target_assembly", out var targetAssemblyObj))
                throw new ArgumentException("target_assembly is required");

            var pluginName = pluginNameObj.ToString() ?? string.Empty;
            var pluginGuid = pluginGuidObj.ToString() ?? string.Empty;
            var targetAssembly = targetAssemblyObj.ToString() ?? string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("using BepInEx;");
            sb.AppendLine("using BepInEx.Logging;");
            sb.AppendLine("using HarmonyLib;");
            sb.AppendLine("using System;");
            sb.AppendLine();
            sb.AppendLine($"namespace {pluginName}");
            sb.AppendLine("{");
            sb.AppendLine($"    [BepInPlugin(\"{pluginGuid}\", \"{pluginName}\", \"1.0.0\")]");
            sb.AppendLine($"    public class {pluginName}Plugin : BaseUnityPlugin");
            sb.AppendLine("    {");
            sb.AppendLine("        private static ManualLogSource Log;");
            sb.AppendLine("        private Harmony harmony;");
            sb.AppendLine();
            sb.AppendLine("        private void Awake()");
            sb.AppendLine("        {");
            sb.AppendLine("            Log = Logger;");
            sb.AppendLine($"            Log.LogInfo(\"{pluginName} is loading...\");");
            sb.AppendLine();
            sb.AppendLine($"            harmony = new Harmony(\"{pluginGuid}\");");
            sb.AppendLine("            harmony.PatchAll();");
            sb.AppendLine();
            sb.AppendLine($"            Log.LogInfo(\"{pluginName} loaded successfully!\");");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private void OnDestroy()");
            sb.AppendLine("        {");
            sb.AppendLine("            harmony?.UnpatchSelf();");
            sb.AppendLine("        }");
            sb.AppendLine("    }");

            // Add hooks if provided
            if (arguments.TryGetValue("hooks", out var hooksObj) && hooksObj is JsonElement hooksElement)
            {
                try
                {
                    var hooks = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(hooksElement.ToString());
                    if (hooks != null && hooks.Count > 0)
                    {
                        sb.AppendLine();
                        foreach (var hook in hooks)
                        {
                            if (hook.TryGetValue("type_name", out var typeName) &&
                                hook.TryGetValue("method_name", out var methodName))
                            {
                                sb.AppendLine();
                                sb.AppendLine($"    [HarmonyPatch(typeof({typeName}), \"{methodName}\")]");
                                sb.AppendLine($"    class {typeName.Replace(".", "_")}_{methodName}_Patch");
                                sb.AppendLine("    {");
                                sb.AppendLine("        static void Prefix()");
                                sb.AppendLine("        {");
                                sb.AppendLine($"            // Add your code before {methodName} executes");
                                sb.AppendLine("        }");
                                sb.AppendLine();
                                sb.AppendLine("        static void Postfix()");
                                sb.AppendLine("        {");
                                sb.AppendLine($"            // Add your code after {methodName} executes");
                                sb.AppendLine("        }");
                                sb.AppendLine("    }");
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore hook parsing errors
                }
            }

            sb.AppendLine("}");

            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = sb.ToString() }
                }
            };
        }

        #endregion

        CallToolResult GetTypeFields(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");
            if (!arguments.TryGetValue("pattern", out var patternObj))
                throw new ArgumentException("pattern is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeFullName = typeNameObj.ToString() ?? string.Empty;
            var pattern = patternObj.ToString() ?? string.Empty;

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeFullName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeFullName}");

            // Convert wildcard pattern to regex
            var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            var regex = new System.Text.RegularExpressions.Regex(regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var allMatchingFields = type.Fields
                .Where(f => regex.IsMatch(f.Name.String))
                .Select(f => new
                {
                    Name = f.Name.String,
                    Type = f.FieldType.FullName,
                    IsPublic = f.IsPublic,
                    IsStatic = f.IsStatic,
                    IsLiteral = f.IsLiteral,
                    IsReadOnly = f.IsInitOnly,
                    Attributes = f.Attributes.ToString()
                })
                .ToList();

            var fieldsToReturn = allMatchingFields.Skip(offset).Take(pageSize).ToList();
            var hasMore = offset + pageSize < allMatchingFields.Count;

            var response = new Dictionary<string, object>
            {
                ["Type"] = typeFullName,
                ["Pattern"] = pattern,
                ["MatchCount"] = allMatchingFields.Count,
                ["ReturnedCount"] = fieldsToReturn.Count,
                ["Fields"] = fieldsToReturn
            };

            if (hasMore)
            {
                response["nextCursor"] = EncodeCursor(offset + pageSize, pageSize);
            }

            var result = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        CallToolResult GetTypeProperty(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");
            if (!arguments.TryGetValue("property_name", out var propertyNameObj))
                throw new ArgumentException("property_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeFullName = typeNameObj.ToString() ?? string.Empty;
            var propertyName = propertyNameObj.ToString() ?? string.Empty;

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeFullName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeFullName}");

            var property = type.Properties.FirstOrDefault(p => p.Name.String.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
            if (property == null)
                throw new ArgumentException($"Property not found: {propertyName}");

            var propertyInfo = new
            {
                Name = property.Name.String,
                Type = property.PropertySig?.RetType?.FullName ?? "unknown",
                CanRead = property.GetMethod != null,
                CanWrite = property.SetMethod != null,
                GetMethod = property.GetMethod != null ? new
                {
                    Name = property.GetMethod.Name.String,
                    IsPublic = property.GetMethod.IsPublic,
                    IsStatic = property.GetMethod.IsStatic
                } : null,
                SetMethod = property.SetMethod != null ? new
                {
                    Name = property.SetMethod.Name.String,
                    IsPublic = property.SetMethod.IsPublic,
                    IsStatic = property.SetMethod.IsStatic
                } : null,
                Attributes = property.Attributes.ToString(),
                CustomAttributes = property.CustomAttributes.Select(a => a.AttributeType.FullName).ToList()
            };

            var result = JsonSerializer.Serialize(propertyInfo, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        #region Section F: Path Finding Commands

        CallToolResult FindPathToType(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("from_type", out var fromTypeObj))
                throw new ArgumentException("from_type is required");
            if (!arguments.TryGetValue("to_type", out var toTypeObj))
                throw new ArgumentException("to_type is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var fromTypeName = fromTypeObj.ToString() ?? string.Empty;
            var toTypeName = toTypeObj.ToString() ?? string.Empty;

            int maxDepth = 5;
            if (arguments.TryGetValue("max_depth", out var maxDepthObj))
            {
                if (maxDepthObj is JsonElement elem && elem.TryGetInt32(out var depth))
                    maxDepth = depth;
            }

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var fromType = FindTypeInAssembly(assembly, fromTypeName);
            if (fromType == null)
                throw new ArgumentException($"From type not found: {fromTypeName}");

            // Find all types matching the target (support partial names)
            var toTypeLower = toTypeName.ToLowerInvariant();
            var targetTypes = assembly.Modules
                .SelectMany(m => m.Types)
                .Where(t => t.FullName.ToLowerInvariant().Contains(toTypeLower) ||
                            t.Name.String.ToLowerInvariant().Contains(toTypeLower))
                .ToList();

            if (targetTypes.Count == 0)
                throw new ArgumentException($"Target type not found: {toTypeName}");

            // BFS to find paths
            var paths = new List<object>();
            foreach (var targetType in targetTypes)
            {
                var path = FindPathBFS(fromType, targetType, maxDepth);
                if (path != null)
                    paths.Add(path);
            }

            if (paths.Count == 0)
            {
                return new CallToolResult
                {
                    Content = new List<ToolContent> {
                        new ToolContent { Text = $"No path found from {fromTypeName} to {toTypeName} within depth {maxDepth}" }
                    }
                };
            }

            var result = JsonSerializer.Serialize(new
            {
                FromType = fromTypeName,
                ToType = toTypeName,
                PathsFound = paths.Count,
                Paths = paths
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        object? FindPathBFS(TypeDef fromType, TypeDef toType, int maxDepth)
        {
            var queue = new Queue<(TypeDef type, List<string> path)>();
            var visited = new HashSet<string>();

            queue.Enqueue((fromType, new List<string> { fromType.Name.String }));
            visited.Add(fromType.FullName);

            while (queue.Count > 0)
            {
                var (currentType, currentPath) = queue.Dequeue();

                if (currentPath.Count > maxDepth + 1)
                    continue;

                // Check if we reached the target
                if (currentType.FullName == toType.FullName)
                {
                    return new
                    {
                        Path = string.Join(" -> ", currentPath),
                        Depth = currentPath.Count - 1,
                        Steps = currentPath
                    };
                }

                // Explore properties
                foreach (var prop in currentType.Properties)
                {
                    var propType = prop.PropertySig?.RetType?.ToTypeDefOrRef()?.ResolveTypeDef();
                    if (propType != null && !visited.Contains(propType.FullName))
                    {
                        visited.Add(propType.FullName);
                        var newPath = new List<string>(currentPath) { prop.Name.String };
                        queue.Enqueue((propType, newPath));
                    }
                }

                // Explore fields
                foreach (var field in currentType.Fields)
                {
                    var fieldType = field.FieldType?.ToTypeDefOrRef()?.ResolveTypeDef();
                    if (fieldType != null && !visited.Contains(fieldType.FullName))
                    {
                        visited.Add(fieldType.FullName);
                        var newPath = new List<string>(currentPath) { field.Name.String };
                        queue.Enqueue((fieldType, newPath));
                    }
                }
            }

            return null;
        }

        #endregion

        #region Section G: Utility Helpers

        AssemblyDef? FindAssemblyByName(string name)
        {
            return documentTreeView.GetAllModuleNodes()
                .Select(m => m.Document?.AssemblyDef)
                .FirstOrDefault(a => a != null && a.Name.String.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
 
        TypeDef? FindTypeInAssembly(AssemblyDef assembly, string fullName)
        {
            return assembly.Modules
                .SelectMany(m => m.Types)
                .FirstOrDefault(t => t.FullName.Equals(fullName, StringComparison.Ordinal));
        }
 
        /// <summary>
        /// Invoke a method by reflection on a lazily-imported tool instance (assemblyTools/typeTools).
        /// Uses reflection to avoid requiring the C# dynamic binder on net48 targets.
        /// </summary>
        CallToolResult InvokeLazy<T>(Lazy<T> lazy, string methodName, Dictionary<string, object>? arguments) where T : class
        {
            if (lazy == null)
                throw new ArgumentNullException(nameof(lazy));
            try
            {
                // Force creation and get the instance
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

                var res = mi.Invoke(instance, new object?[] { arguments });
                if (res is CallToolResult ctr)
                    return ctr;

                var text = JsonSerializer.Serialize(res, new JsonSerializerOptions { WriteIndented = true });
                return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = text } } };
            }
            catch (TargetInvocationException tie)
            {
                McpLogger.Exception(tie, $"InvokeLazy target invocation failed for {methodName}");
                return new CallToolResult
                {
                    Content = new List<ToolContent> { new ToolContent { Text = $"InvokeLazy invocation error: {tie.InnerException?.Message ?? tie.Message}" } },
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
 
        /// <summary>
        /// Helper to dynamically invoke interop tool implementations (avoids compile-time dependency).
        /// Looks for type 'dnSpy.MCP.Server.McpInteropTools' in loaded assemblies and calls the specified method.
        /// </summary>
        CallToolResult CallInterop(string methodName, Dictionary<string, object>? arguments)
        {
            try
            {
                var interopType = Type.GetType("dnSpy.MCP.Server.McpInteropTools");
                if (interopType == null)
                {
                    return new CallToolResult
                    {
                        Content = new List<ToolContent> { new ToolContent { Text = "Interop implementation not found at runtime" } },
                        IsError = true
                    };
                }
 
                // Find a constructor that accepts IDocumentTreeView or parameterless
                object? instance = null;
                var ctor = interopType.GetConstructors()
                    .FirstOrDefault(c =>
                    {
                        var ps = c.GetParameters();
                        return ps.Length == 1 && ps[0].ParameterType == typeof(IDocumentTreeView);
                    });
 
                if (ctor != null)
                {
                    instance = ctor.Invoke(new object[] { documentTreeView });
                }
                else
                {
                    // Try parameterless
                    ctor = interopType.GetConstructor(Type.EmptyTypes);
                    if (ctor != null)
                        instance = ctor.Invoke(null);
                }
 
                if (instance == null)
                {
                    return new CallToolResult
                    {
                        Content = new List<ToolContent> { new ToolContent { Text = "Failed to construct interop tools instance" } },
                        IsError = true
                    };
                }
 
                var mi = interopType.GetMethod(methodName);
                if (mi == null)
                {
                    return new CallToolResult
                    {
                        Content = new List<ToolContent> { new ToolContent { Text = $"Interop method not found: {methodName}" } },
                        IsError = true
                    };
                }
 
                var res = mi.Invoke(instance, new object?[] { arguments });
                if (res is CallToolResult ctr)
                    return ctr;
 
                // If method returned an object serializable to JSON, wrap it
                var text = JsonSerializer.Serialize(res, new JsonSerializerOptions { WriteIndented = true });
                return new CallToolResult { Content = new List<ToolContent> { new ToolContent { Text = text } } };
            }
            catch (Exception ex)
            {
                McpLogger.Exception(ex, "CallInterop failed");
                return new CallToolResult
                {
                    Content = new List<ToolContent> { new ToolContent { Text = $"CallInterop error: {ex.Message}" } },
                    IsError = true
                };
            }
        }

        /// <summary>
        /// Encodes pagination state into an opaque cursor string.
        /// </summary>
        string EncodeCursor(int offset, int pageSize)
        {
            var cursorData = new { offset, pageSize };
            var json = JsonSerializer.Serialize(cursorData);
            var bytes = Encoding.UTF8.GetBytes(json);
            return Convert.ToBase64String(bytes);
        }

        /// <summary>
        /// Decodes a cursor string into pagination state.
        /// Returns (offset, pageSize) tuple. Returns (0, 10) if cursor is null/empty.
        /// Throws ArgumentException for invalid cursors (per MCP protocol, error code -32602).
        /// </summary>
        (int offset, int pageSize) DecodeCursor(string? cursor)
        {
            const int defaultPageSize = 10;

            // Null or empty cursor is valid - it's the first request
            if (string.IsNullOrEmpty(cursor))
                return (0, defaultPageSize);

            try
            {
                var bytes = Convert.FromBase64String(cursor);
                var json = Encoding.UTF8.GetString(bytes);
                var cursorData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

                if (cursorData == null)
                    throw new ArgumentException("Invalid cursor: cursor data is null");

                if (!cursorData.TryGetValue("offset", out var offsetObj) || !(offsetObj is JsonElement offsetElem) || !offsetElem.TryGetInt32(out var offset))
                    throw new ArgumentException("Invalid cursor: missing or invalid 'offset' field");

                if (!cursorData.TryGetValue("pageSize", out var pageSizeObj) || !(pageSizeObj is JsonElement pageSizeElem) || !pageSizeElem.TryGetInt32(out var pageSize))
                    throw new ArgumentException("Invalid cursor: missing or invalid 'pageSize' field");

                if (offset < 0)
                    throw new ArgumentException("Invalid cursor: offset cannot be negative");

                if (pageSize <= 0)
                    throw new ArgumentException("Invalid cursor: pageSize must be positive");

                return (offset, pageSize);
            }
            catch (FormatException)
            {
                throw new ArgumentException("Invalid cursor: not a valid base64 string");
            }
            catch (JsonException ex)
            {
                throw new ArgumentException($"Invalid cursor: invalid JSON format - {ex.Message}");
            }
            catch (ArgumentException)
            {
                // Re-throw ArgumentExceptions we created above
                throw;
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Invalid cursor: {ex.Message}");
            }
        }

        /// <summary>
        /// Creates a paginated response with optional nextCursor.
        /// </summary>
        CallToolResult CreatePaginatedResponse<T>(List<T> allItems, int offset, int pageSize)
        {
            var itemsToReturn = allItems.Skip(offset).Take(pageSize).ToList();
            var hasMore = offset + pageSize < allItems.Count;

            var response = new Dictionary<string, object>
            {
                ["items"] = itemsToReturn,
                ["total_count"] = allItems.Count,
                ["returned_count"] = itemsToReturn.Count
            };

            if (hasMore)
            {
                response["nextCursor"] = EncodeCursor(offset + pageSize, pageSize);
            }

            var result = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        CallToolResult ListTools()
        {
            var tools = GetAvailableTools();
            var toolList = tools.Select(t => new
            {
                t.Name,
                t.Description,
                InputSchema = t.InputSchema
            }).ToList();

            var result = JsonSerializer.Serialize(new
            {
                Tools = toolList,
                TotalCount = toolList.Count
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        #endregion

        #region Section D: Type Analysis Commands

        CallToolResult GetMethodSignature(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");
            if (!arguments.TryGetValue("method_name", out var methodNameObj))
                throw new ArgumentException("method_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeName = typeNameObj.ToString() ?? string.Empty;
            var methodName = methodNameObj.ToString() ?? string.Empty;

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeName}");

            var method = type.Methods.FirstOrDefault(m => m.Name.String == methodName);
            if (method == null)
                throw new ArgumentException($"Method not found: {methodName}");

            var parameters = method.Parameters.Select(p => new
            {
                Name = p.Name,
                Type = p.Type.FullName,
                IsByRef = p.Type.IsByRef
            }).ToList();

            var methodInfo = new
            {
                Name = method.Name.String,
                ReturnType = method.ReturnType.FullName,
                IsPublic = method.IsPublic,
                IsPrivate = method.IsPrivate,
                IsStatic = method.IsStatic,
                IsVirtual = method.IsVirtual,
                IsAbstract = method.IsAbstract,
                Parameters = parameters,
                GenericParameters = method.GenericParameters.Select(gp => gp.Name.String).ToList()
            };

            var result = JsonSerializer.Serialize(methodInfo, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        CallToolResult ListMethodsInType(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeName = typeNameObj.ToString() ?? string.Empty;
            string? visibility = null;
            if (arguments.TryGetValue("visibility", out var visObj))
                visibility = visObj.ToString()?.ToLower();

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeName}");

            var methods = type.Methods
                .Where(m =>
                {
                    if (visibility == null) return true;
                    return visibility switch
                    {
                        "public" => m.IsPublic,
                        "private" => m.IsPrivate,
                        "protected" => m.IsFamily,
                        "internal" => m.IsAssembly,
                        _ => false
                    };
                })
                .Select(m => new
                {
                    Name = m.Name.String,
                    ReturnType = m.ReturnType.FullName,
                    IsPublic = m.IsPublic,
                    IsStatic = m.IsStatic,
                    IsVirtual = m.IsVirtual,
                    ParameterCount = m.Parameters.Count
                })
                .ToList();

            return CreatePaginatedResponse(methods, offset, pageSize);
        }

        CallToolResult ListPropertiesInType(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeName = typeNameObj.ToString() ?? string.Empty;

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeName}");

            var properties = type.Properties
                .Select(p => new
                {
                    Name = p.Name.String,
                    PropertyType = p.PropertySig?.RetType?.FullName ?? "Unknown",
                    CanRead = p.GetMethod != null,
                    CanWrite = p.SetMethod != null,
                    IsPublic = (p.GetMethod?.IsPublic ?? false) || (p.SetMethod?.IsPublic ?? false),
                    IsStatic = (p.GetMethod?.IsStatic ?? false) || (p.SetMethod?.IsStatic ?? false)
                })
                .ToList();

            return CreatePaginatedResponse(properties, offset, pageSize);
        }

        CallToolResult FindTypeReferences(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeName = typeNameObj.ToString() ?? string.Empty;

            string? cursor = null;
            if (arguments.TryGetValue("cursor", out var cursorObj))
                cursor = cursorObj.ToString();

            var (offset, pageSize) = DecodeCursor(cursor);

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeName}");

            var references = new List<object>();

            // Find field references
            foreach (var t in assembly.Modules.SelectMany(m => m.Types))
            {
                foreach (var field in t.Fields.Where(f => f.FieldType.FullName == typeName))
                {
                    references.Add(new
                    {
                        Kind = "Field",
                        InType = t.FullName,
                        Member = field.Name.String,
                        MemberType = field.FieldType.FullName
                    });
                }
            }

            // Find method parameter/return type references
            foreach (var t in assembly.Modules.SelectMany(m => m.Types))
            {
                foreach (var method in t.Methods)
                {
                    if (method.ReturnType.FullName == typeName)
                    {
                        references.Add(new
                        {
                            Kind = "ReturnType",
                            InType = t.FullName,
                            Member = method.Name.String,
                            MemberType = method.ReturnType.FullName
                        });
                    }

                    foreach (var param in method.Parameters.Where(p => p.Type.FullName == typeName))
                    {
                        references.Add(new
                        {
                            Kind = "Parameter",
                            InType = t.FullName,
                            Member = method.Name.String,
                            ParameterName = param.Name
                        });
                    }
                }
            }

            return CreatePaginatedResponse(references, offset, pageSize);
        }

        #endregion

        #region Section E: Inheritance & Hook Commands

        CallToolResult AnalyzeTypeInheritance(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeName = typeNameObj.ToString() ?? string.Empty;

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeName}");

            var baseClasses = new List<string>();
            var interfaces = new List<string>();

            // Get base class chain
            var current = type.BaseType;
            while (current != null && current.FullName != "System.Object")
            {
                baseClasses.Add(current.FullName);
                if (current is TypeDef baseDef)
                    current = baseDef.BaseType;
                else
                    break;
            }

            // Get interfaces
            interfaces.AddRange(type.Interfaces.Select(i => i.Interface?.FullName ?? "Unknown"));

            var inheritance = new
            {
                Type = typeName,
                BaseClasses = baseClasses,
                Interfaces = interfaces,
                IsAbstract = type.IsAbstract,
                IsSealed = type.IsSealed,
                IsInterface = type.IsInterface
            };

            var result = JsonSerializer.Serialize(inheritance, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        CallToolResult GetConstantValues(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeName = typeNameObj.ToString() ?? string.Empty;

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeName}");

            var constants = new List<object>();

            // Extract literal fields (constants)
            foreach (var field in type.Fields.Where(f => f.IsLiteral))
            {
                constants.Add(new
                {
                    Name = field.Name.String,
                    Type = field.FieldType.FullName,
                    Value = field.Constant?.Value?.ToString() ?? "null"
                });
            }

            // If type is an enum, list enum values
            if (type.IsEnum)
            {
                foreach (var field in type.Fields.Where(f => !f.IsSpecialName))
                {
                    var value = field.Constant?.Value?.ToString() ?? "0";
                    constants.Add(new
                    {
                        Name = field.Name.String,
                        Type = "EnumValue",
                        Value = value
                    });
                }
            }

            var result = JsonSerializer.Serialize(new
            {
                Type = typeName,
                IsEnum = type.IsEnum,
                Constants = constants
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        CallToolResult GenerateHarmonyPatch(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");
            if (!arguments.TryGetValue("method_name", out var methodNameObj))
                throw new ArgumentException("method_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeName = typeNameObj.ToString() ?? string.Empty;
            var methodName = methodNameObj.ToString() ?? string.Empty;

            var patchTypes = new List<string>();
            if (arguments.TryGetValue("patch_types", out var patchTypesObj))
            {
                if (patchTypesObj is JsonElement elem && elem.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in elem.EnumerateArray())
                    {
                        patchTypes.Add(item.GetString() ?? "prefix");
                    }
                }
            }

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeName}");

            var method = type.Methods.FirstOrDefault(m => m.Name.String == methodName);
            if (method == null)
                throw new ArgumentException($"Method not found: {methodName}");

            var patches = new List<object>();

            if (patchTypes.Count == 0 || patchTypes.Contains("prefix"))
            {
                patches.Add(new
                {
                    Type = "Prefix",
                    Template = $@"[HarmonyPrefix]
public static bool {methodName}Prefix()
{{
    // Add your prefix logic here
    // Return false to skip the original method
    return true;
}}"
                });
            }

            if (patchTypes.Count == 0 || patchTypes.Contains("postfix"))
            {
                patches.Add(new
                {
                    Type = "Postfix",
                    Template = $@"[HarmonyPostfix]
public static void {methodName}Postfix()
{{
    // Add your postfix logic here
    // This runs after the original method
}}"
                });
            }

            if (patchTypes.Count == 0 || patchTypes.Contains("transpiler"))
            {
                patches.Add(new
                {
                    Type = "Transpiler",
                    Template = $@"[HarmonyTranspiler]
public static IEnumerable<CodeInstruction> {methodName}Transpiler(IEnumerable<CodeInstruction> instructions)
{{
    // Add IL modification logic here
    var codes = new List<CodeInstruction>(instructions);
    // Modify codes as needed
    return codes;
}}"
                });
            }

            var harmonyInfo = new
            {
                TargetType = typeName,
                TargetMethod = methodName,
                Patches = patches,
                RequiredUsings = new[] { "HarmonyLib", "System.Collections.Generic", "System.Reflection.Emit" }
            };

            var result = JsonSerializer.Serialize(harmonyInfo, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        CallToolResult FindVirtualMethodOverrides(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeName = typeNameObj.ToString() ?? string.Empty;

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeName}");

            var virtualMethods = new List<object>();

            // Get virtual methods from this type
            foreach (var method in type.Methods.Where(m => m.IsVirtual && !m.IsAbstract))
            {
                virtualMethods.Add(new
                {
                    Method = method.Name.String,
                    ReturnType = method.ReturnType.FullName,
                    IsAbstract = method.IsAbstract,
                    Parameters = method.Parameters.Select(p => new { p.Name, Type = p.Type.FullName }).ToList()
                });
            }

            // Find derived types that override these methods
            var allTypes = assembly.Modules.SelectMany(m => m.Types).ToList();
            var derivedTypes = allTypes.Where(t =>
            {
                var current = t.BaseType;
                while (current != null)
                {
                    if (current.FullName == typeName)
                        return true;
                    if (current is TypeDef baseDef)
                        current = baseDef.BaseType;
                    else
                        break;
                }
                return false;
            }).ToList();

            var overrides = new List<object>();
            foreach (var derived in derivedTypes)
            {
                foreach (var method in derived.Methods.Where(m => m.IsVirtual))
                {
                    overrides.Add(new
                    {
                        DerivedType = derived.FullName,
                        Method = method.Name.String,
                        ReturnType = method.ReturnType.FullName
                    });
                }
            }

            var result = JsonSerializer.Serialize(new
            {
                Type = typeName,
                VirtualMethods = virtualMethods,
                Overrides = overrides,
                TotalOverrides = overrides.Count
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        CallToolResult SuggestHookPoints(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeName = typeNameObj.ToString() ?? string.Empty;

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeName}");

            var hookPoints = new List<object>();

            // Suggest virtual methods as hook points
            foreach (var method in type.Methods.Where(m => m.IsVirtual && !m.IsAbstract && m.IsPublic))
            {
                hookPoints.Add(new
                {
                    Type = "VirtualMethod",
                    Name = method.Name.String,
                    ReturnType = method.ReturnType.FullName,
                    Priority = "High",
                    Reason = "Virtual methods are easily overridable and ideal for hooks"
                });
            }

            // Suggest events as hook points
            foreach (var @event in type.Events)
            {
                hookPoints.Add(new
                {
                    Type = "Event",
                    Name = @event.Name.String,
                    EventType = @event.EventType?.FullName ?? "Unknown",
                    Priority = "High",
                    Reason = "Events can be hooked to trigger custom logic"
                });
            }

            // Suggest constructors
            foreach (var ctor in type.Methods.Where(m => m.IsConstructor && m.IsPublic))
            {
                hookPoints.Add(new
                {
                    Type = "Constructor",
                    Name = ".ctor",
                    Priority = "Medium",
                    Reason = "Constructors are entry points for type initialization"
                });
            }

            // Suggest public methods with state-changing characteristics
            var hookMethods = type.Methods.Where(m =>
                m.IsPublic &&
                !m.IsStatic &&
                (m.Name.String.StartsWith("Set") ||
                 m.Name.String.StartsWith("Update") ||
                 m.Name.String.StartsWith("Initialize") ||
                 m.Name.String.StartsWith("Load"))
            ).ToList();

            foreach (var method in hookMethods)
            {
                hookPoints.Add(new
                {
                    Type = "StateChangeMethod",
                    Name = method.Name.String,
                    ReturnType = method.ReturnType.FullName,
                    Priority = "Medium",
                    Reason = "Methods that modify state are good hook points"
                });
            }

            var result = JsonSerializer.Serialize(new
            {
                Type = typeName,
                HookPoints = hookPoints,
                TotalSuggestions = hookPoints.Count
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> {
                    new ToolContent { Text = result }
                }
            };
        }

        #endregion

        #region Section H: Phase 4 IL Analysis Commands

        /// <summary>
        /// Helper: Get all types including nested types from a module.
        /// </summary>
        IEnumerable<TypeDef> GetAllTypesRecursive(ModuleDef module)
        {
            foreach (var type in module.Types)
            {
                yield return type;
                // Also yield nested types
                foreach (var nested in GetNestedTypesRecursive(type))
                    yield return nested;
            }
        }

        /// <summary>
        /// Helper: Get all nested types recursively.
        /// </summary>
        IEnumerable<TypeDef> GetNestedTypesRecursive(TypeDef type)
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
        IEnumerable<MethodDef> GetAllMethodDefinitions()
        {
            return documentTreeView.GetAllModuleNodes()
                .Select(m => m.Document?.AssemblyDef)
                .Where(a => a != null)
                .SelectMany(a => a!.Modules)
                .SelectMany(m => GetAllTypesRecursive(m))
                .SelectMany(t => t.Methods)
                .Where(m => m.Body != null);
        }

        /// <summary>
        /// Helper: Find all methods that call a specific target method.
        /// Analyzes IL instructions for CALL and CALLVIRT opcodes.
        /// </summary>
        List<(string MethodName, string TypeName, string AssemblyName)> FindMethodCallersInIL(MethodDef targetMethod)
        {
            var callers = new List<(string, string, string)>();

            foreach (var method in GetAllMethodDefinitions())
            {
                if (method.Body?.Instructions == null)
                    continue;

                foreach (var instr in method.Body.Instructions)
                {
                    // Check for CALL and CALLVIRT instructions
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
        List<(string MethodName, string TypeName, string AssemblyName)> FindFieldReadersInIL(FieldDef targetField)
        {
            var readers = new List<(string, string, string)>();

            foreach (var method in GetAllMethodDefinitions())
            {
                if (method.Body?.Instructions == null)
                    continue;

                foreach (var instr in method.Body.Instructions)
                {
                    // Check for LDFLD and LDSFLD instructions
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
        List<(string MethodName, string TypeName, string AssemblyName)> FindFieldWritersInIL(FieldDef targetField)
        {
            var writers = new List<(string, string, string)>();

            foreach (var method in GetAllMethodDefinitions())
            {
                if (method.Body?.Instructions == null)
                    continue;

                foreach (var instr in method.Body.Instructions)
                {
                    // Check for STFLD and STSFLD instructions
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
        List<(string Context, string UsageLocus, string AssemblyName)> BuildTypeReferenceGraph(TypeDef targetType)
        {
            var usages = new List<(string, string, string)>();

            foreach (var assembly in documentTreeView.GetAllModuleNodes()
                .Select(m => m.Document?.AssemblyDef)
                .Where(a => a != null))
            {
                var assemblyName = assembly!.Name.String;

                foreach (var module in assembly.Modules)
                {
                    foreach (var type in GetAllTypesRecursive(module))
                    {
                        // Check base type
                        if (type.BaseType?.FullName == targetType.FullName && type.FullName != targetType.FullName)
                            usages.Add(("BaseType", type.FullName, assemblyName));

                        // Check interfaces
                        foreach (var iface in type.Interfaces)
                        {
                            if (iface.Interface?.FullName == targetType.FullName)
                                usages.Add(("Interface", type.FullName, assemblyName));
                        }

                        // Check field types
                        foreach (var field in type.Fields)
                        {
                            if (field.FieldType.FullName == targetType.FullName)
                                usages.Add(("FieldType", $"{type.FullName}.{field.Name}", assemblyName));
                        }

                        // Check method parameter and return types
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

        CallToolResult FindWhoUsesType(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeName = typeNameObj.ToString() ?? string.Empty;

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var targetType = FindTypeInAssembly(assembly, typeName);
            if (targetType == null)
                throw new ArgumentException($"Type not found: {typeName}");

            var usages = BuildTypeReferenceGraph(targetType);

            // Group usages by context for better readability
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

        CallToolResult FindWhoCallsMethod(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");
            if (!arguments.TryGetValue("method_name", out var methodNameObj))
                throw new ArgumentException("method_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeName = typeNameObj.ToString() ?? string.Empty;
            var methodName = methodNameObj.ToString() ?? string.Empty;

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeName}");

            var targetMethod = type.Methods.FirstOrDefault(m => m.Name.String.Equals(methodName, StringComparison.Ordinal));
            if (targetMethod == null)
                throw new ArgumentException($"Method not found: {methodName}");

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

        CallToolResult FindWhoReadsField(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");
            if (!arguments.TryGetValue("field_name", out var fieldNameObj))
                throw new ArgumentException("field_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeName = typeNameObj.ToString() ?? string.Empty;
            var fieldName = fieldNameObj.ToString() ?? string.Empty;

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeName}");

            var targetField = type.Fields.FirstOrDefault(f => f.Name.String.Equals(fieldName, StringComparison.Ordinal));
            if (targetField == null)
                throw new ArgumentException($"Field not found: {fieldName}");

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

        CallToolResult FindWhoWritesField(Dictionary<string, object>? arguments)
        {
            if (arguments == null)
                throw new ArgumentException("Arguments required");
            if (!arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");
            if (!arguments.TryGetValue("field_name", out var fieldNameObj))
                throw new ArgumentException("field_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeName = typeNameObj.ToString() ?? string.Empty;
            var fieldName = fieldNameObj.ToString() ?? string.Empty;

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeName}");

            var targetField = type.Fields.FirstOrDefault(f => f.Name.String.Equals(fieldName, StringComparison.Ordinal));
            if (targetField == null)
                throw new ArgumentException($"Field not found: {fieldName}");

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

        #endregion

        #region Section I: Phase 5+ Stub Commands

        CallToolResult FindDependencyChain(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("source_type_full_name", out var sourceTypeObj))
                throw new ArgumentException("source_type_full_name is required");
            if (!arguments.TryGetValue("target_type_full_name", out var targetTypeObj))
                throw new ArgumentException("target_type_full_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var sourceTypeName = sourceTypeObj.ToString() ?? string.Empty;
            var targetTypeName = targetTypeObj.ToString() ?? string.Empty;

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var sourceType = FindTypeInAssembly(assembly, sourceTypeName);
            if (sourceType == null)
                throw new ArgumentException($"Source type not found: {sourceTypeName}");

            var targetType = FindTypeInAssembly(assembly, targetTypeName);
            if (targetType == null)
                throw new ArgumentException($"Target type not found: {targetTypeName}");

            var maxPathLength = 10;
            if (arguments.TryGetValue("max_path_length", out var maxPathObj) && int.TryParse(maxPathObj.ToString(), out var pathLen))
                maxPathLength = pathLen;

            var analysisHelpers = new CodeAnalysisHelpers(documentTreeView, usageFindingCommandTools.Value);
            var paths = analysisHelpers.FindDependencyPaths(sourceType, targetType, maxPathLength);

            var result = JsonSerializer.Serialize(new
            {
                SourceType = sourceTypeName,
                TargetType = targetTypeName,
                PathCount = paths.Count,
                Paths = paths.Select((p, idx) => new
                {
                    PathNumber = idx + 1,
                    PathLength = p.Count,
                    Sequence = p
                }).ToList()
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = result } }
            };
        }

        CallToolResult AnalyzeCallGraph(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");
            if (!arguments.TryGetValue("method_name", out var methodNameObj))
                throw new ArgumentException("method_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeName = typeNameObj.ToString() ?? string.Empty;
            var methodName = methodNameObj.ToString() ?? string.Empty;

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeName}");

            var targetMethod = type.Methods.FirstOrDefault(m => m.Name.String.Equals(methodName, StringComparison.Ordinal));
            if (targetMethod == null)
                throw new ArgumentException($"Method not found: {methodName}");

            var maxDepth = 5;
            if (arguments.TryGetValue("max_depth", out var maxDepthObj) && int.TryParse(maxDepthObj.ToString(), out var depth))
                maxDepth = depth;

            var callGraphHelpers = new CodeAnalysisHelpers(documentTreeView, usageFindingCommandTools.Value);
            var callGraph = callGraphHelpers.BuildCallGraph(targetMethod, maxDepth);

            var result = JsonSerializer.Serialize(callGraph, new JsonSerializerOptions { WriteIndented = true });
            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = result } }
            };
        }

        CallToolResult FindExposedInterfaces(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var exposedInternals = new List<(string ExposedType, string ExposedAs, string PublicMember, string MemberKind)>();

            foreach (var module in assembly.Modules)
            {
                foreach (var type in GetAllTypesRecursive(module))
                {
                    if (!type.IsPublic && !type.IsNestedPublic)
                        continue;

                    // Check methods for non-public return types and parameters
                    foreach (var method in type.Methods.Where(m => m.IsPublic || m.IsFamily))
                    {
                        // Check return type
                        var retTypeDef = method.ReturnType?.ToTypeDefOrRef()?.ResolveTypeDef();
                        if (retTypeDef != null && !retTypeDef.IsPublic && !retTypeDef.IsNestedPublic)
                        {
                            exposedInternals.Add((retTypeDef.FullName, "ReturnType", $"{type.FullName}.{method.Name}", "Method"));
                        }

                        // Check parameters
                        foreach (var param in method.Parameters)
                        {
                            var paramTypeDef = param.Type?.ToTypeDefOrRef()?.ResolveTypeDef();
                            if (paramTypeDef != null && !paramTypeDef.IsPublic && !paramTypeDef.IsNestedPublic)
                            {
                                exposedInternals.Add((paramTypeDef.FullName, "Parameter", $"{type.FullName}.{method.Name}", "Method"));
                            }
                        }
                    }

                    // Check properties
                    foreach (var prop in type.Properties.Where(p => (p.GetMethod?.IsPublic ?? false) || (p.SetMethod?.IsPublic ?? false)))
                    {
                        var propTypeDef = prop.PropertySig?.RetType?.ToTypeDefOrRef()?.ResolveTypeDef();
                        if (propTypeDef != null && !propTypeDef.IsPublic && !propTypeDef.IsNestedPublic)
                        {
                            exposedInternals.Add((propTypeDef.FullName, "PropertyType", $"{type.FullName}.{prop.Name}", "Property"));
                        }
                    }

                    // Check fields
                    foreach (var field in type.Fields.Where(f => f.IsPublic))
                    {
                        var fieldTypeDef = field.FieldType?.ToTypeDefOrRef()?.ResolveTypeDef();
                        if (fieldTypeDef != null && !fieldTypeDef.IsPublic && !fieldTypeDef.IsNestedPublic)
                        {
                            exposedInternals.Add((fieldTypeDef.FullName, "FieldType", $"{type.FullName}.{field.Name}", "Field"));
                        }
                    }
                }
            }

            var result = JsonSerializer.Serialize(new
            {
                AssemblyName = assemblyName,
                ExposedInternalCount = exposedInternals.Count,
                ExposedInternals = exposedInternals
                    .GroupBy(x => x.ExposedType)
                    .Select(g => new
                    {
                        InternalTypeName = g.Key,
                        ExposureCount = g.Count(),
                        Usages = g.Select(x => new
                        {
                            ExposedAs = x.ExposedAs,
                            PublicMember = x.PublicMember,
                            MemberKind = x.MemberKind
                        }).OrderBy(x => x.PublicMember).ToList()
                    })
                    .OrderBy(x => x.InternalTypeName)
                    .ToList()
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = result } }
            };
        }

        CallToolResult TraceDataFlow(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");
            if (!arguments.TryGetValue("type_full_name", out var typeNameObj))
                throw new ArgumentException("type_full_name is required");
            if (!arguments.TryGetValue("method_name", out var methodNameObj))
                throw new ArgumentException("method_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var typeName = typeNameObj.ToString() ?? string.Empty;
            var methodName = methodNameObj.ToString() ?? string.Empty;
            var paramIndex = 0;
            if (arguments.TryGetValue("param_index", out var paramIdxObj) && int.TryParse(paramIdxObj.ToString(), out var idx))
                paramIndex = idx;

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var type = FindTypeInAssembly(assembly, typeName);
            if (type == null)
                throw new ArgumentException($"Type not found: {typeName}");

            var targetMethod = type.Methods.FirstOrDefault(m => m.Name.String.Equals(methodName, StringComparison.Ordinal));
            if (targetMethod == null)
                throw new ArgumentException($"Method not found: {methodName}");

            if (paramIndex < 0 || paramIndex >= targetMethod.Parameters.Count)
                throw new ArgumentException($"Parameter index {paramIndex} out of range (method has {targetMethod.Parameters.Count} parameters)");

            var maxDepth = 5;
            if (arguments.TryGetValue("max_depth", out var maxDepthObj) && int.TryParse(maxDepthObj.ToString(), out var depth))
                maxDepth = depth;

            // Analyze data flow through the method body
            var dataFlowPaths = new List<Dictionary<string, object>>();

            if (targetMethod.Body?.Instructions != null)
            {
                var instrList = targetMethod.Body.Instructions.ToList();
                var targetParam = targetMethod.Parameters[paramIndex];

                // Simple data flow: find where this parameter is used
                var usages = new List<string>();
                for (int i = 0; i < instrList.Count; i++)
                {
                    var instr = instrList[i];
                    // Check if instruction uses the parameter (Ldarg for the parameter index)
                    if ((instr.OpCode.Code == dnlib.DotNet.Emit.Code.Ldarg_0 && paramIndex == 0) ||
                        (instr.OpCode.Code == dnlib.DotNet.Emit.Code.Ldarg_1 && paramIndex == 1) ||
                        (instr.OpCode.Code == dnlib.DotNet.Emit.Code.Ldarg_2 && paramIndex == 2) ||
                        (instr.OpCode.Code == dnlib.DotNet.Emit.Code.Ldarg_3 && paramIndex == 3) ||
                        (instr.OpCode.Code == dnlib.DotNet.Emit.Code.Ldarg && instr.Operand is Parameter p && p.MethodSigIndex == paramIndex))
                    {
                        // Check next instruction to see what's done with it
                        if (i + 1 < instrList.Count)
                        {
                            var nextInstr = instrList[i + 1];
                            usages.Add($"Offset {instr.Offset}: Used in {nextInstr.OpCode.Name} operation");
                        }
                    }
                }

                dataFlowPaths.Add(new Dictionary<string, object>
                {
                    ["parameter"] = targetParam.Name,
                    ["parameter_type"] = targetParam.Type.FullName,
                    ["usage_count"] = usages.Count,
                    ["usages"] = usages
                });
            }

            var result = JsonSerializer.Serialize(new
            {
                TargetMethod = targetMethod.FullName,
                ParameterName = targetMethod.Parameters[paramIndex].Name,
                ParameterType = targetMethod.Parameters[paramIndex].Type.FullName,
                DataFlowPaths = dataFlowPaths,
                AnalysisNote = "Basic data flow analysis showing parameter usage in method body"
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = result } }
            };
        }

        CallToolResult FindDeadCode(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("assembly_name", out var assemblyNameObj))
                throw new ArgumentException("assembly_name is required");

            var assemblyName = assemblyNameObj.ToString() ?? string.Empty;
            var includePrivate = true;
            if (arguments.TryGetValue("include_private", out var includePrivateObj) && bool.TryParse(includePrivateObj.ToString(), out var incPriv))
                includePrivate = incPriv;

            var assembly = FindAssemblyByName(assemblyName);
            if (assembly == null)
                throw new ArgumentException($"Assembly not found: {assemblyName}");

            var analysisHelpers = new CodeAnalysisHelpers(documentTreeView, usageFindingCommandTools.Value);
            var (deadMethods, deadTypes) = analysisHelpers.IdentifyDeadCode(assembly, includePrivate);

            var result = JsonSerializer.Serialize(new
            {
                AssemblyName = assemblyName,
                DeadMethodCount = deadMethods.Count,
                DeadTypeCount = deadTypes.Count,
                TotalDeadCodeItems = deadMethods.Count + deadTypes.Count,
                DeadMethods = deadMethods.OrderBy(x => x).ToList(),
                DeadTypes = deadTypes.OrderBy(x => x).ToList(),
                AnalysisNote = "Dead code detection is simplified and may include methods used via reflection or dynamic dispatch"
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = result } }
            };
        }

        CallToolResult AnalyzeCrossAssemblyDependencies(Dictionary<string, object>? arguments)
        {
            var analysisHelpers = new CodeAnalysisHelpers(documentTreeView, usageFindingCommandTools.Value);
            var assemblyDeps = analysisHelpers.ComputeAssemblyDependencies();

            var result = JsonSerializer.Serialize(new
            {
                TotalAssemblies = assemblyDeps.Count,
                AssemblyDependencies = assemblyDeps.Select(kvp => new
                {
                    AssemblyName = kvp.Key,
                    DependencyCount = kvp.Value.Count,
                    Dependencies = kvp.Value.OrderBy(x => x).ToList()
                }).OrderBy(x => x.AssemblyName).ToList()
            }, new JsonSerializerOptions { WriteIndented = true });

            return new CallToolResult
            {
                Content = new List<ToolContent> { new ToolContent { Text = result } }
            };
        }

        #endregion
    }

    #region Section J: StringBuilderDecompilerOutput Helper Class

    /// <summary>
    /// Helper class that captures decompiler output into a string.
    /// </summary>
    class StringBuilderDecompilerOutput : IDecompilerOutput
    {
        readonly StringBuilder sb = new StringBuilder();

        public void Write(string text, object color) => sb.Append(text);
        public void WriteLine() => sb.AppendLine();

        public override string ToString() => sb.ToString();

        public int Length => sb.Length;
        public int NextPosition => sb.Length;
        public bool UsesCustomData => false;

        public void AddCustomData<TData>(string id, TData data) { }
        public void DecreaseIndent() { }
        public void IncreaseIndent() { }
        public void Write(string text, int index, int length, object color) => sb.Append(text, index, length);
        public void Write(string text, object? reference, DecompilerReferenceFlags flags, object color) => sb.Append(text);
        public void Write(string text, int index, int length, object? reference, DecompilerReferenceFlags flags, object color) => sb.Append(text, index, length);
    }

    #endregion
}

