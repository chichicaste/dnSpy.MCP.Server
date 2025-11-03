using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using dnSpy.MCP.Server.Presentation;
using dnSpy.MCP.Server.Application;
using dnSpy.MCP.Server.Contracts;
using dnSpy.MCP.Server.Helper;

namespace dnSpy.MCP.Server.Communication {
	/// <summary>
	/// HTTP server implementing the Model Context Protocol (MCP) for exposing dnSpy analysis tools to AI assistants.
	/// Uses an HttpListener hosted on localhost to keep dependencies minimal.
	/// </summary>
	[Export(typeof(McpServer))]
	public sealed class McpServer : IDisposable {
		readonly McpSettings settings;
		readonly McpTools tools;
		readonly BepInExResources bepinexResources;
		HttpListener? httpListener;
		readonly List<SseClient> sseClients = new List<SseClient>();
		readonly object sseClientsLock = new object();
		CancellationTokenSource? cts;

		// JSON serialization options to ignore null values (JSON-RPC 2.0 requirement)
		static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions {
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
		};

		/// <summary>
		/// Initializes the MCP server with the specified settings, tools, and documentation.
		/// </summary>
		[ImportingConstructor]
		public McpServer(McpSettings settings, McpTools tools, BepInExResources bepinexResources) {
			this.settings = settings;
			this.tools = tools;
			this.bepinexResources = bepinexResources;
		}

		/// <summary>
		/// Starts the MCP server if enabled in settings.
		/// </summary>
		public void Start() {
			McpLogger.Debug($"Start() called - EnableServer={settings.EnableServer}");

			if (!settings.EnableServer) {
				McpLogger.Info("Server not enabled in settings - skipping start");
				return;
			}

			if (httpListener != null) {
				McpLogger.Warning($"Server is already running on {settings.Host}:{settings.Port}");
				return;
			}

			McpLogger.Info($"═══════════════════════════════════════════════════════");
			McpLogger.Info($"Starting MCP Server");
			McpLogger.Info($"Host: {settings.Host}");
			McpLogger.Info($"Port: {settings.Port}");
			McpLogger.Info($"═══════════════════════════════════════════════════════");

			try {
				cts = new CancellationTokenSource();

				StartHttpListenerServer();
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "Failed to start server");
			}
		}

		void StartHttpListenerServer() {
			Task.Run(() => {
				try {
					McpLogger.Debug("Creating HttpListener");

					httpListener = new HttpListener();
					// Bind only to localhost to avoid exposing the admin API on network interfaces.
					var prefix = $"http://localhost:{settings.Port}/";
					httpListener.Prefixes.Add(prefix);
					httpListener.Start();

					McpLogger.Info($"HttpListener server started on localhost:{settings.Port}");
					McpLogger.Info("Server is ready to accept connections");
					BroadcastStatus("running");

					while (!cts!.Token.IsCancellationRequested) {
						try {
							var context = httpListener.GetContext();
							McpLogger.Debug($"Accepted connection from {context.Request.RemoteEndPoint}");
							Task.Run(() => HandleHttpRequest(context), cts.Token);
						}
						catch (HttpListenerException) {
							McpLogger.Debug("HttpListener stopped (expected during shutdown)");
							break; // Listener was stopped
						}
						catch (Exception ex) {
							McpLogger.Exception(ex, "Error accepting HTTP request");
						}
					}
				}
				catch (Exception ex) {
					McpLogger.Exception(ex, "Error starting HttpListener");
					httpListener = null;
				}
			}, cts!.Token);
		}

