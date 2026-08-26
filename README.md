# MCPIndexSearch

MCPIndexSearch is a local desktop indexer for Codex. The Avalonia application manages named projects and folders; research and conversation stay in Codex through a separate, read-only stdio MCP executable. Source files are opened read-only and are never edited, launched, or used to fetch external resources.

## Development prerequisites

- .NET SDK 10.0.203 or a compatible 10.0 feature band selected by `global.json`.
- Windows x64 for the validated development smoke in this workspace. The supported publish targets are `win-x64`, `linux-x64` (glibc), `osx-x64`, and `osx-arm64`.
- Internet access for NuGet restore and the first-run OCR model download, plus the optional semantic-model download when the user enables it. Indexing, OCR, and search then run locally without Docker, Python, Node.js, API keys, or cloud services.

## Restore and build

```powershell
dotnet restore MCPIndexSearch.slnx --locked-mode
dotnet build MCPIndexSearch.slnx -c Release --no-restore
```

Package versions are centrally pinned in `Directory.Packages.props`; each project has a committed `packages.lock.json`. No Git repository initialization is performed by the setup.

An isolated manual integration gate is available for development work:

```powershell
$env:MCPINDEXSEARCH_DATA_DIR = Join-Path $env:TEMP "MCPIndexSearch-manual-smoke"
dotnet run --file tools/DevelopmentSmoke.cs
```

It exercises migration, create/modify/rename/delete observation, identity preservation, pause backlog/resume, forced reindex, FTS provenance, project removal, and source hash/timestamp stability. It is a manual smoke utility, not an automated test suite.

Two focused manual gates cover job supersession and the one-click Codex configuration flow:

```powershell
$env:MCPINDEXSEARCH_DATA_DIR = Join-Path $env:TEMP "MCPIndexSearch-job-smoke"
dotnet run --file tools/JobSupersessionSmoke.cs

$env:MCPINDEXSEARCH_DATA_DIR = Join-Path $env:TEMP "MCPIndexSearch-codex-config-smoke"
dotnet run --file tools/CodexConfigurationSmoke.cs
```

Attachment materialization has a separate isolated gate covering verified roots, safe EML attachment extraction, changed or missing sources, traversal-style names, hashing, idempotent reuse, and the configured size limit:

```powershell
$env:MCPINDEXSEARCH_DATA_DIR = Join-Path $env:TEMP "MCPIndexSearch-materialization-smoke"
dotnet run --file tools/MaterializationSmoke.cs
```

Root-document inventory has a focused gate for lifecycle statuses, authorized filters, metadata aggregation, stable cursor pagination, and structured validation errors:

```powershell
$env:MCPINDEXSEARCH_DATA_DIR = Join-Path $env:TEMP "MCPIndexSearch-document-inventory-smoke"
dotnet run --file tools/DocumentInventorySmoke.cs
```

The OCR gate downloads into the isolated data directory, verifies direct multilingual recognition, and confirms that a bitmap-only PDF page takes the PDFium-to-PP-OCRv6 fallback path:

```powershell
$env:MCPINDEXSEARCH_DATA_DIR = Join-Path $env:TEMP "MCPIndexSearch-ocr-smoke"
dotnet run --file tools/OcrRuntimeSmoke.cs
```

## OCR and semantic-search setup

On first launch, the application automatically downloads the Apache-2.0 PP-OCRv6 medium detector and multilingual recognizer (about 139 MB total). Downloads are resumable, revision-pinned, SHA-256 verified, and atomically activated under the application data directory. Indexing waits for this setup when a scanned PDF page or image needs OCR; native PDF text continues to be preferred. No Paddle runtime, Python environment, cloud OCR service, account, API key, or license-consent screen is required. Once installed, OCR is fully local and offline.

