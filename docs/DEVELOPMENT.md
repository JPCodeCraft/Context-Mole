# Development checks

Use the SDK pinned by `global.json`, restore the committed lock files, and run the automated suite:

```powershell
dotnet restore MCPIndexSearch.slnx --locked-mode
dotnet test --solution MCPIndexSearch.slnx -c Release --no-restore
```

This is the default validation for every change. The tools below are supplemental manual smoke tests for focused integration and runtime checks. Run this assignment again before every smoke command so each one gets a fresh disposable data directory:

```powershell
$env:MCPINDEXSEARCH_DATA_DIR = Join-Path $env:TEMP ("MCPIndexSearch-smoke-" + [guid]::NewGuid().ToString("N"))
```

Run the checks relevant to a change:

```powershell
# Core indexing lifecycle, pause/resume, provenance, and source integrity
dotnet run --file tools/DevelopmentSmoke.cs

# Durable job replacement and retry behavior
dotnet run --file tools/JobSupersessionSmoke.cs
dotnet run --file tools/ErrorResolutionSmoke.cs

# CPU/model persistence, global admission, fairness, and full-budget upgrades
dotnet run --file tools/CpuUsagePolicySmoke.cs

# HTML/MHTML variants, extraction failures, and malformed/unsupported content
dotnet run --file tools/ExtractionRobustnessSmoke.cs
dotnet run --file tools/EmlRegressionSmoke.cs

# SQLite WAL reader/writer concurrency and embedding-policy migration safety
dotnet run --file tools/SqliteWalConcurrencySmoke.cs

# ZIP/RAR recursion and entry provenance
dotnet run --file tools/ArchiveSmoke.cs

# Inventory filters, status, sorting, and cursor pagination
dotnet run --file tools/DocumentInventorySmoke.cs

# Verified root and attachment materialization
dotnet run --file tools/MaterializationSmoke.cs

# One-click Codex configuration ownership and backup behavior
dotnet run --file tools/CodexConfigurationSmoke.cs
```

The OCR runtime check downloads model assets into the isolated directory and verifies direct image OCR plus scanned-PDF fallback:

```powershell
dotnet run --file tools/OcrRuntimeSmoke.cs
```

For headless semantic-model setup:

```powershell
dotnet run --file tools/BootstrapAssets.cs -- --accept-gemma-terms
```

Before publishing, follow the platform checklist in [NATIVE-SMOKE.md](NATIVE-SMOKE.md). Linux and macOS desktop/native-loader checks must be run on those operating systems.
