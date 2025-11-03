using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Settings;
using dnSpy.Contracts.Text;

namespace dnSpy.MCP.Server {
	/// <summary>
	/// Settings for the MCP server extension, including server configuration and logging.
	/// </summary>
	public class McpSettings : ViewModelBase {
		/// <summary>
		/// Allows derived implementations to attach a server instance so lifecycle changes can be propagated.
		/// Base implementation is a no-op.
		/// </summary>
		/// <param name="server">The MCP server instance to attach.</param>
		internal virtual void SetServer(McpServer server) {
			// Default implementation intentionally left blank.
		}

		/// <summary>
		/// Gets or sets whether the MCP server is enabled.
		/// </summary>
		public bool EnableServer {
			get => enableServer;
			set {
				if (enableServer != value) {
					enableServer = value;
					OnPropertyChanged(nameof(EnableServer));
				}
			}
		}
		bool enableServer = false;

		/// <summary>
		/// Gets or sets the server host (default: localhost).
		/// </summary>
		public string Host {
			get => host;
			set {
				if (host != value) {
					host = value;
					OnPropertyChanged(nameof(Host));
				}
			}
		}
		string host = "localhost";

		/// <summary>
		/// Gets or sets the server port (default: 3000).
		/// </summary>
		public int Port {
			get => port;
			set {
				if (port != value) {
					port = value;
					OnPropertyChanged(nameof(Port));
				}
			}
		}
		int port = 3000;

		/// <summary>
		/// Gets the collection of log messages (limited to last 100 messages).
		/// </summary>
		public ObservableCollection<string> LogMessages { get; } = new ObservableCollection<string>();

		/// <summary>
		/// Gets or sets the combined log text for easy copying.
		/// </summary>
		string logText = string.Empty;
		public string LogText {
			get => logText;
			set {
				if (logText != value) {
					logText = value;
					OnPropertyChanged(nameof(LogText));
				}
			}
		}

		/// <summary>
		/// Adds a log message to the log collection and forwards it to the centralized logger.
		/// </summary>
		/// <param name="message">The log message to add.</param>
		public void Log(string message) {
			// Use centralized logger (includes timestamp and file logging)
			McpLogger.Info(message);

			// Keep and update the in-UI log collection (used by the settings UI).
			var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
			var logEntry = $"[{timestamp}] {message}";

			if (System.Windows.Application.Current?.Dispatcher != null) {
				System.Windows.Application.Current.Dispatcher.Invoke(() => {
					LogMessages.Add(logEntry);
					while (LogMessages.Count > 100)
						LogMessages.RemoveAt(0);
					LogText = string.Join(Environment.NewLine, LogMessages);
				});
			} else {
				LogMessages.Add(logEntry);
				while (LogMessages.Count > 100)
					LogMessages.RemoveAt(0);
				LogText = string.Join(Environment.NewLine, LogMessages);
			}
		}

		/// <summary>
		/// Log an informational message.
		/// </summary>
		public void LogInfo(string message) {
			McpLogger.Info(message);
			AddToUILog("INFO", message);
		}

		/// <summary>
		/// Log a warning message.
		/// </summary>
		public void LogWarn(string message) {
			McpLogger.Warning(message);
			AddToUILog("WARN", message);
		}

		/// <summary>
		/// Log an error message.
		/// </summary>
		public void LogError(string message) {
			McpLogger.Error(message);
			AddToUILog("ERROR", message);
		}

		/// <summary>
		/// Adds a message to the UI log collection.
		/// </summary>
		void AddToUILog(string level, string message) {
			var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
			var logEntry = $"[{timestamp}] [{level}] {message}";

			if (System.Windows.Application.Current?.Dispatcher != null) {
				System.Windows.Application.Current.Dispatcher.Invoke(() => {
					LogMessages.Add(logEntry);
					while (LogMessages.Count > 100)
						LogMessages.RemoveAt(0);
					LogText = string.Join(Environment.NewLine, LogMessages);
				});
			} else {
				LogMessages.Add(logEntry);
				while (LogMessages.Count > 100)
					LogMessages.RemoveAt(0);
				LogText = string.Join(Environment.NewLine, LogMessages);
			}
		}