Keyword search also works immediately. To add optional multilingual semantic search, select **Set up** in the desktop application. The in-app flow explains that Granite is Apache 2.0 while its derived tokenizer is subject to the [Gemma terms](https://ai.google.dev/gemma/terms), asks for acceptance only when it has not already been recorded, and downloads the model with progress, cancellation, resume, SHA-256 verification, and atomic activation. Existing projects are automatically queued for re-embedding after the model loads.

The command-line equivalent remains available for headless development use:

```powershell
dotnet run --file tools/BootstrapAssets.cs -- --accept-gemma-terms
```

On AVX2 x64 computers the installer fetches both the optimized and FP32 Granite profiles, then uses PT/EN/ES fixtures to require at least 0.995 mean corresponding-vector cosine and 90% mean top-10 overlap. A failing optimized profile is disabled in favor of FP32. Other supported architectures download only FP32. Missing model assets never prevent keyword search.

ONNX Runtime 1.29.0, the pinned runtime for this build, no longer publishes an Intel macOS native binary. Consequently the `osx-x64` executable deliberately reports semantic search as unavailable and continues with FTS5 keyword search plus an explicit warning. Granite semantic search remains enabled on `win-x64`, `linux-x64`, and `osx-arm64`. Supporting Granite on Intel macOS would require changing the mandated runtime pin or supplying a separately built compatible native library.

Set `MCPINDEXSEARCH_DATA_DIR` to override the default data directory. Otherwise the application uses:

- Windows: `%LOCALAPPDATA%\MCPIndexSearch`
- macOS: `~/Library/Application Support/MCPIndexSearch`
- Linux: `$XDG_DATA_HOME/MCPIndexSearch` or `~/.local/share/MCPIndexSearch`

## Run the desktop application

```powershell
dotnet run --project src/App.UI/MCPIndexSearch.App.UI.csproj -c Release --no-restore
```

Create a project and select one or more non-overlapping folders. The UI reports discovered, pending, indexed, skipped, and error counts plus every actively accessed file. Each live row shows its pipeline stage, total elapsed time, time in the current stage, and pipeline position; the panel also reports active-time and successfully completed-file averages for the selected project during the current app session. Rows are reconciled in place so timer refreshes do not reset scrolling. Closing the window keeps indexing active when a usable tray icon is available; Linux uses a minimize/taskbar fallback. **Quit** cancels producers and OCR, drains the single database writer, checkpoints WAL, and stops the host.

Pause persists across restarts and stops new job dispatch while watchers continue to record changes. Reindex and remove both require confirmation. Removing a project deletes only local index records—originals remain untouched.

## Register the read-only MCP server with Codex

Use **Connect to Codex** in the application header. It finds the bundled read-only MCP executable, adds a marked `mcp-index-search` block to the [shared Codex configuration](https://learn.chatgpt.com/docs/extend/mcp?surface=cli), preserves all other settings, creates a timestamped backup, and asks the user to restart Codex. The same button disconnects it later. A pre-existing entry that the application does not own is reported and never overwritten.

When the application runs from a development build, the connection flow atomically stages the complete MCP output under `<data-directory>\mcp-server\deployments\<fingerprint>` and registers that isolated copy. Codex is never pointed at `src\Mcp\bin`, so its long-running MCP process cannot lock normal build outputs.

This enables local MCPIndexSearch tools in Codex surfaces that use the local `~/.codex/config.toml`. It does not expose the local server to ordinary ChatGPT web or mobile chats. No network listener is created.

The equivalent manual registration remains available:

```powershell
codex mcp add mcp-index-search --env MCPINDEXSEARCH_DATA_DIR="<data-directory>" -- "<absolute-published-path>\MCPIndexSearch.Mcp.exe"
```

On macOS/Linux, use the published `MCPIndexSearch.Mcp` path without `.exe`. Restart Codex after adding or removing the entry. The server uses stdio only; stdout is reserved for MCP protocol messages and logs go to stderr. It exposes:

- `list_projects`
- `search_project`
- `read_passages`
- `get_document_info`
- `list_documents`
- `list_attachments`
- `resolve_local_file`
- `materialize_content`

All eight tools declare read-only, non-destructive, idempotent, closed-world annotations. The MCP composition contains no migrations, index writer, watchers, project mutation service, web server, or document launcher. It accepts indexed IDs rather than arbitrary paths. `list_documents` provides filtered, deterministically paginated root-document inventory without loading extracted text. `materialize_content` returns an unchanged root document path or extracts only the requested indexed attachment into controlled temporary storage after validating the project folder and active revision fingerprint. The maximum source or attachment size defaults to 250 MiB and can be configured with `MCPINDEXSEARCH_MATERIALIZE_MAX_BYTES`.

## Supported content and provenance

The indexer supports PDF (including selective OCR for scanned pages), DOCX, XLSX, PPTX, TXT, Markdown, HTML, PNG, JPEG, BMP, GIF, WebP, TIFF, EML, MSG, and recursively supported attachments. Expansion is bounded by depth, count, individual size, aggregate size, and SHA-256 cycle detection. Encrypted, malformed, unsupported, or unavailable items are isolated as errors without stopping a project.

Every passage stores its project/document/content identity, physical source path, file metadata, attachment chain, extraction method, OCR confidence when available, and a typed page/sheet/cell/slide/structure/email/image locator. Search returns only stored provenance and never synthesizes citations.

## Search implementation

- SQLite FTS5 with `unicode61 remove_diacritics 2` and BM25.
- IBM Granite Embedding 311M Multilingual R2 through ONNX Runtime.
- CLS pooling from the 768-dimensional output, first 384 Matryoshka dimensions, L2 normalization, exactly 1,536 little-endian bytes per passage.
- Exact `System.Numerics.Vector<float>` dot-product search behind `IVectorIndex`; the interface is the future HNSW replacement seam.
- Equal reciprocal-rank fusion with `k=60`, deterministic tie-breaking, metadata/path filters, and generation-labelled snapshots under a 512 MiB cache budget.

## Publish

Build self-contained, folder-based, non-trimmed, non-AOT executables. Publishing the UI also publishes the separate MCP executable into its `mcp-server` subfolder so the in-app connection is one click. Repeat for `win-x64`, `linux-x64`, `osx-x64`, and `osx-arm64`:

```powershell
dotnet publish src/App.UI/MCPIndexSearch.App.UI.csproj -c Release -f net10.0 -r <rid> --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishAot=false --no-restore
```

The MCP executable can still be published independently when only the server is needed:

```powershell
dotnet publish src/Mcp/MCPIndexSearch.Mcp.csproj -c Release -f net10.0 -r <rid> --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishAot=false --no-restore
```

See [docs/NATIVE-SMOKE.md](docs/NATIVE-SMOKE.md) for publish-output inspection and native platform checks. macOS/Linux checklists are supplied for execution on those platforms; they are not claimed as executed from Windows.

## Architecture

The modular monolith has eight projects: `App.UI`, `Core`, `Indexing`, `Documents`, `Search`, `Storage`, `Mcp`, and `Infrastructure`. SQLite runs in WAL mode with one bounded-channel writer and concurrent query-only read connections. Durable jobs use leases, retry backoff, startup recovery, observation epochs, and invisible staging revisions followed by atomic activation.

Automated tests, installers, signing, update workflows, and release automation are intentionally outside this development build.
