# Hevo.Charting.Mcp

MCP (Model Context Protocol) server exposing Hevo.Charting low-code blueprint capabilities to LLMs.

> **Status**: Initial cut. Provides 3 tools — `list_components`, `describe_component`, `validate_blueprint` — covering the LLM "see → describe → validate" loop for blueprint authoring. Runtime launch / streaming diagnostics are out of scope here (those need a host with WPF render loop).

## Why

[低代码.md](../Hevo.Charting/LowCode/Designer/低代码.md) + [§K trigger 协议](../Hevo.Charting/LowCode/Designer/低代码优化方案.md) made the blueprint protocol stable enough that an LLM can author/modify them as data. This MCP server gives the LLM agent live access to the live-process ComponentRegistry and DryRun static analyzer, so it can:

1. Discover what features are available (no hallucinated type names)
2. Inspect port shapes before composing connections (no type mismatches → no blank screen)
3. Validate generated blueprints before user runs them (catch the "loads-but-blank" failure mode)

## Tools

### `list_components(kind = "all")`

Returns a JSON array of registered components. `kind` ∈ `"feature"` / `"trait"` / `"datasource"` / `"all"`.

Example response:
```json
[
  {"alias": "LineSeriesFeature", "fullTypeName": "Hevo.Charting.Features.LineSeriesFeature", "kind": "feature"},
  {"alias": "ScaleStrategyTrait", "fullTypeName": "Hevo.Charting.Core.ScaleStrategyTrait", "kind": "trait"},
  ...
]
```

### `describe_component(typeName)`

Returns the port shape and category for a specific alias.

Example response:
```json
{
  "alias": "LineSeriesFeature",
  "fullTypeName": "Hevo.Charting.Features.LineSeriesFeature",
  "kind": "feature",
  "category": "Series",
  "inputPorts": [
    {"name": "DataPort", "dataType": "DataPort<ReadOnlyMemory<double>>", "isArray": false, "description": "线段值数据源…"}
  ],
  "outputPorts": [
    {"name": "YRangePort", "dataType": "DataPort<RealRange>", "isArray": false, "description": "Y 轴量程…"}
  ]
}
```

### `validate_blueprint(blueprintJson)`

Runs `BlueprintLauncher.DryRun` against the deserialized blueprint and returns the structured diagnostics. **No handlers are passed** — trigger / delegate-prop handler references will surface as warnings (`BP_TRIGGER_HANDLER_MISSING` / `BP_HANDLER_NOT_REGISTERED`); that's expected for a static check.

Example response:
```json
{
  "error": null,
  "launched": true,
  "diagnostics": [
    {"severity": "Warning", "code": "BP_PORT_TYPE_MISMATCH", "portName": "ts_time",
     "message": "端口类型冲突: 已被 TimeData.Time 注册为 DateTime,LineSeriesFeature.DataPort 试图重连为 ReadOnlyMemory<Double>…"}
  ]
}
```

## Build & Run

From repo root:

```sh
dotnet build "Hevo.Charting.Mcp/Hevo.Charting.Mcp.csproj" -nologo
dotnet run --project "Hevo.Charting.Mcp/Hevo.Charting.Mcp.csproj"
```

Server reads JSON-RPC frames from stdin, writes responses to stdout. **All logs go to stderr** to keep the protocol channel clean.

## Wire it to a client

Most MCP clients accept a `command` + `args` setup. Example for Claude Desktop / Cursor `mcp.json`:

```json
{
  "mcpServers": {
    "hevo-charting": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "D:/Code/Hevo.Drawing/Hevo.Charting.Mcp/Hevo.Charting.Mcp.csproj",
        "--no-build"
      ]
    }
  }
}
```

For production, prefer `dotnet publish -c Release` once and point `command` at the resulting exe to skip startup compilation.

## Try it (manual JSON-RPC)

After starting the server in a terminal you can paste a JSON-RPC message into stdin to drive it:

```jsonc
// initialize → tools/list → tools/call (list_components)
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"manual","version":"0"}}}
{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_components","arguments":{"kind":"feature"}}}
```

Response on stdout will contain a `content[].text` JSON array of feature aliases registered by `GraphViewerBootstrap.Initialize()` at startup.

## Platform note

This MCP server currently targets `net8.0-windows10.0.19041.0` because it ProjectReferences `Hevo.Charting.csproj` which uses WPF. The MCP server itself **does not render UI** (`<UseWPF>` is intentionally not set), but the WPF runtime must be available — meaning **Windows-only** for now. Cross-platform requires upstream work to split the WPF rendering layer from `Hevo.Charting`'s schema/blueprint model. That's a separate engineering item.

## Caveats

- `validate_blueprint` runs DryRun with `handlers: null`. Blueprints with `Triggers` or `OnRequireDataAsync` etc. delegate-prop will get `BP_*_HANDLER_*` warnings — those resolve at runtime, not statically. Treat them as informational.
- `BlueprintJsonOptions.Default` is used for deserialization. `Color` / `IHevoBrush` JSON forms documented in [低代码优化方案.md §7](../Hevo.Charting/LowCode/Designer/低代码优化方案.md) apply.
- `describe_component` calls `NodeFactory.CreateNode` which runs reflection but does not render. WPF assemblies must be loadable but no UI thread is needed.
