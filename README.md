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

Document parallelism follows the selected CPU profile and the machine's logical CPU count: Light uses 20%, Normal 40%, and Heavy 80%, with at least one worker. OCR temporarily borrows the full capacity allowed by that profile and remains serialized so it does not compete with document parsers. Per-operation resources are disposed promptly, OCR and embedding sessions unload after idle periods, and the broker still clears disposable vector caches under memory pressure.

On Windows, start-at-sign-in is enabled on first launch and can be disabled in Settings. Sign-in launches start quietly in the system tray; clicking the tray icon or choosing **Show Context Mole** restores the window. Installed builds check GitHub Releases for updates and offer to restart when an update is ready and indexing is idle.

## AI Connections

Every configured client launches a small, local stdio adapter and searches the same Context Mole database. The adapters share one on-demand broker process, so Granite model sessions and the vector-index cache are not duplicated when several clients are open. Adapters run only while their client or session is running; the broker unloads idle model state and exits after extended inactivity.

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

## Agent-directed search

`search_project` is a structured, agent-first tool. It searches exactly one project per call and does not accept a human-style query string or query-language expression. The calling agent chooses the retrieval mode, supplies independent lexical clauses, controls ranking weights and filters, and decides how much grouped evidence to return.

The first v0.2 startup discards pre-v0.2 derived passages, embeddings, and full-text rows, then rebuilds each retained project from its source files. Project and folder configuration is preserved. Search coverage can be temporarily incomplete while that one-time background reindex finishes.

Its top-level arguments are `project_id`, `mode`, `semantic_query`, `clauses`, `minimum_should_match`, `field_weights`, `branch_weights`, `filters`, and `result_options`. `project_id` comes from `list_projects`; all other arguments are explicit search controls.

| Mode | Use it for | Required input |
| --- | --- | --- |
| `keyword` | Exact terms, phrases, prefixes, filenames, paths, headings, sheets, or email subjects | At least one positive `must` or `should` clause |
| `semantic` | Concepts whose wording may differ from the question | `semantic_query` |
| `hybrid` (default) | Recall from semantic search plus precision from lexical constraints | `semantic_query`, a positive clause, or both |

Options that cannot affect the selected mode are rejected instead of being silently ignored. For example, `semantic_query` is invalid in `keyword` mode, `field_weights` are invalid in `semantic` mode, and `branch_weights` are valid only in `hybrid` mode.

### Clauses and searchable fields

Each clause has a stable caller-defined `id`, `text`, an `occur` value (`must`, `should`, or `must_not`), a `match` value (`term`, `phrase`, or `prefix`), and optional `fields`. This allows exact requirements, optional ranking signals, and exclusions to be mixed in the same call. Clauses can also constrain candidates in semantic mode. A `term` or `prefix` contains one normalized token; a `phrase` contains one or more tokens in order. This is structured data, not a free-form `+term -term` syntax.

Clause logic is evaluated per passage: every `must` clause and the requested number of `should` clauses must match the same indexed passage. When evidence may be spread across sections of one document or attachment, use separate or `content_ids`-focused searches, then inspect neighboring text with `read_passages` or the original structure with `materialize_content`.

When `fields` is omitted, the clause searches every lexical field: `body`, `title`, `heading`, `filename`, `path`, `content_name`, `sheet`, and `email_subject`. `minimum_should_match` defaults to `1` when a request has only `should` clauses and to `0` when it also has a `must` clause. It can be set from zero through the number of `should` clauses. A request accepts at most 64 clauses; clause IDs must be unique and use 1–64 ASCII letters, numbers, dots, underscores, or hyphens. Clause text is limited to 512 characters and `semantic_query` to 4,096 characters.

The default lexical field weights are:

| Field | Default | Field | Default |
| --- | ---: | --- | ---: |
| `body` | 1.0 | `title` | 3.0 |
| `heading` | 2.0 | `filename` | 2.5 |
| `path` | 0.5 | `content_name` | 2.5 |
| `sheet` | 1.5 | `email_subject` | 3.0 |