		void HandleHttpRequest(HttpListenerContext context) {
			try {
				// Enable CORS
				context.Response.AddHeader("Access-Control-Allow-Origin", "*");
				context.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
				context.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");

				if (context.Request.HttpMethod == "OPTIONS") {
					context.Response.StatusCode = 200;
					context.Response.Close();
					return;
				}

				var path = context.Request.Url?.AbsolutePath ?? "/";

				// Handle SSE requests on both /events and / (GET)
				if (context.Request.HttpMethod == "GET" && (path == "/events" || path == "/")) {
					McpLogger.Debug($"SSE connection attempt on path: {path}");
					HandleSseRequest(context);
					return;
				}

				if (path == "/health" && context.Request.HttpMethod == "GET") {
					var healthResponse = "{\"status\":\"ok\",\"service\":\"dnSpy MCP Server\"}";
					var buffer = Encoding.UTF8.GetBytes(healthResponse);
					context.Response.ContentType = "application/json";
					context.Response.ContentLength64 = buffer.Length;
					context.Response.OutputStream.Write(buffer, 0, buffer.Length);
					context.Response.Close();
					return;
				}

				if (path == "/" && context.Request.HttpMethod == "POST") {
					using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
					var body = reader.ReadToEnd();

					var request = JsonSerializer.Deserialize<McpRequest>(body);
					if (request == null) {
						context.Response.StatusCode = 400;
						var errorBytes = Encoding.UTF8.GetBytes("Invalid request");
						context.Response.OutputStream.Write(errorBytes, 0, errorBytes.Length);
						context.Response.Close();
						return;
					}

					var response = HandleRequest(request);
					var responseJson = JsonSerializer.Serialize(response, jsonOptions);
					var responseBytes = Encoding.UTF8.GetBytes(responseJson);

					context.Response.ContentType = "application/json";
					context.Response.ContentLength64 = responseBytes.Length;
					context.Response.OutputStream.Write(responseBytes, 0, responseBytes.Length);
					context.Response.Close();
				}
				else {
					McpLogger.Warning($"Unhandled request: {context.Request.HttpMethod} {path}");
					context.Response.StatusCode = 404;
					context.Response.Close();
				}
			}
			catch (Exception ex) {
				try {
					settings.Log($"ERROR in HandleHttpRequest: {ex.GetType().Name}: {ex.Message}");
					var errorResponse = new McpResponse {
						JsonRpc = "2.0",
						Error = new McpError {
							Code = -32603,
							Message = "Internal error",
							Data = ex.Message
						}
					};

					var responseJson = JsonSerializer.Serialize(errorResponse, jsonOptions);
					var responseBytes = Encoding.UTF8.GetBytes(responseJson);
					context.Response.ContentType = "application/json";
					context.Response.ContentLength64 = responseBytes.Length;
					context.Response.OutputStream.Write(responseBytes, 0, responseBytes.Length);
					context.Response.Close();
				}
				catch {
					// Failed to send error response
				}
				// Also surface full exception to Output pane for diagnostics
				try {
					settings.LogError($"ERROR in HandleHttpRequest (stack): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
					McpOutput.SafeWriteException(ex);
				}
				catch {
					// best-effort
				}
			}
		}

		/// <summary>
		/// Restarts the MCP server.
		/// </summary>
		public void Restart() {
			McpLogger.Info("Restarting MCP server...");
			Stop();
			// Small delay to ensure clean shutdown
			Task.Delay(500).Wait();
			Start();
			McpLogger.Info("MCP server restart completed");
		}

		/// <summary>
		/// Gets the current status of the server.
		/// </summary>
		public bool IsRunning =>
			httpListener != null && httpListener.IsListening;

		/// <summary>
		/// Gets a status message for the server.
		/// </summary>
		public string GetStatusMessage() {
			// Report localhost as the bind address since the server intentionally binds to loopback.
			return IsRunning ? $"Server is running on localhost:{settings.Port}" : "Server is stopped";
		}

		/// <summary>
		/// Stops the MCP server if it's running.
		/// </summary>
		public void Stop() {
			McpLogger.Info("Stopping MCP server...");
			try {
				cts?.Cancel();
				httpListener?.Stop();
				httpListener?.Close();
				httpListener = null;
				McpLogger.Info("MCP server stopped successfully");
				CloseAllSseClients();
				BroadcastStatus("stopped");
				cts?.Dispose();
				cts = null;
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "Error stopping server");
			}
		}

