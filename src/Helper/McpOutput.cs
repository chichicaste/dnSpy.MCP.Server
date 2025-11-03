using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Output;
using dnSpy.Contracts.Text;

namespace dnSpy.MCP.Server.Helper {
	/// <summary>
	/// Centralized logger for the MCP Server extension.
	/// Provides unified logging to both file and dnSpy Output pane.
	/// All messages are timestamped and written to a single log file.
	/// Default log directory: %APPDATA%\dnSpy\dnSpy.MCPServer (override with DNSpy_MCP_LOG_PATH env var).
	/// </summary>
	public static class McpLogger {
		static readonly object locker = new object();

		// These are set by the auto-loaded MEF class.
		internal static IOutputTextPane? OutputPane { get; set; }
		internal static IOutputService? OutputService { get; set; }

		// Buffer messages produced before the output pane is created.
		static readonly List<string> pendingMessages = new List<string>();
		const int MaxBufferedMessages = 1000;

		/// <summary>
		/// Gets the log directory path.
		/// </summary>
		public static string LogDirectory {
			get {
				var env = Environment.GetEnvironmentVariable("DNSpy_MCP_LOG_PATH");
				if (!string.IsNullOrEmpty(env))
					return env;
				var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
				return Path.Combine(appdata, "dnSpy", "dnSpy.MCPServer");
			}
		}

		/// <summary>
		/// Gets the main log file path.
		/// </summary>
		public static string LogFilePath => Path.Combine(LogDirectory, "dnspy_mcp.log");

		/// <summary>
		/// Log levels for categorizing messages.
		/// </summary>
		public enum LogLevel {
			Info,
			Warning,
			Error,
			Debug
		}

		/// <summary>
		/// Logs a message with the specified level.
		/// Message is written to both file and output pane.
		/// </summary>
		public static void Log(LogLevel level, string message) {
			var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
			var levelStr = level.ToString().ToUpper();
			var formattedMessage = $"[{timestamp}] [{levelStr}] {message}";

			// Write to file first (best-effort)
			WriteToFile(formattedMessage);

			// Write to output pane (buffered if not ready)
			WriteToOutputPane(formattedMessage);

			// For errors, also write to Debug/Trace as fallback
			if (level == LogLevel.Error) {
				try {
					System.Diagnostics.Debug.WriteLine(formattedMessage);
					System.Diagnostics.Trace.WriteLine(formattedMessage);
				}
				catch {
					// ignore
				}
			}
		}

		/// <summary>
		/// Logs an informational message.
		/// </summary>
		public static void Info(string message) => Log(LogLevel.Info, message);

		/// <summary>
		/// Logs a warning message.
		/// </summary>
		public static void Warning(string message) => Log(LogLevel.Warning, message);

		/// <summary>
		/// Logs an error message.
		/// </summary>
		public static void Error(string message) => Log(LogLevel.Error, message);

		/// <summary>
		/// Logs a debug message.
		/// </summary>
		public static void Debug(string message) => Log(LogLevel.Debug, message);

		/// <summary>
		/// Logs an exception with full details including stack trace.
		/// </summary>
		public static void Exception(Exception ex, string? contextMessage = null) {
			var message = contextMessage != null
				? $"{contextMessage}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}"
				: $"EXCEPTION: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}";

			Log(LogLevel.Error, message);
		}

		/// <summary>
		/// Writes a message to the log file (best-effort).
		/// Creates directory if it doesn't exist.
		/// </summary>
		static void WriteToFile(string formattedMessage) {
			try {
				var dir = LogDirectory;
				if (!Directory.Exists(dir)) {
					Directory.CreateDirectory(dir);
				}

				File.AppendAllText(LogFilePath, formattedMessage + Environment.NewLine);
			}
			catch {
				// ignore any file I/O errors (some hosts may restrict disk access)
			}
		}

		/// <summary>
		/// Writes a message to the output pane.
		/// Buffers message if pane is not ready yet.
		/// </summary>
		static void WriteToOutputPane(string formattedMessage) {
			lock (locker) {
				if (OutputPane != null) {
					try {
						OutputPane.WriteLine(formattedMessage);
						return;
					}
					catch {
						// fall through to buffering on error
					}
				}

				// Buffer if can't write now
				pendingMessages.Add(formattedMessage);
				// Keep buffer bounded to avoid unbounded memory use
				if (pendingMessages.Count > MaxBufferedMessages)
					pendingMessages.RemoveAt(0);
			}
		}