Agents can override each field with a finite value from 0–10, including zero to disable its ranking contribution. Hybrid keyword and semantic branch weights both default to 1.0, accept finite values from 0–10, and are normalized before reciprocal-rank fusion. The internal fusion constant is intentionally fixed.

### Filters, confidence, and grouped results

Filters can target stable `document_ids` or returned `content_ids`, authorized `path_prefixes`, inclusive modified-time bounds, and `attachment_scope` (`any`, `root_only`, or `attachments_only`). `root_extensions` filter the source document; `content_extensions` independently filter the root or nested content node. This distinction can, for example, find a PDF attachment inside a `.msg` email. A call accepts up to 100 document IDs, 100 content IDs, 50 path prefixes, and 50 values in each extension list.

Semantic recall is permissive by default. Matches below `semantic_confidence_threshold` (0.25 by default, configurable from -1 to 1) remain in the response with their raw `semantic_score` and `low_confidence: true`. Set `strict_semantic_threshold: true` only when excluding borderline leads is worth the risk of false negatives. If semantic retrieval is unavailable, `semantic` returns no matches plus a structured `semantic_unavailable` warning; `hybrid` returns keyword matches when possible and reports `fallback_keyword`.

Results are grouped by stable `content_id`, so an attachment or archive entry is separate from its container. The defaults return 10 groups, one consolidated preview per group, and at most two groups from one root document. Agents can set `group_limit` from 1–50, `previews_per_group` from 1–10, and `max_groups_per_document` from 1–50. Responses include stable document, content, and passage IDs; provenance and attachment chains; typed locations; keyword, semantic, rank-fusion, and confidence signals; matched clause IDs and fields; unique evaluated match counts; separate keyword, optional-keyword-boost, and semantic inspection depths; collapsed counts; and compact `suppressed_sources` summaries. Branch depths deliberately are not summed because the same passage can appear in more than one branch. Check `candidate_limit_reached` before treating those counts or summaries as exhaustive: when it is `true`, retrieval stopped after it had enough groups, so additional lower-ranked matches or sources may exist. Raise the group limits or focus a follow-up with `filters.content_ids` when those omitted candidates could matter.

The examples below show only tool arguments. Replace the sample project and returned IDs with values from `list_projects` and `search_project`.

### Exact filename lookup

Target `filename` with a phrase, then compare the returned `file_name` value when character-for-character equality matters; phrase matching uses normalized filename tokens.

```json
{
  "project_id": "11111111-1111-1111-1111-111111111111",
  "mode": "keyword",
  "clauses": [
    {
      "id": "exact_file",
      "text": "Q4 forecast.xlsx",
      "occur": "must",
      "match": "phrase",
      "fields": ["filename"]
    }
  ],
  "field_weights": {
    "filename": 10.0,
    "body": 0.0
  }
}
```

### Required phrases with an exclusion

```json
{
  "project_id": "11111111-1111-1111-1111-111111111111",
  "mode": "keyword",
  "clauses": [
    {
      "id": "agreement",
      "text": "service level agreement",
      "occur": "must",
      "match": "phrase",
      "fields": ["body", "title", "heading"]
    },
    {
      "id": "effective_date",
      "text": "effective date",
      "occur": "must",
      "match": "phrase",
      "fields": ["body"]
    },
    {
      "id": "exclude_drafts",
      "text": "draft",
      "occur": "must_not",
      "match": "term",
      "fields": ["filename", "path", "heading"]
    }
  ]
}
```

### Conceptual search

```json
{
  "project_id": "11111111-1111-1111-1111-111111111111",
  "mode": "semantic",
  "semantic_query": "decisions that reduced customer onboarding delays",
  "result_options": {
    "group_limit": 15,
    "previews_per_group": 2
  }
}
```

### Target a PDF nested inside email

```json
{
  "project_id": "11111111-1111-1111-1111-111111111111",
  "mode": "hybrid",
  "semantic_query": "renewal pricing and termination rights",
  "clauses": [
    {
      "id": "contract_name",
      "text": "contract",
      "occur": "should",
      "match": "prefix",
      "fields": ["content_name", "title", "heading"]
    },
    {
      "id": "exclude_template",
      "text": "template",
      "occur": "must_not",
      "match": "term"
    }
  ],
  "minimum_should_match": 0,
  "branch_weights": {
    "keyword": 0.8,
    "semantic": 1.4
  },
  "filters": {
    "root_extensions": ["msg"],
    "content_extensions": ["pdf"],
    "attachment_scope": "attachments_only"
  }
}
```

