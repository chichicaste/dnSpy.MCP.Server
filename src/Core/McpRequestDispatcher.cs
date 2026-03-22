using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using dnSpy.MCP.Server.Application;
using dnSpy.MCP.Server.Contracts;
using dnSpy.MCP.Server.Helper;
using dnSpy.MCP.Server.Tools;

namespace dnSpy.MCP.Server.Core {
	[Export(typeof(McpRequestDispatcher))]
	public sealed class McpRequestDispatcher {
		readonly ToolRegistry toolRegistry;
		readonly McpSessionManager sessionManager;

		[ImportingConstructor]
		public McpRequestDispatcher(ToolRegistry toolRegistry, McpSessionManager sessionManager) {
			this.toolRegistry = toolRegistry;
			this.sessionManager = sessionManager;
		}

		public Task<McpResponse?> DispatchAsync(McpRequestContext context) {
			if (context.IsNotification) {
				HandleNotification(context);
				return Task.FromResult<McpResponse?>(null);
			}

			try {
				var result = DispatchCore(context);
				return Task.FromResult<McpResponse?>(McpProtocolHelpers.CreateSuccess(context.RequestId, result));
			}
			catch (McpProtocolException ex) {
				McpLogger.Warning($"mcp.dispatch transport={context.Transport.ToLogValue()} sessionId={context.SessionId ?? "-"} requestId={context.RequestId ?? "null"} method={context.Method} code={ex.ErrorCode} message=\"{ex.Message}\"");
				return Task.FromResult<McpResponse?>(McpProtocolHelpers.CreateError(context.RequestId, ex.ErrorCode, ex.Message, ex.ErrorData));
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, $"mcp.dispatch transport={context.Transport.ToLogValue()} sessionId={context.SessionId ?? "-"} requestId={context.RequestId ?? "null"} method={context.Method}");
				return Task.FromResult<McpResponse?>(McpProtocolHelpers.CreateError(context.RequestId, -32603, "Internal error", ex.Message));
			}
		}

		void HandleNotification(McpRequestContext context) {
			McpLogger.Debug($"mcp.notification transport={context.Transport.ToLogValue()} sessionId={context.SessionId ?? "-"} method={context.Method}");
			if (string.Equals(context.Method, "notifications/initialized", StringComparison.Ordinal))
				return;
			if (context.Method.StartsWith("notifications/", StringComparison.Ordinal))
				return;
			throw new McpProtocolException(-32601, $"Unknown notification: {context.Method}", HttpStatusCode.BadRequest);
		}

		object DispatchCore(McpRequestContext context) {
			McpLogger.Info($"mcp.request transport={context.Transport.ToLogValue()} sessionId={context.SessionId ?? "-"} requestId={context.RequestId ?? "null"} method={context.Method} remote={context.RemoteEndPoint ?? "-"}");

			switch (context.Method) {
			case "initialize":
				return HandleInitialize(context);
			case "ping":
				return new Dictionary<string, object>();
			case "tools/list":
				return new ListToolsResult { Tools = toolRegistry.ListTools(CreateCatalogFilter(context.Request.Params)).ToList() };
			case "tools/call":
				return HandleCallTool(context);
			case "resources/list":
				return new ListResourcesResult { Resources = toolRegistry.ListResources().ToList() };
			case "resources/read":
				return HandleReadResource(context);
			case "prompts/list":
				return new { prompts = Array.Empty<object>() };
			default:
				throw new McpProtocolException(-32601, $"Method not found: {context.Method}", HttpStatusCode.NotFound);
			}
		}

		object HandleInitialize(McpRequestContext context) {
			var protocolVersion = McpProtocolHelpers.GetString(context.Request.Params, "protocolVersion") ?? McpProtocolHelpers.DefaultProtocolVersion;
			var sessionId = context.SessionId;
			if (!string.IsNullOrWhiteSpace(sessionId))
				sessionManager.MarkInitialized(sessionId!, protocolVersion);

			McpLogger.Info($"mcp.initialize transport={context.Transport.ToLogValue()} sessionId={context.SessionId ?? "-"} protocolVersion={protocolVersion}");

			return new InitializeResult {
				ProtocolVersion = protocolVersion,
				Capabilities = new ServerCapabilities {
					Tools = new Dictionary<string, object> {
						["listChanged"] = false,
					},
					Resources = new Dictionary<string, object> {
						["listChanged"] = false,
					},
				},
				ServerInfo = new ServerInfo {
					Name = "dnSpy MCP Server",
					Version = "1.1.0",
				},
			};
		}

		object HandleCallTool(McpRequestContext context) {
			var toolName = McpProtocolHelpers.GetString(context.Request.Params, "name");
			if (string.IsNullOrWhiteSpace(toolName))
				throw new McpProtocolException(-32602, "Tool call requires a non-empty 'name' parameter.");
			var normalizedToolName = toolName!;

			if (!toolRegistry.ContainsTool(normalizedToolName))
				throw new McpProtocolException(-32602, $"Unknown tool: {normalizedToolName}", HttpStatusCode.BadRequest, new { name = normalizedToolName });

			var toolArguments = McpProtocolHelpers.GetObject(context.Request.Params, "arguments");
			var stopwatch = Stopwatch.StartNew();
			try {
				var result = toolRegistry.ExecuteTool(normalizedToolName, toolArguments);
				stopwatch.Stop();
				McpLogger.Info($"mcp.tool transport={context.Transport.ToLogValue()} sessionId={context.SessionId ?? "-"} requestId={context.RequestId ?? "null"} name={normalizedToolName} durationMs={stopwatch.ElapsedMilliseconds} isError={result.IsError}");
				return result;
			}
			catch (Exception ex) {
				stopwatch.Stop();
				McpLogger.Exception(ex, $"mcp.tool transport={context.Transport.ToLogValue()} sessionId={context.SessionId ?? "-"} requestId={context.RequestId ?? "null"} name={normalizedToolName} durationMs={stopwatch.ElapsedMilliseconds}");
				throw;
			}
		}

		object HandleReadResource(McpRequestContext context) {
			var uri = McpProtocolHelpers.GetString(context.Request.Params, "uri");
			if (string.IsNullOrWhiteSpace(uri))
				throw new McpProtocolException(-32602, "Resource read requires a non-empty 'uri' parameter.");
			var normalizedUri = uri!;

			var content = toolRegistry.ReadResource(normalizedUri);
			if (content == null)
				throw new McpProtocolException(-32602, $"Resource not found: {normalizedUri}", HttpStatusCode.NotFound, new { uri = normalizedUri });

			return new ReadResourceResult {
				Contents = new List<ResourceContent> {
					new ResourceContent {
						Uri = normalizedUri,
						MimeType = "text/markdown",
						Text = content,
					},
				},
			};
		}

		static ToolCatalogFilter CreateCatalogFilter(Dictionary<string, object>? args) {
			bool includeHidden = false;
			var mode = McpProtocolHelpers.GetString(args, "mode");
			if (string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase))
				includeHidden = true;

			if (args != null && args.TryGetValue("include_hidden", out var includeHiddenObj)) {
				if (includeHiddenObj is bool hiddenBool) includeHidden = hiddenBool;
				else if (includeHiddenObj is System.Text.Json.JsonElement hiddenElem) includeHidden = hiddenElem.ValueKind == System.Text.Json.JsonValueKind.True;
				else if (bool.TryParse(includeHiddenObj?.ToString(), out var parsed)) includeHidden = parsed;
			}

			return new ToolCatalogFilter { IncludeHiddenByDefault = includeHidden };
		}
	}
}