		void HandleSseRequest(HttpListenerContext context) {
			if (cts == null) {
				McpLogger.Warning("SSE request rejected: CancellationTokenSource is null (server stopping)");
				context.Response.StatusCode = 503;
				context.Response.Close();
				return;
			}

			try {
				McpLogger.Info("Initializing SSE stream for client");
				var response = context.Response;
				response.StatusCode = 200;
				response.ContentType = "text/event-stream";
				response.SendChunked = true;
				response.Headers["Cache-Control"] = "no-cache";
				response.Headers["Connection"] = "keep-alive";

				McpLogger.Debug("SSE headers sent to client");
				var writer = new StreamWriter(response.OutputStream, Encoding.UTF8) { AutoFlush = true };
				writer.WriteLine(": connected");
				writer.WriteLine();
				writer.Flush();

				McpLogger.Info("SSE client connected and registered");
				var client = new SseClient(writer, response);
				AddSseClient(client);
				BroadcastStatus("running"); // push latest state immediately

				McpLogger.Debug("Starting SSE heartbeat task");
				Task.Run(() => RunSseHeartbeatAsync(client, cts.Token));
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "Failed to initialize SSE stream");
				try {
					context.Response.StatusCode = 500;
					context.Response.Close();
				}
				catch { }
			}
		}

		async Task RunSseHeartbeatAsync(SseClient client, CancellationToken token) {
			try {
				while (!token.IsCancellationRequested && !client.IsClosed) {
					await Task.Delay(TimeSpan.FromSeconds(15), token);
					if (token.IsCancellationRequested || client.IsClosed)
						break;

					client.WriteComment("heartbeat");
				}
			}
			catch (TaskCanceledException) {
				// expected during shutdown
			}
			catch (Exception ex) {
				McpLogger.Exception(ex, "Heartbeat loop failed for SSE client");
			}
			finally {
				RemoveSseClient(client);
			}
		}

		void BroadcastStatus(string status) {
			lock (sseClientsLock) {
				if (sseClients.Count == 0)
					return;
				var payload = $"{{\"status\":\"{status}\",\"timestamp\":\"{DateTime.UtcNow:O}\"}}";
				foreach (var client in sseClients.ToArray()) {
					try {
						client.WriteEvent("status", payload);
					}
					catch {
						client.Dispose();
						sseClients.Remove(client);
					}
				}
			}
		}

		void AddSseClient(SseClient client) {
			lock (sseClientsLock) {
				sseClients.Add(client);
			}
		}

		void RemoveSseClient(SseClient client) {
			lock (sseClientsLock) {
				if (sseClients.Remove(client)) {
					client.Dispose();
				}
			}
		}

		void CloseAllSseClients() {
			lock (sseClientsLock) {
				foreach (var client in sseClients)
					client.Dispose();
				sseClients.Clear();
			}
		}

		sealed class SseClient : IDisposable {
			readonly StreamWriter writer;
			readonly HttpListenerResponse response;
			readonly object writeLock = new object();
			bool isClosed;

			public bool IsClosed {
				get {
					lock (writeLock) {
						return isClosed;
					}
				}
			}

			public SseClient(StreamWriter writer, HttpListenerResponse response) {
				this.writer = writer;
				this.response = response;
			}

			public void WriteEvent(string eventName, string data) {
				lock (writeLock) {
					if (isClosed)
						return;
					writer.WriteLine($"event: {eventName}");
					writer.WriteLine($"data: {data}");
					writer.WriteLine();
					writer.Flush();
				}
			}

			public void WriteComment(string comment) {
				lock (writeLock) {
					if (isClosed)
						return;
					writer.WriteLine($": {comment}");
					writer.WriteLine();
					writer.Flush();
				}
			}

			public void Dispose() {
				lock (writeLock) {
					if (isClosed)
						return;
					isClosed = true;
					try {
						writer.Dispose();
					}
					catch { }
					try {
						response.OutputStream.Close();
					}
					catch { }
					try {
						response.Close();
					}
					catch { }
				}
			}
		}

		McpResponse HandleRequest(McpRequest request) {
			try {
				// Handle notifications (no response needed)
				if (request.Method.StartsWith("notifications/")) {
					McpLogger.Debug($"Received notification: {request.Method}");
					return new McpResponse {
						JsonRpc = "2.0",
						Id = request.Id,
						Result = new { }
					};
				}

				McpLogger.Info($"Handling MCP request: {request.Method}");

				var result = request.Method switch {
					"initialize" => HandleInitialize(),
					"ping" => HandlePing(),
					"tools/list" => HandleListTools(),
					"tools/call" => HandleCallTool(request.Params),
					"resources/list" => HandleListResources(),
					"resources/read" => HandleReadResource(request.Params),
					_ => throw new Exception($"Unknown method: {request.Method}")
				};

				McpLogger.Debug($"Request {request.Method} completed successfully");

				return new McpResponse {
					JsonRpc = "2.0",
					Id = request.Id,
					Result = result
				};
			}
			catch (ArgumentException ex) {
				// ArgumentException indicates invalid parameters (MCP error code -32602)
				McpLogger.Warning($"Invalid parameters for {request.Method}: {ex.Message}");
				return new McpResponse {
					JsonRpc = "2.0",
					Id = request.Id,
					Error = new McpError {
						Code = -32602,
						Message = ex.Message
					}
				};
			}
			catch (Exception ex) {
				// Other exceptions are internal errors (MCP error code -32603)
				McpLogger.Exception(ex, $"Error handling request {request.Method}");
				return new McpResponse {
					JsonRpc = "2.0",
					Id = request.Id,
					Error = new McpError {
						Code = -32603,
						Message = ex.Message
					}
				};
			}
		}

		object HandleInitialize() {
			return new InitializeResult {
				ProtocolVersion = "2024-11-05",
				Capabilities = new ServerCapabilities {
					Tools = new Dictionary<string, object>(),
					Resources = new Dictionary<string, object>()
				},
				ServerInfo = new ServerInfo {
					Name = "dnSpy MCP Server",
					Version = "1.0.0"
				}
			};
		}

		object HandlePing() {
			// Simple ping/pong for keepalive
			return new { };
		}

		object HandleListTools() {
			return new ListToolsResult {
				Tools = tools.GetAvailableTools()
			};
		}

		object HandleCallTool(Dictionary<string, object>? parameters) {
			if (parameters == null)
				throw new ArgumentException("Parameters required");

			var toolCallJson = JsonSerializer.Serialize(parameters);
			var toolCall = JsonSerializer.Deserialize<CallToolRequest>(toolCallJson);

			if (toolCall == null)
				throw new ArgumentException("Invalid tool call parameters");

			return tools.ExecuteTool(toolCall.Name, toolCall.Arguments);
		}

		object HandleListResources() {
			return new ListResourcesResult {
				Resources = bepinexResources.GetResources()
			};
		}

		object HandleReadResource(Dictionary<string, object>? parameters) {
			if (parameters == null)
				throw new ArgumentException("Parameters required");

			var requestJson = JsonSerializer.Serialize(parameters);
			var readRequest = JsonSerializer.Deserialize<ReadResourceRequest>(requestJson);

			if (readRequest == null || string.IsNullOrEmpty(readRequest.Uri))
				throw new ArgumentException("Resource URI required");

			var content = bepinexResources.ReadResource(readRequest.Uri);
			if (content == null)
				throw new ArgumentException($"Resource not found: {readRequest.Uri}");

			return new ReadResourceResult {
				Contents = new List<ResourceContent> {
					new ResourceContent {
						Uri = readRequest.Uri,
						MimeType = "text/markdown",
						Text = content
					}
				}
			};
		}

		/// <summary>
		/// Disposes the server and releases all resources.
		/// </summary>
		public void Dispose() {
			Stop();
		}
	}
}
