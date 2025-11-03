using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using dnSpy.Contracts.Output;
using dnSpy.Contracts.Text;

namespace dnSpy.MCP.Server {
	/// <summary>
	/// User control for MCP server settings UI.
	/// </summary>
	public partial class McpSettingsControl : UserControl {
		/// <summary>
		/// Initializes the settings control.
		/// </summary>
		public McpSettingsControl() => InitializeComponent();
	}
}
