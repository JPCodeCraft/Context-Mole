# Context Mole

Context Mole is a private, local document index for Codex. The desktop app watches folders and builds a searchable SQLite index; its read-only MCP server lets Codex find passages with exact file, attachment, page, sheet, slide, or message provenance.

Documents are opened read-only. Indexing, OCR, embeddings, and search run locally after the required model files have been downloaded.

## Install and use

Windows 10+ x64 users can download the latest per-user installer from [GitHub Releases](https://github.com/JPCodeCraft/Context-Mole/releases/latest). Releases are currently unsigned, so Windows SmartScreen may show an unrecognized-app warning.

1. Install and open Context Mole.
2. Add a project and select one or more non-overlapping folders.
3. In **Settings**, choose a global CPU profile and a Granite embedding model.
4. Select **Connect to Codex** and restart Codex.
5. Ask Codex to search the indexed projects.

The CPU and embedding-model choices apply globally across projects. **Light**, **Normal**, and **Heavy** use up to 20%, 40%, and 80% of logical CPU threads. Granite 311M favors quality; Granite 97M uses less time and memory. Switching models re-embeds active projects in the background while keyword search remains available.

On Windows, start-at-sign-in is enabled on first launch and can be disabled in Settings. Installed builds check GitHub Releases for updates and offer to restart when an update is ready and indexing is idle.

## Supported content

Context Mole supports PDF, DOCX, XLSX, PPTX, TXT, Markdown, HTML/HTM, MHT/MHTML, PNG, JPEG, BMP, GIF, WebP, TIFF, EML, MSG, ZIP, and RAR, including supported nested attachments and archive entries.

Keyword search uses SQLite FTS5. Optional semantic search uses IBM Granite Embedding Multilingual R2. PP-OCRv6 handles scanned PDFs and images. Malformed, encrypted, unavailable, oversized, or unsupported items are isolated as document errors instead of stopping a project.

| Platform | Native extraction | OCR | Granite |
| --- | --- | --- | --- |
| Windows x64 | Yes | Yes | Yes |
| Linux x64 (glibc) | Yes | Yes | Yes |
| macOS arm64 | Yes | Yes | Yes |
| macOS x64 | Yes | No | No |

ONNX Runtime 1.29 does not provide an Intel macOS native library, so macOS x64 uses native text extraction and keyword search only.

## Codex connection

**Connect to Codex** safely updates the shared Codex configuration, preserves unrelated settings, and creates a timestamped backup. A pre-existing entry not owned by the app is never overwritten. The MCP server uses stdio only and creates no network listener.

The read-only tools list projects and documents, search passages, inspect provenance and errors, browse attachments, resolve verified source files, and materialize indexed content into controlled temporary storage.

## Data and upgrade compatibility

New installations store application data in:

- Windows: `%LOCALAPPDATA%\ContextMole`
- macOS: `~/Library/Application Support/ContextMole`
- Linux: `$XDG_DATA_HOME/ContextMole` or `~/.local/share/ContextMole`

Existing installations automatically continue using an existing `MCPIndexSearch` data directory when no `ContextMole` directory exists, preserving the database, settings, logs, and downloaded models. Set `CONTEXTMOLE_DATA_DIR` to choose another location; `MCPINDEXSEARCH_DATA_DIR` remains a supported alias. The former `MCPINDEXSEARCH_MCP_PATH` and `MCPINDEXSEARCH_MATERIALIZE_MAX_BYTES` variables also remain aliases for their `CONTEXTMOLE_*` replacements.

The executable names `MCPIndexSearch.App.UI` and `MCPIndexSearch.Mcp`, the Codex configuration key `mcp-index-search`, and the Velopack package ID `JPCodeCraft.MCPIndexSearch` intentionally remain stable. These internal identifiers let existing installations and Codex connections upgrade without being orphaned; the product shown to users is Context Mole.

## Development

The repository pins its .NET SDK in `global.json`. Restore and test with:

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

## Architecture

The modular monolith contains `App.UI`, `Core`, `Documents`, `Indexing`, `Infrastructure`, `Mcp`, `Search`, and `Storage`. SQLite uses WAL mode, query-only read connections, one serialized writer, durable job leases, staging revisions, and atomic activation.
