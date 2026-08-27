# MCPIndexSearch

MCPIndexSearch is a local desktop indexer for Codex. The Avalonia application manages named projects and folders; research and conversation stay in Codex through a separate, read-only stdio MCP executable. Source files are opened read-only and are never edited, launched, or used to fetch external resources.

## Install on Windows

Windows 10+ x64 users can download the latest `Setup.exe` from [GitHub Releases](https://github.com/JPCodeCraft/MCPIndexSearch/releases/latest). The Velopack installer is per-user, does not require administrator privileges, and creates shortcuts on the Desktop and Start menu. This initial release is not digitally signed, so Windows SmartScreen may display an unrecognized-app warning. Code signing is recommended before broader public distribution and can be added to the release workflow without changing the package or update-feed format.

Installed Windows builds check the public stable `win-x64` GitHub Releases feed at startup and every six hours. New versions download in the background. When a package is ready, the app displays **Restart to update**; restarting remains unavailable while any project is actively indexing so services can drain cleanly before the binaries are replaced. Development builds, portable copies, macOS, and Linux do not query the update feed.

Updates replace application files only. The index database, downloaded models, logs, and settings remain in their existing application-data directories. If the bundled MCP executable changes, the existing connection status displays **Update Codex connection** so Codex can be pointed to the new verified deployment.

## Development prerequisites

- The exact .NET SDK 10.0.203 selected by `global.json`.
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

ZIP/RAR recursive indexing and safe entry materialization (uses a committed RAR regression fixture and does not
require WinRAR or 7-Zip):

```powershell
$env:MCPINDEXSEARCH_DATA_DIR = "$PWD/artifacts/archive-smoke-data"
dotnet run --file tools/ArchiveSmoke.cs
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

Create a project and select one or more non-overlapping folders. The UI reports discovered, pending, indexed, skipped, and error counts plus every actively accessed file. Each live row shows its pipeline stage, total elapsed time, time in the current stage, and pipeline position; the panel also reports the active-file count and successfully completed-file average for the selected project during the current app session. Rows are reconciled in place so timer refreshes do not reset scrolling. Closing the window keeps indexing active when a usable tray icon is available; Linux uses a minimize/taskbar fallback. **Quit** cancels producers and OCR, drains the single database writer, checkpoints WAL, and stops the host.

The global performance selector persists one CPU budget for the whole application: **Light** permits at most 20% of logical threads, **Normal** 40%, and **Heavy** 80% (with a one-thread minimum). Ordinary indexing jobs reserve one unit each. Granite, OCR, and Granite model validation temporarily upgrade one job to the complete selected budget and configure ONNX Runtime to use that many intra-operation threads, so even a lone document can use the available capacity. The upgrade is exclusive across every project and process-local model consumer, preventing parallel projects from multiplying the limit. On Windows, the application registers itself to start at sign-in on first launch; clear **Start MCPIndexSearch with Windows** to persistently disable it.

Pause persists across restarts and stops new job dispatch while watchers continue to record changes. **Retry failed files** queues only documents currently carrying an unresolved error; successful files remain untouched. Retry, reindex, and remove require confirmation. Removing a project deletes only local index records—originals remain untouched.

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

The indexer supports PDF (including selective OCR for scanned pages), DOCX, XLSX, PPTX, TXT, Markdown, HTML, PNG, JPEG, BMP, GIF, WebP, TIFF, EML, MSG, ZIP, RAR, and recursively supported attachments. ZIP and RAR entries are streamed individually in archive order and are never expanded as a directory tree, so entry names such as `../file.txt` remain provenance rather than filesystem paths. Nested archives use the same depth, count, individual-size, aggregate-size, and SHA-256 cycle limits as email and document attachments. Encrypted, malformed, or unavailable items are isolated as errors without stopping a project. Unsupported embedded items are retained as attachment nodes without marking the otherwise successful parent document as failed, and remain available to `materialize_content` by their indexed `content_id`.

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

### Publish a Windows release

The Windows installer and automatic-update feed are produced only from strict stable tags in the form `vMAJOR.MINOR.PATCH`. The tag version is applied to both the desktop executable and bundled MCP sidecar. For example:

```powershell
git tag -a v0.1.0 -m "MCPIndexSearch v0.1.0"
git push origin v0.1.0
```

`.github/workflows/release-windows.yml` restores committed lockfiles, builds and publishes self-contained `win-x64` output, verifies the approved branding source and required executables, downloads the previous feed when available for delta generation, and runs Velopack 1.2.0. It publishes the per-user installer, full package, available deltas, and `releases.win-x64.json` to a public GitHub Release using the workflow-provided `GITHUB_TOKEN`. Release notes are generated from GitHub history and embedded in both the package and Release.

Manual self-contained builds for `linux-x64`, `osx-x64`, and `osx-arm64` remain available through the commands above, but those platforms do not currently have an installer or automatic updates.

## Architecture

The modular monolith has eight projects: `App.UI`, `Core`, `Indexing`, `Documents`, `Search`, `Storage`, `Mcp`, and `Infrastructure`. SQLite runs in WAL mode with one bounded-channel writer and concurrent query-only read connections. Durable jobs use leases, retry backoff, startup recovery, observation epochs, and invisible staging revisions followed by atomic activation.

The first public Windows release is intentionally unsigned. The release workflow keeps packaging and publication separate so a signing step can be inserted before Velopack publication later.
