# Context Mole

<p align="center">
  <a href="https://contextmole.com/">
    <img src="docs/branding/context-mole/originals/context-mole-01-app-icon.png" alt="Context Mole mascot" width="160">
  </a>
</p>

<p align="center">
  <a href="https://contextmole.com/"><img alt="Website" src="https://img.shields.io/badge/website-contextmole.com-2D7FF9?style=flat-square&amp;logo=googlechrome&amp;logoColor=white"></a>
  <a href="https://github.com/JPCodeCraft/Context-Mole/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/JPCodeCraft/Context-Mole?display_name=tag&amp;sort=semver&amp;style=flat-square&amp;logo=github"></a>
  <a href="https://github.com/JPCodeCraft/Context-Mole/actions/workflows/release-windows.yml"><img alt="Release status" src="https://img.shields.io/github/actions/workflow/status/JPCodeCraft/Context-Mole/release-windows.yml?style=flat-square&amp;label=release"></a>
  <a href="https://github.com/JPCodeCraft/Context-Mole/releases/latest"><img alt="Platform" src="https://img.shields.io/badge/platform-Windows%2010%2B-0078D4?style=flat-square&amp;logo=windows11&amp;logoColor=white"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/github/license/JPCodeCraft/Context-Mole?style=flat-square"></a>
</p>

Context Mole is a private, local document index for AI assistants. The desktop app watches your folders and builds a searchable SQLite index; its read-only MCP server lets compatible agents find passages with exact file, attachment, page, sheet, slide, or message provenance.

Documents are opened read-only. Indexing, OCR, embeddings, and search run on your computer after the required model files have been downloaded.

## Install and use

