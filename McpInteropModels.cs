using System;
using System.Collections.Generic;

namespace dnSpy.MCP.Server
{
    /// <summary>
    /// Interfaces y modelos para herramientas de interoperabilidad nativa.
    /// </summary>
    public interface IMcpInteropTools
    {
        CallToolResult FindPInvokeSignatures(Dictionary<string, object>? arguments);
        CallToolResult AnalyzeMarshallingAndLayout(Dictionary<string, object>? arguments);
    }
}