### Keep borderline semantic leads, then optionally tighten

Start with the recall-oriented default and inspect `results[].previews[].semantic_score` and `low_confidence`:

```json
{
  "project_id": "11111111-1111-1111-1111-111111111111",
  "mode": "semantic",
  "semantic_query": "informal concern about launch readiness"
}
```

Only if the task benefits from precision over recall, repeat it with strict filtering:

```json
{
  "project_id": "11111111-1111-1111-1111-111111111111",
  "mode": "semantic",
  "semantic_query": "informal concern about launch readiness",
  "result_options": {
    "semantic_confidence_threshold": 0.32,
    "strict_semantic_threshold": true
  }
}
```

### Focus a follow-up and inspect the evidence

Focus the next search on one or more promising `content_id` values returned earlier:

```json
{
  "project_id": "11111111-1111-1111-1111-111111111111",
  "mode": "hybrid",
  "semantic_query": "specific approval conditions and exceptions",
  "clauses": [
    {
      "id": "approval",
      "text": "approval",
      "occur": "should",
      "match": "term"
    }
  ],
  "filters": {
    "content_ids": [
      "22222222-2222-2222-2222-222222222222"
    ]
  },
  "result_options": {
    "previews_per_group": 3
  }
}
```

Use returned passage IDs to read stored neighboring text:

```json
{
  "project_id": "11111111-1111-1111-1111-111111111111",
  "passage_ids": [
    "33333333-3333-3333-3333-333333333333"
  ],
  "context_before": 1,
  "context_after": 2
}
```

Or materialize the selected root document, attachment, or archive entry when original formatting, tables, images, or structure matter:

```json
{
  "project_id": "11111111-1111-1111-1111-111111111111",
  "content_id": "22222222-2222-2222-2222-222222222222"
}
```

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

### Windows uninstall and local data

Installed, non-portable Windows builds show **Uninstall Context Mole…** in Settings. On a Windows development or portable build, the control is disabled and explains why it cannot be used; it is not shown on other operating systems. The confirmation dialog selects **Keep data** by default. This closes Context Mole and runs the normal interactive Velopack uninstaller while retaining indexes, downloaded models, settings, temporary materializations, and logs for a compatible reinstall.

Choosing **Permanently delete local data** removes only the canonical `%LOCALAPPDATA%\ContextMole` directory after Context Mole's UI and MCP processes release it. The dialog lists the affected data and warns that deletion cannot be undone. Indexed source files are never deleted. If `CONTEXTMOLE_DATA_DIR` points anywhere else, permanent deletion is disabled and the active custom path is shown for manual removal.

Context Mole retries locked local-data cleanup for up to two minutes. If anything remains locked, the application uninstall still completes, the remaining data is preserved, and Windows displays the exact path with manual-removal guidance. Context Mole's start-at-sign-in entry is removed only after a successful in-app uninstall. Managed AI-client connection entries are intentionally retained so a reinstall at the same location can reconnect.

Uninstalling through Windows Installed apps or directly through Velopack keeps its existing behavior: application data is retained, and the in-app data-choice flow is not invoked.

## Development

The repository pins its .NET SDK in `global.json`. Restore, test, and run with:

```powershell
dotnet restore ContextMole.slnx --locked-mode
dotnet test --solution ContextMole.slnx -c Release --no-restore
dotnet run --project src/App.UI/ContextMole.App.UI.csproj -c Release --no-restore
```

Package versions are centralized in `Directory.Packages.props`, and lock files are committed. See [development checks](docs/DEVELOPMENT.md) and the [native smoke checklist](docs/NATIVE-SMOKE.md) for additional validation.

Publishing the UI also places the self-contained MCP adapter in its `mcp-server` directory and the shared broker in `mcp-server/broker`:

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