		/// <summary>
		/// Clears all log messages from the UI.
		/// </summary>
		public void ClearLogs() {
			// Clear on UI thread if available
			if (System.Windows.Application.Current?.Dispatcher != null) {
				System.Windows.Application.Current.Dispatcher.Invoke(() => {
					LogMessages.Clear();
					LogText = string.Empty;
				});
			} else {
				LogMessages.Clear();
				LogText = string.Empty;
			}
		}
		/// <summary>
		/// Creates a copy of these settings.
		/// </summary>
		public McpSettings Clone() => CopyTo(new McpSettings());

		/// <summary>
		/// Copies these settings to another instance.
		/// </summary>
		public McpSettings CopyTo(McpSettings other) {
			other.EnableServer = EnableServer;
			other.Host = Host;
			other.Port = Port;
			return other;
		}
	}

	/// <summary>
	/// Implementation of MCP settings with persistence support.
	/// </summary>
	[Export(typeof(McpSettings))]
	sealed class McpSettingsImpl : McpSettings {
		static readonly Guid SETTINGS_GUID = new Guid("352907A0-9DF5-4B2B-B47B-95E504CAC301");

		readonly ISettingsService settingsService;
		McpServer? mcpServer;

		[ImportingConstructor]
		McpSettingsImpl(ISettingsService settingsService) {
			this.settingsService = settingsService;

			// Load settings from persistent storage
			var sect = settingsService.GetOrCreateSection(SETTINGS_GUID);
			EnableServer = sect.Attribute<bool?>(nameof(EnableServer)) ?? EnableServer;
			Host = sect.Attribute<string>(nameof(Host)) ?? Host;
			Port = sect.Attribute<int?>(nameof(Port)) ?? Port;

			PropertyChanged += McpSettingsImpl_PropertyChanged;
		}

		/// <summary>
		/// Sets the server instance for dynamic control.
		/// If the settings already indicate the server should be enabled, start it immediately.
		/// </summary>
		internal override void SetServer(McpServer server) {
			mcpServer = server;
			try {
				if (mcpServer != null && EnableServer) {
					// Log and attempt to start - Start() is idempotent if already running
					Log("EnableServer=true on settings; starting MCP server from SetServer()");
					mcpServer.Start();
				}
			}
			catch (Exception ex) {
				Log($"ERROR while starting server from SetServer(): {ex.GetType().Name}: {ex.Message}");
			}
		}

		void McpSettingsImpl_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
			// Save settings to persistent storage
			var sect = settingsService.RecreateSection(SETTINGS_GUID);
			sect.Attribute(nameof(EnableServer), EnableServer);
			sect.Attribute(nameof(Host), Host);
			sect.Attribute(nameof(Port), Port);

			// Handle server enable/disable dynamically (no restart required)
			if (e.PropertyName == nameof(EnableServer) && mcpServer != null) {
				if (EnableServer) {
					Log("Starting MCP server");
					mcpServer.Start();

					// Verify asynchronously that the server started and report to output
					System.Threading.Tasks.Task.Run(async () => {
						await System.Threading.Tasks.Task.Delay(300);
						try {
							if (mcpServer.IsRunning)
								Log("MCP server is running");
							else
								Log("MCP server failed to start");
						}
						catch (Exception ex) {
							Log($"Error checking server status: {ex.Message}");
						}
					});
				} else {
					Log("Stopping MCP server");
					mcpServer.Stop();

					// Verify asynchronously that the server stopped and report to output
					System.Threading.Tasks.Task.Run(async () => {
						await System.Threading.Tasks.Task.Delay(200);
						try {
							if (mcpServer.IsRunning)
								Log("MCP server is still running");
							else
								Log("MCP server stopped");
						}
						catch (Exception ex) {
							Log($"Error checking server status: {ex.Message}");
						}
					});
				}
			}
		}
	}
}
