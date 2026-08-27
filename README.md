# MCPIndexSearch

MCPIndexSearch is a local document index for Codex. The desktop app watches folders and builds a searchable SQLite index; a separate read-only MCP server lets Codex search it and retrieve passages with exact file, attachment, page, sheet, slide, or message provenance.

Source documents are opened read-only. The app does not edit or launch them, and indexing, OCR, embeddings, and search run locally after the model files have been downloaded.

## Install and use

Windows 10+ x64 users can download the latest per-user installer from [GitHub Releases](https://github.com/JPCodeCraft/MCPIndexSearch/releases/latest). Windows releases are currently unsigned, so SmartScreen may show an unrecognized-app warning.

1. Install and open MCPIndexSearch.
2. Add a project and select one or more non-overlapping folders.
3. Choose a global CPU profile: **Light** (20% of logical threads), **Normal** (40%), or **Heavy** (80%).
4. In **Settings**, choose the Granite 311M model for best quality or Granite 97M for faster, lower-memory embeddings.
5. Select **Connect to Codex**, restart Codex, and use the local MCP tools.

The CPU and embedding-model choices persist globally across projects. Parallel file jobs use one thread each; OCR and Granite temporarily use the full selected CPU budget so one document can use the available capacity. The desktop app and separate MCP process refresh the same model selection, so search stays consistent after a switch.

On Windows, start-at-sign-in is enabled on first launch. Clear **Start MCPIndexSearch with Windows** to disable it persistently. Installed builds check GitHub Releases for updates at startup and every six hours, download in the background, and offer **Restart to update** when indexing is idle. Application updates preserve the database, settings, logs, and downloaded models.

## Search, OCR, and models

Keyword search works with SQLite FTS5. On first use, the app downloads the PP-OCRv6 detector and multilingual recognizer (about 139 MB) for scanned PDFs and images.

Semantic search is optional. Settings offers IBM Granite Embedding Multilingual R2 in two sizes: **311M** for best quality and **97M** for faster inference with lower memory use. Each model is pinned to an exact revision, checksum-verified, and resumable. Switching models automatically re-embeds active projects in the background; paused projects refresh when resumed. The 311M setup presents its tokenizer terms before download. Keyword search remains available whenever the selected model is not ready.

| Platform | Native extraction | OCR | Granite |
| --- | --- | --- | --- |
| Windows x64 | Yes | Yes | Yes |
| Linux x64 (glibc) | Yes | Yes | Yes |
| macOS arm64 | Yes | Yes | Yes |
| macOS x64 | Yes | No | No |

ONNX Runtime 1.29 does not publish an Intel macOS native library, so macOS x64 uses native text extraction and keyword search only.

## Codex connection

**Connect to Codex** updates the shared Codex configuration with a marked `mcp-index-search` block, preserves unrelated settings, and creates a timestamped backup. A pre-existing entry not owned by the app is never overwritten. The server uses stdio only and creates no network listener.

Available read-only tools:

- `list_projects`
- `search_project`
- `read_passages`
- `get_document_info`
- `list_documents`
- `list_attachments`
- `resolve_local_file`
- `materialize_content`

`materialize_content` validates the active index revision and project folder before returning a root file or extracting one indexed attachment into controlled temporary storage.

## Supported content

PDF, DOCX, XLSX, PPTX, TXT, Markdown, HTML/HTM, MHT/MHTML web archives, PNG, JPEG, BMP, GIF, WebP, TIFF, EML, MSG, ZIP, and RAR are supported, including recursively supported attachments and archive entries. Malformed, encrypted, unavailable, or oversized items are isolated as document errors instead of stopping the whole project. Unsupported embedded items remain visible in the attachment tree without failing their parent; unsupported root documents are reported as errors.

Application data is stored in:

- Windows: `%LOCALAPPDATA%\MCPIndexSearch`
- macOS: `~/Library/Application Support/MCPIndexSearch`
- Linux: `$XDG_DATA_HOME/MCPIndexSearch` or `~/.local/share/MCPIndexSearch`

Set `MCPINDEXSEARCH_DATA_DIR` to use another location.

## Development

The repository pins .NET SDK 10.0.203 in `global.json`.

```powershell
dotnet restore MCPIndexSearch.slnx --locked-mode
dotnet test --solution MCPIndexSearch.slnx -c Release --no-restore
dotnet run --project src/App.UI/MCPIndexSearch.App.UI.csproj -c Release --no-restore
```

Automated tests are the default validation. Package versions are centralized in `Directory.Packages.props`, and lock files are committed. See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) for supplemental smoke tests and [docs/NATIVE-SMOKE.md](docs/NATIVE-SMOKE.md) for native publish checks.

## Publish and release

Publishing the UI also places the self-contained MCP sidecar in its `mcp-server` directory. Supported RIDs are `win-x64`, `linux-x64`, `osx-x64`, and `osx-arm64`.

```powershell
dotnet publish src/App.UI/MCPIndexSearch.App.UI.csproj `
  -c Release -f net10.0 -r <rid> --self-contained true `
  -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishAot=false
```

Stable Windows releases are created from exact `vMAJOR.MINOR.PATCH` tags:

```powershell
git tag -a vX.Y.Z -m "MCPIndexSearch vX.Y.Z"
git push origin vX.Y.Z
```

The [Windows release workflow](.github/workflows/release-windows.yml) runs the automated tests, builds the app and MCP sidecar, verifies the payload, creates the Velopack installer/update feed, and publishes the GitHub Release.

## Architecture

The modular monolith contains `App.UI`, `Core`, `Documents`, `Indexing`, `Infrastructure`, `Mcp`, `Search`, and `Storage`. SQLite uses WAL mode, query-only read connections, one serialized writer, durable job leases, staging revisions, and atomic activation. Vector indexes are generation-labelled and cached within a bounded memory budget.