Windows 10+ x64 users can download the latest per-user installer from [GitHub Releases](https://github.com/JPCodeCraft/Context-Mole/releases/latest). Releases are currently unsigned, so Windows SmartScreen may show an unrecognized-app warning.

Before installing the first Context Mole release, uninstall any pre-rename test build. The clean rename intentionally starts with new application data, startup registration, executable names, and AI connections.

1. Install and open Context Mole.
2. Add a project and select one or more non-overlapping folders.
3. Open **Settings** and choose a global CPU profile and Granite embedding model.
4. Under **AI Connections**, configure each assistant you want to use.
5. Reload the assistant, approve the local MCP server if prompted, and ask it to list the Context Mole projects.

The CPU and embedding-model choices apply globally across projects. **Light**, **Normal**, and **Heavy** use up to 20%, 40%, and 80% of logical CPU threads. Granite 311M favors quality; Granite 97M uses less time and memory. Switching models rebuilds semantic embeddings in the background while keyword search remains available.

On Windows, start-at-sign-in is enabled on first launch and can be disabled in Settings. Installed builds check GitHub Releases for updates and offer to restart when an update is ready and indexing is idle.

## AI Connections

Every configured client launches the same local, read-only MCP server and searches the same Context Mole database. Indexes and embeddings are not duplicated. “Configured” means the client knows how to start Context Mole; the MCP process itself runs only while that client or session is running.

Context Mole can configure these clients automatically:

- OpenAI Codex and ChatGPT desktop
- Claude Code and Claude Desktop
- Cursor
- Zed
- GitHub Copilot CLI
- Gemini CLI
- Google Antigravity
- Kiro
- JetBrains Junie
- Devin CLI / Devin Desktop
- Windsurf Cascade (legacy configuration)
- Cline

VS Code is configured through **MCP: Add Server** because its user file depends on the active profile. Roo Code is configured through **Edit Global MCP** because its storage path depends on the host editor. OpenCode uses a different nested configuration shape. All three appear in AI Connections with a button to open the appropriate manual guidance below.

Automatic setup preserves unrelated values, writes a timestamped backup before changing an existing file, and refuses to overwrite a same-name entry it does not own. JSON formatting and comments may be normalized; the backup retains the original text. Reload the client after configuring or removing Context Mole.

## Manual MCP setup

Use manual setup when a client is not listed or when you prefer to own its configuration. Manual `context-mole` entries are never overwritten or removed by the app.

First resolve these locations to absolute paths:

- MCP server: `mcp-server\ContextMole.Mcp.exe` beside the installed `ContextMole.App.UI.exe` on Windows, or `mcp-server/ContextMole.Mcp` on macOS/Linux.
- Data directory: `%LOCALAPPDATA%\ContextMole` on Windows, `~/Library/Application Support/ContextMole` on macOS, or `${XDG_DATA_HOME:-~/.local/share}/ContextMole` on Linux.

If `CONTEXTMOLE_DATA_DIR` is set for the desktop app, use that same path in the client configuration.

### Standard JSON clients

Claude Desktop, Cursor, Gemini CLI, Google Antigravity, Kiro, JetBrains Junie, Devin, Windsurf, Cline, and most MCP clients accept this shape. Merge `context-mole` into any existing `mcpServers` object; do not replace other entries.

```json
{
  "mcpServers": {
    "context-mole": {
      "command": "C:\\absolute\\path\\to\\mcp-server\\ContextMole.Mcp.exe",
      "args": [],
      "env": {
        "CONTEXTMOLE_DATA_DIR": "C:\\absolute\\path\\to\\ContextMole"
      }
    }
  }
}
```

Clients infer the local stdio transport from `command`. Cursor is the exception in this list: add `"type": "stdio"` inside its `context-mole` object. Common user-level locations are:

| Client | Configuration |
| --- | --- |
| Claude Desktop | `%APPDATA%\Claude\claude_desktop_config.json` |
| Cursor | `~/.cursor/mcp.json` |
| Gemini CLI | `~/.gemini/settings.json` |
| Google Antigravity | `~/.gemini/config/mcp_config.json` |
| Kiro | `~/.kiro/settings/mcp.json` |
| JetBrains Junie | `~/.junie/mcp/mcp.json` |
| Devin CLI / Desktop | `%APPDATA%\devin\mcp_config.json` on Windows; `~/.config/devin/mcp_config.json` elsewhere |
| Windsurf Cascade (legacy) | `~/.codeium/windsurf/mcp_config.json` |
| Cline | `~/.cline/data/settings/cline_mcp_settings.json` |
| Roo Code | MCP view → **Edit Global MCP** |

### Zed

Zed uses the same server object under `context_servers` in `%APPDATA%\Zed\settings.json` on Windows, `~/Library/Application Support/Zed/settings.json` on macOS, or `~/.config/zed/settings.json` on Linux:

```json
{
  "context_servers": {
    "context-mole": {
      "command": "C:\\absolute\\path\\to\\mcp-server\\ContextMole.Mcp.exe",
      "args": [],
      "env": {
        "CONTEXTMOLE_DATA_DIR": "C:\\absolute\\path\\to\\ContextMole"
      }
    }
  }
}
```

### Claude Code

The user scope makes Context Mole available in every local Claude Code project:

```powershell
claude mcp add --transport stdio --scope user `
  --env "CONTEXTMOLE_DATA_DIR=C:\absolute\path\to\ContextMole" `
  context-mole -- "C:\absolute\path\to\mcp-server\ContextMole.Mcp.exe"
```

Remove it with `claude mcp remove context-mole --scope user` and verify it with `claude mcp get context-mole`.

### ChatGPT desktop and OpenAI Codex

ChatGPT desktop, Codex CLI, and the Codex IDE extension share `~/.codex/config.toml`. Merge this entry there:

```toml
[mcp_servers.context-mole]
command = "C:\\absolute\\path\\to\\mcp-server\\ContextMole.Mcp.exe"
enabled = true
startup_timeout_sec = 60

[mcp_servers.context-mole.env]
CONTEXTMOLE_DATA_DIR = "C:\\absolute\\path\\to\\ContextMole"
```

### Visual Studio Code

Run **MCP: Add Server**, choose **Command (stdio)** and the user profile, or merge this into the `mcp.json` opened by **MCP: Open User Configuration**:

```json
{
  "servers": {
    "context-mole": {
      "type": "stdio",
      "command": "C:\\absolute\\path\\to\\mcp-server\\ContextMole.Mcp.exe",
      "args": [],
      "env": {
        "CONTEXTMOLE_DATA_DIR": "C:\\absolute\\path\\to\\ContextMole"
      }
    }
  }
}
```

### GitHub Copilot CLI

Use the CLI:

```powershell
copilot mcp add context-mole `
  --env "CONTEXTMOLE_DATA_DIR=C:\absolute\path\to\ContextMole" `
  -- "C:\absolute\path\to\mcp-server\ContextMole.Mcp.exe"
```

Its manual file is `~/.copilot/mcp-config.json`, with a top-level `mcpServers` object and `"type": "local"`. Remove the entry with `copilot mcp remove context-mole`.

### OpenCode

Merge this local server into `~/.config/opencode/opencode.json` or `opencode.jsonc`. The current stable configuration keeps server names directly under `mcp`:

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "context-mole": {
      "type": "local",
      "command": [
        "C:\\absolute\\path\\to\\mcp-server\\ContextMole.Mcp.exe"
      ],
      "enabled": true,
      "environment": {
        "CONTEXTMOLE_DATA_DIR": "C:\\absolute\\path\\to\\ContextMole"
      }
    }
  }
}
```

OpenCode v2 uses the same local-server object under `mcp.servers` and replaces `enabled` with an optional `disabled` flag:

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "servers": {
      "context-mole": {
        "type": "local",
        "command": [
          "C:\\absolute\\path\\to\\mcp-server\\ContextMole.Mcp.exe"
        ],
        "environment": {
          "CONTEXTMOLE_DATA_DIR": "C:\\absolute\\path\\to\\ContextMole"
        }
      }
    }
  }
}
```

