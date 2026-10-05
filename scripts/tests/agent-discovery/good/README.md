# Fixture

## Pointframe MCP Server

```powershell
Invoke-WebRequest "$release/Pointframe.Mcp-win-x64.mcpb" -OutFile $mcpb
claude mcp add --scope user pointframe -- "$env:LOCALAPPDATA\Programs\Pointframe.Mcp\Pointframe.Mcp.exe"
codex mcp add pointframe -- "$env:LOCALAPPDATA\Programs\Pointframe.Mcp\Pointframe.Mcp.exe"
```

## Privacy Policy

Fixture. Opt out with `POINTFRAME_TELEMETRY_OPTOUT=1`. Properties: `name`, `host`.