		/// <summary>
		/// Flushes any buffered messages to the output pane.
		/// Called when the output pane becomes available.
		/// </summary>
		internal static void FlushPendingMessages() {
			List<string>? toFlush = null;
			lock (locker) {
				if (pendingMessages.Count == 0 || OutputPane == null)
					return;
				toFlush = new List<string>(pendingMessages);
				pendingMessages.Clear();
			}

			foreach (var msg in toFlush!) {
				try {
					OutputPane!.WriteLine(msg);
				}
				catch {
					// ignore individual write errors
				}
			}
		}
	}

	/// <summary>
	/// Legacy compatibility wrapper for existing code.
	/// Redirects to McpLogger for unified logging.
	/// </summary>
	public static class McpOutput {
		// Unique GUID for our output pane - keep stable so users can identify it
		public static readonly Guid THE_GUID = new("3A6F9C2E-8E3B-4D7A-AB9F-1C2D3E4F5A6B");

		// These are set by the auto-loaded MEF class.
		public static IOutputTextPane? Instance {
			get => McpLogger.OutputPane;
			internal set => McpLogger.OutputPane = value;
		}

		public static IOutputService? OutputService {
			get => McpLogger.OutputService;
			internal set => McpLogger.OutputService = value;
		}

		public static string LogDirectory => McpLogger.LogDirectory;
		public static string LogFilePath => McpLogger.LogFilePath;

		/// <summary>
		/// Writes a line to the output pane (legacy wrapper).
		/// </summary>
		public static void SafeWriteLine(string text) => McpLogger.Info(text);

		/// <summary>
		/// Writes exception details (legacy wrapper).
		/// </summary>
		public static void SafeWriteException(Exception ex) => McpLogger.Exception(ex);

		/// <summary>
		/// Flushes pending messages (legacy wrapper).
		/// </summary>
		internal static void FlushPendingToPane() => McpLogger.FlushPendingMessages();

		/// <summary>
		/// Appends to log file (legacy wrapper).
		/// </summary>
		internal static void TryAppendLogFile(string text) {
			var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
			var formattedMessage = $"[{timestamp}] [INFO] {text}";
			try {
				var dir = LogDirectory;
				if (!Directory.Exists(dir))
					Directory.CreateDirectory(dir);
				File.AppendAllText(LogFilePath, formattedMessage + Environment.NewLine);
			}
			catch {
				// ignore
			}
		}
	}

	// Top-level MEF auto-loaded class so the composition host reliably discovers it.
	[ExportAutoLoaded(Order = double.MinValue)]
	public sealed class McpOutputAutoLoaded : IAutoLoaded {
		[ImportingConstructor]
		public McpOutputAutoLoaded(IOutputService outputService) {
			try {
				McpLogger.Debug("McpOutputAutoLoaded invoked - initializing output pane");

				McpLogger.OutputService = outputService;
				McpLogger.OutputPane = outputService.Create(McpOutput.THE_GUID, "MCP Server");

				// Announce initialization
				McpLogger.Info("═══════════════════════════════════════════════════════");
				McpLogger.Info("MCP Server Output Pane Initialized");
				McpLogger.Info($"Log file location: {McpLogger.LogFilePath}");
				McpLogger.Info($"Version: 1.0.0");
				McpLogger.Info("═══════════════════════════════════════════════════════");

				// Flush any messages that were buffered before the pane existed
				McpLogger.FlushPendingMessages();

				McpLogger.Debug("Output pane initialization completed successfully");
			}
			catch (Exception ex) {
				// If creation fails, fallback to Debug/Trace
				var msg = $"McpOutputAutoLoaded failed: {ex.GetType().Name}: {ex.Message}";
				try {
					System.Diagnostics.Debug.WriteLine(msg);
					System.Diagnostics.Debug.WriteLine(ex.StackTrace);
					System.Diagnostics.Trace.WriteLine(msg);
				}
				catch {
					// ignore
				}
			}
		}
	}
}