After any manual setup, reload the client, approve or trust the local server if asked, and have the client call `list_projects`. The server uses standard input/output only and opens no network listener.

## Supported content

Context Mole supports:

- Documents and ebooks: PDF, DOCX/DOCM/DOTX/DOTM, XLSX/XLSM/XLTX/XLTM, PPTX/PPTM/PPSX/PPSM/POTX/POTM, ODT, ODS, ODP, RTF, and EPUB.
- Tables and structured data: CSV, TSV, JSON, JSONL, XML, YAML/YML, and TOML.
- Text and web content: TXT, LOG, Markdown, RST, AsciiDoc, TeX, HTML/HTM, and MHT/MHTML.
- Images: PNG, JPEG, BMP, GIF, WebP, and TIFF.
- Email: EML and MSG.
- Archives: ZIP, RAR, 7Z, TAR, TAR.GZ/TGZ, and GZ.

Supported content is also extracted when it appears inside email attachments, Office packages, PDFs, or archives. Macro-enabled Office files are read as document packages; embedded macros are never executed.

Keyword search uses SQLite FTS5. Optional semantic search uses IBM Granite Embedding Multilingual R2. PP-OCRv6 handles scanned PDFs and images. Malformed, encrypted, unavailable, oversized, or unsupported items are isolated as document errors instead of stopping a project.

| Platform | Native extraction | OCR | Granite |
| --- | --- | --- | --- |
| Windows x64 | Yes | Yes | Yes |
| Linux x64 (glibc) | Yes | Yes | Yes |
| macOS arm64 | Yes | Yes | Yes |
| macOS x64 | Yes | No | No |

ONNX Runtime 1.29 does not provide an Intel macOS native library, so macOS x64 uses native text extraction and keyword search only.

## Data and privacy

Application data is stored in:

- Windows: `%LOCALAPPDATA%\ContextMole`
- macOS: `~/Library/Application Support/ContextMole`
- Linux: `$XDG_DATA_HOME/ContextMole` or `~/.local/share/ContextMole`

Set `CONTEXTMOLE_DATA_DIR` to choose another location. `CONTEXTMOLE_MCP_PATH` can point development builds at a specific MCP executable, and `CONTEXTMOLE_MATERIALIZE_MAX_BYTES` controls the maximum size of materialized attachment content.

Context Mole never modifies indexed source files. When an AI client explicitly requests attachment materialization, the MCP server may create a controlled temporary copy inside the Context Mole data directory.

## Development

The repository pins its .NET SDK in `global.json`. Restore, test, and run with:

```powershell
dotnet restore ContextMole.slnx --locked-mode
dotnet test --solution ContextMole.slnx -c Release --no-restore
dotnet run --project src/App.UI/ContextMole.App.UI.csproj -c Release --no-restore
```

Package versions are centralized in `Directory.Packages.props`, and lock files are committed. See [development checks](docs/DEVELOPMENT.md) and the [native smoke checklist](docs/NATIVE-SMOKE.md) for additional validation.

Publishing the UI also places the self-contained MCP sidecar in its `mcp-server` directory:

```powershell
dotnet publish src/App.UI/ContextMole.App.UI.csproj `
  -c Release -f net10.0 -r <rid> --self-contained true `
  -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishAot=false
```

Stable Windows releases are built from exact `vMAJOR.MINOR.PATCH` tags by the [release workflow](.github/workflows/release-windows.yml).

## License

Context Mole is available under the [MIT License](LICENSE).

Copyright (c) 2026 JPCodeCraft

## Architecture

The modular monolith contains `App.UI`, `Core`, `Documents`, `Indexing`, `Infrastructure`, `Mcp`, `Search`, and `Storage`. SQLite uses WAL mode, query-only read connections, one serialized writer, durable job leases, staging revisions, and atomic activation.
