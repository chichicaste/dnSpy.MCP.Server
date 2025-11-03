using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using dnSpy.Contracts.Extension;
using dnSpy.MCP.Server;
using dnSpy.MCP.Server.Communication;
using dnSpy.MCP.Server.Helper;

namespace dnSpy.MCP.Server.Presentation {
	/// <summary>
	/// Main extension entry point for the MCP (Model Context Protocol) Server.
	/// This extension enables AI assistants to analyze .NET assemblies loaded in dnSpy.
	/// </summary>
	[ExportExtension]
	sealed class TheExtension : IExtension {
		readonly McpServer mcpServer;
		readonly McpSettings mcpSettings;
		readonly object attachmentLock = new();
		bool serverAttached;
		const string LogSeparator = "------------------------------------------------------------";
		// Static constructor for early diagnostics - this should ALWAYS execute if the class is loaded
		static TheExtension() {
			try {
				var logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dnSpy", "dnSpy.MCPServer");
				System.IO.Directory.CreateDirectory(logDir);
				var emergencyLog = System.IO.Path.Combine(logDir, "emergency.log");
				System.IO.File.AppendAllText(emergencyLog, $"[{DateTime.Now:O}] TheExtension STATIC constructor called - MEF IS LOADING THIS CLASS{Environment.NewLine}");
			}
			catch { }
		}

		/// <summary>
		/// Initializes the extension and wires settings to the server instance.
		/// </summary>
		[ImportingConstructor]
		public TheExtension(McpServer mcpServer, McpSettings mcpSettings) {
			this.mcpServer = mcpServer;
			this.mcpSettings = mcpSettings;

			// Emergency logging FIRST thing
			try {
				var logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dnSpy", "dnSpy.MCPServer");
				System.IO.Directory.CreateDirectory(logDir);
				var emergencyLog = System.IO.Path.Combine(logDir, "emergency.log");
				System.IO.File.AppendAllText(emergencyLog, $"[{DateTime.Now:O}] TheExtension instance constructor called - imports set up{Environment.NewLine}");
			}
			catch { }

			McpLogger.Info(LogSeparator);
			McpLogger.Info("MCP Extension - Constructor");
			McpLogger.Info(LogSeparator);

			AttachServerToSettings();
		}

		/// <summary>
		/// Gets merged resource dictionaries. This extension does not provide any.
		/// </summary>
		public IEnumerable<string> MergedResourceDictionaries {
			get {
				yield break;
			}
		}

		/// <summary>
		/// Gets information about this extension.
		/// </summary>
		public ExtensionInfo ExtensionInfo => new ExtensionInfo {
			ShortDescription = "MCP Server for AI-assisted .NET assembly analysis and BepInEx plugin development",
		};

		/// <summary>
		/// Handles extension lifecycle events.
		/// </summary>
		/// <param name="event">The extension event type.</param>
		/// <param name="obj">Event-specific data.</param>
		public void OnEvent(ExtensionEvent @event, object? obj) {
			// Emergency logging
			try {
				var logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dnSpy", "dnSpy.MCPServer");
				var emergencyLog = System.IO.Path.Combine(logDir, "emergency.log");
				System.IO.File.AppendAllText(emergencyLog, $"[{DateTime.Now:O}] OnEvent({@event}) called{Environment.NewLine}");
			}
			catch { }

			McpLogger.Info(LogSeparator);
			McpLogger.Info($"Extension event: {@event}");
			McpLogger.Info(LogSeparator);

			AttachServerToSettings();

			if (@event == ExtensionEvent.AppLoaded) {
				if (mcpSettings.EnableServer) {
					McpLogger.Info("AppLoaded event - ensuring MCP server is running (EnableServer=True)");
					mcpServer.Start();
				}
				else {
					McpLogger.Info("AppLoaded event - MCP server disabled via settings");
				}
			}
			else if (@event == ExtensionEvent.AppExit) {
				McpLogger.Info("MCP Extension unloading");
				try {
					mcpServer.Stop();
				}
				catch (Exception ex) {
					McpLogger.Exception(ex, "Error stopping MCP server during AppExit");
				}
			}
		}

		void AttachServerToSettings() {
			if (serverAttached)
				return;

			lock (attachmentLock) {
				if (serverAttached)
					return;

				try {
					McpLogger.Debug("Linking McpServer instance with persistent settings");
					mcpSettings.SetServer(mcpServer);
					serverAttached = true;
				}
				catch (Exception ex) {
					McpLogger.Exception(ex, "Failed to connect McpServer to settings");
				}
			}
		}
	}
}













