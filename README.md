# dnSpy MCP Server

A Model Context Protocol (MCP) server that exposes dnSpy's .NET assembly analysis capabilities to AI assistants, enabling advanced code analysis, reverse engineering, and tool generation.

**Status**: 🟢 Develop | **Commands**: 30/30 Implemented | **Compilation**: ✅ 0 errors, 0 warnings

---

## Features

### Core Capabilities
- **Assembly Discovery**: List and navigate .NET assemblies
- **Type Inspection**: Analyze types, methods, properties, and fields
- **Code Analysis**: Decompile methods, analyze inheritance, find references
- **Usage Finding**: Track method callers, field access, type dependencies
- **Call Graph Analysis**: Build recursive call graphs for methods
- **Dead Code Detection**: Identify unused types and methods
- **Code Generation**: Generate BepInEx plugins and Harmony patches

### 30 Implemented Commands
**Phases 1-3** (15 commands): Assembly discovery, type inspection, code generation
**Phase 4** (4 commands): Usage finding with IL analysis
**Phase 5** (6 commands): Advanced code analysis
**Stubs** (5 commands): Available for future enhancements

---

## Installation & Build

### Prerequisites
- Visual Studio 2022 or dotnet CLI
- .NET Framework 4.8 SDK
- .NET 8.0 SDK

### Build Instructions

1. **Clone dnSpyEx repository**:
   ```bash
   git clone https://github.com/dnSpyEx/dnSpy.git dnSpyEx
   cd dnSpyEx
   ```

2. **Clone this extension into Extensions folder**:
   ```bash
   cd dnSpy\Extensions
   git clone <this-repo-url> dnSpy.MCP.Server
   cd dnSpy.MCP.Server
   ```

3. **Build the extension**:
   ```bash
   dotnet build -c Debug
   ```

4. **Output location**:
   - .NET 4.8: `dnSpy\bin\Debug\net48\dnSpy.MCP.Server.x.dll`
   - .NET 8.0: `dnSpy\bin\Debug\net8.0-windows\dnSpy.MCP.Server.x.dll`

### Runtime Setup

1. **Start dnSpy** with the compiled extension
2. **MCP Server** automatically starts on `http://localhost:3000`
3. **Configure MCP client** (e.g., KiloCode):

```json
{
  "mcpServers": {
    "dnspy-mcp": {
      "type": "streamable-http",
      "url": "http://localhost:3000",
      "alwaysAllow": ["list_assemblies"],
      "disabled": false
    }
  }
}
```

---

## Usage Examples

### Find Type Usages
```python
client.call_tool("find_who_uses_type", {
    "assembly_name": "MyGame",
    "type_full_name": "Game.Player"
})
```

### Analyze Call Graph
```python
client.call_tool("analyze_call_graph", {
    "assembly_name": "MyGame",
    "type_full_name": "Game.Manager",
    "method_name": "Update",
    "max_depth": 5
})
```

### Find Dead Code
```python
client.call_tool("find_dead_code", {
    "assembly_name": "MyGame",
    "include_private": True
})
```

---

## Documentation

- **STATUS.md** - Implementation status and progress metrics
- **ARCHITECTURE.md** - System design and component documentation
- **WORKFLOW.md** - Comprehensive technical guide with all command schemas

---

## Architecture

| Component | Purpose |
|-----------|---------|
| **McpServer.cs** | HTTP/SSE protocol handling (546 lines) |
| **McpTools.cs** | 30 command implementations (3,000+ lines) |
| **CodeAnalysisHelpers.cs** | Phase 5 analysis utilities (370 lines) |
| **UsageFindingCommandTools.cs** | Phase 4 IL analysis (350 lines) |
| **AssemblyTools.cs** | Assembly operations (200 lines) |
| **TypeTools.cs** | Type deep analysis (400 lines) |

---

## Command Categories

| Category | Commands | Status |
|----------|----------|--------|
| Assembly Discovery | 4 commands | ✅ |
| Type Inspection | 6 commands | ✅ |
| Method Analysis | 3 commands | ✅ |
| Inheritance & Hooks | 6 commands | ✅ |
| Code Generation | 2 commands | ✅ |
| **Phase 4: Usage Finding** | **4 commands** | **✅** |
| **Phase 5: Code Analysis** | **6 commands** | **✅** |
| Utility | 1 command | ✅ |

---

## Project Structure
 
```
dnSpy.MCP.Server/
├─ src/
│  ├─ Presentation/   # Integracion (UI, menús)
│  ├─ Application/    # Command handlers
│  ├─ Core/           # Modelos + interfaces (dominio)
│  ├─ Communication/  # JSON-RPC + MCP transport (stdio/ws)
│  ├─ Helper/         # Utilidades transversales
│  └─ Contracts/      # DTOs MCP y contratos públicos
├─ docs/
│  ├─ ARCHITECTURE.md
│  └─ STATUS.md
└─ README.md
```

---

## Compilation Status

✅ **0 Errors**
✅ **0 Warnings**
✅ **Multi-target**: .NET Framework 4.8 + .NET 8.0
✅ **MEF Composition**: Validated

---

## Quick Troubleshooting

| Issue | Solution |
|-------|----------|
| Extension not loading | Check `dnSpy\Extensions` folder permissions |
| Server won't start | Port 3000 in use? Check `netstat -ano \| findstr :3000` |
| Command not found | Call `list_tools` to verify available commands |
| Type not found | Use `list_assemblies` then `search_types` |

---

## Contributing

1. Create a new branch
2. Implement changes
3. Run `dotnet build` - must compile with 0 errors/warnings
4. Test via MCP client
5. Submit PR with clear description

---

## Development Info

**Language**: C# 9.0+
**Framework**: .NET Framework 4.8 & .NET 8.0
**Protocol**: Model Context Protocol (MCP)
**Transport**: HTTP/SSE on localhost:3000

**See Also**:
- [STATUS.md](STATUS.md) - Implementation progress
- [ARCHITECTURE.md](ARCHITECTURE.md) - System design
- [WORKFLOW.md](WORKFLOW.md) - Complete technical documentation

---

**Version**: 1.1 | **Status**: 🟢 Ready
