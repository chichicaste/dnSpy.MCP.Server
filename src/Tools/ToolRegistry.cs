using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using dnSpy.MCP.Server.Application;
using dnSpy.MCP.Server.Communication;
using dnSpy.MCP.Server.Contracts;

namespace dnSpy.MCP.Server.Tools {
	[Export(typeof(ToolRegistry))]
	public sealed class ToolRegistry {
		readonly McpTools tools;
		readonly BepInExResources resources;

		[ImportingConstructor]
		public ToolRegistry(McpTools tools, BepInExResources resources) {
			this.tools = tools;
			this.resources = resources;
		}

		public IReadOnlyList<ToolInfo> ListTools(ToolCatalogFilter? filter = null) => tools.GetAvailableTools(filter);

		public bool ContainsTool(string toolName) => tools.GetAvailableTools().Any(a => string.Equals(a.Name, toolName, StringComparison.Ordinal));

		public CallToolResult ExecuteTool(string toolName, Dictionary<string, object>? arguments) => tools.ExecuteTool(toolName, arguments);

		public IReadOnlyList<ResourceInfo> ListResources() => resources.GetResources();

		public string? ReadResource(string uri) => resources.ReadResource(uri);
	}
}
