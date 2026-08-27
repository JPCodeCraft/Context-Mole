# Native smoke checklists

## Every RID publish output

1. Publish the UI with `Release`, self-contained, folder-based, non-trimmed, and non-AOT settings. Confirm its `mcp-server` subfolder contains the separate self-contained `ContextMole.Mcp` executable. An independent MCP-only publish must also remain possible.
2. Verify the UI output and bundled MCP output contain the target RID's SQLite, tokenizer, Skia/HarfBuzz, PDFium, and ONNX Runtime native libraries, except for the documented `osx-x64` ONNX degradation below. Confirm no obsolete OCR executable/data, Paddle runtime, or Python files are present.
3. Verify both outputs contain `THIRD-PARTY-NOTICES.md` and `THIRD-PARTY-LICENSES`, and confirm production symbols contain only mapped source paths rather than local user directories.
4. On `win-x64`, `linux-x64`, and `osx-arm64`, set `CONTEXTMOLE_DATA_DIR` to a new private smoke directory. Start the UI, confirm the PP-OCRv6 medium detector and recognizer download automatically with visible progress, interrupt and resume the download, and verify checksum-gated activation followed by offline OCR. Exercise both Granite 311M and 97M from Settings, including 311M terms review/rejection/acceptance, cancellation/resume, checksums, switching in both directions, persisted selection, and automatic re-embedding. Skip this ONNX-dependent step on `osx-x64` and verify its documented degradation instead.
5. Start the UI, create a project over disposable fixtures, and verify create/modify/rename/delete plus pause/resume/reindex/edit/remove.
6. Confirm source hashes and modification timestamps remain unchanged.
7. Exercise every row under **AI Connections**. For automatic clients, verify only the owned `context-mole` entry changes, unrelated settings survive, an existing file receives a backup before changes, and removal deletes only that entry. For VS Code, Roo Code, and OpenCode, follow the manual instructions. Reload each available client, approve the local server, invoke all MCP tools, and confirm the database's logical contents are unchanged. Inspect the OpenAI TOML block separately because it uses a different configuration format.

## Windows x64

- Check PDF native text and PDFium-rendered scanned OCR pages.
- Check DOCX/XLSX/PPTX, all raster formats, EML/MSG, and nested attachments.
- Exercise a OneDrive placeholder that is not resident and verify it is retained/retried without hydration.
- Force-close during indexing, restart, and verify expired jobs/staging work recover.
- Verify the tray close/show flow and explicit Quit drain.

## Linux x64 glibc

- Run on an x64 glibc desktop distribution (not Alpine/musl).
- Verify executable permission on the UI, MCP, and native libraries.
- Verify PDFium, Skia, ONNX Runtime, SQLite, and tokenizer libraries load without `LD_LIBRARY_PATH` changes.
- Check tray integration. When AppIndicator is unavailable, verify close minimizes to the taskbar and indexing continues.
- Verify `$XDG_DATA_HOME` and `~/.local/share` resolution and user-only data-directory permissions.

## macOS arm64

- Test arm64 natively; do not treat Rosetta execution as the arm64 result.
- Verify executable permission on the UI, MCP, and dylibs.
- Verify PDFium, Skia, ONNX Runtime, SQLite, and tokenizer dylibs load under the app's folder layout.
- Verify `~/Library/Application Support/ContextMole` resolution, folder picker access, window hide/show, and explicit Quit.
- If packaging later adds an app bundle, repeat after signing/notarization; those release steps are outside this build.

## macOS x64

- Verify executable permission on the UI, MCP, and the available dylibs.
- ONNX Runtime 1.29.0 no longer ships an Intel macOS native library. Verify startup does not attempt ONNX initialization, indexing remains available for natively extractable text, scanned content records the explicit OCR-platform error, and `search_project` returns keyword results with an explicit semantic-unavailable warning.
- Verify PDFium, Skia, SQLite, and tokenizer dylibs load under the app's folder layout.
- Repeat the data-directory, folder-picker, hide/show, and explicit-Quit checks above.

These Linux/macOS lists are intended for execution on their native systems. A Windows workspace cannot validate their desktop integration or dynamic loader behavior.
