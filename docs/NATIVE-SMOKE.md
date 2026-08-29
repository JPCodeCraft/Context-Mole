# Native smoke checklists

## Every RID publish output

1. Publish the UI with `Release`, self-contained, folder-based, non-trimmed, and non-AOT settings. Confirm its `mcp-server` subfolder contains the self-contained `ContextMole.Mcp` adapter, `mcp-server/broker` contains the shared `ContextMole.Broker` executable, and its `uninstall-helper` subfolder contains the single-file Windows helper on `win-x64`. An independent MCP-only publish must also remain possible and include its broker subfolder.
2. Verify the UI output and shared broker bundle contain the target RID's required SQLite, tokenizer, Skia/HarfBuzz, PDFium, and ONNX Runtime native libraries, except for the documented `osx-x64` ONNX degradation below. The thin MCP adapter must not duplicate the broker, ONNX, document, search, or storage runtime graph at its root; the single broker executable and its native dependencies belong only under `mcp-server/broker`. Confirm no external `.pdb`, obsolete OCR executable/data, Paddle runtime, or Python files are present.
3. Verify both outputs contain `THIRD-PARTY-NOTICES.md` and `THIRD-PARTY-LICENSES`, and confirm production symbols contain only mapped source paths rather than local user directories.
4. On `win-x64`, `linux-x64`, and `osx-arm64`, set `CONTEXTMOLE_DATA_DIR` to a new private smoke directory. Start the UI, confirm the PP-OCRv6 medium detector and recognizer download automatically with visible progress, interrupt and resume the download, and verify checksum-gated activation followed by offline OCR. Exercise both Granite 311M and 97M from Settings, including 311M terms review/rejection/acceptance, cancellation/resume, checksums, switching in both directions, persisted selection, and automatic re-embedding. Skip this ONNX-dependent step on `osx-x64` and verify its documented degradation instead.
5. Start the UI, create a project over disposable fixtures, and verify create/modify/rename/delete plus pause/resume/reindex/edit/remove.
6. With a CPU profile that permits at least two workers, queue several PDF/image/Office/email/archive fixtures and verify document extraction reaches the profile's CPU-count-derived parallel limit. On supported ONNX targets, let several files reach OCR and verify OCR itself remains serialized, temporarily receives the full thread count allowed by the profile, and every file completes without deadlock. Confirm the UI reports only processor-capacity waits, with no memory-admission or exclusive-file messages.
7. Confirm source hashes and modification timestamps remain unchanged.
8. Exercise every row under **AI Connections**. For automatic clients, verify only the owned `context-mole` entry changes, unrelated settings survive, an existing file receives a backup before changes, and removal deletes only that entry. For VS Code, Roo Code, and OpenCode, follow the manual instructions. Reload several clients concurrently, approve each local adapter, invoke all MCP tools, and confirm they share exactly one broker process and one Granite model session while the database's logical contents remain unchanged. Verify the broker unloads model/cache state after two idle minutes and exits after ten idle minutes. Inspect the OpenAI TOML block separately because it uses a different configuration format.

## Windows x64

- Check PDF native text and PDFium-rendered scanned OCR pages.
- Check DOCX/XLSX/PPTX, all raster formats, EML/MSG, and nested attachments.
- Exercise a OneDrive placeholder that is not resident and verify it is retained/retried without hydration.
- Force-close during indexing, restart, and verify expired jobs/staging work recover.
- In an installed build, exercise the registered start-at-sign-in command (or a fresh sign-in), show the window from the tray more than once, and verify every show remains visible. Verify close hides the window without stopping indexing, a later tray show restores it, and explicit Quit drains the process.
- In an installed build, confirm Settings shows **Uninstall Context Mole…**. From an unpackaged or portable Windows publish, confirm the same control is disabled with an installed-build explanation. Confirm **Keep data** is selected initially, the permanent-delete option names every local-data category, the irreversible warning appears only when deletion is selected, the destructive action is not the Enter-key default, and the dialog says indexed source files are never deleted. Cancel and close the dialog and verify both leave the app and data untouched.
- Start with `CONTEXTMOLE_DATA_DIR` pointing to a disposable custom path and verify the delete option is disabled while normal uninstall remains available.
- On a disposable Windows user profile or VM, keep copies and hashes of indexed source fixtures outside `%LOCALAPPDATA%\ContextMole`. Exercise in-app uninstall once with Keep and once with Delete. Verify polling, watchers, indexing/model/database/log work drain; the UI and every MCP sidecar release their process leases; an AI client cannot respawn the MCP sidecar while the shutdown marker is active; and the Velopack uninstaller remains interactive. Verify the startup Run entry is removed only after success, Keep preserves all application data and its saved preference, Delete removes only `%LOCALAPPDATA%\ContextMole`, and every source fixture remains byte-for-byte unchanged.
- During both in-app flows, verify the helper runs from a unique temporary directory, waits for the exact initiating UI process, waits for Velopack to finish, and removes its temporary directory after exit. Exercise a fake or deliberately unavailable Velopack launcher in a disposable test install and verify failure is reported without deleting data or removing the startup entry.
- Reinstall, create local data, then uninstall through Windows Installed apps. Verify this normal Velopack path keeps `%LOCALAPPDATA%\ContextMole` and does not invoke Context Mole's data-choice flow.
- Confirm the stable-tag workflow's packaged uninstall smoke passes. It installs the generated Setup executable on a disposable hosted profile, verifies ordinary Velopack uninstall retains canonical data, then invokes the packaged in-app helper and verifies Delete removes only canonical data while an outside source fixture remains byte-for-byte unchanged.
- Hold an application-data file and a process lease open during a Delete run. Verify cleanup retries with backoff for two minutes, completes the application uninstall, preserves any leftovers, and shows the exact manual-removal path instead of terminating an unrelated process or touching a source directory. Include a junction or symbolic link fixture inside the disposable data directory and verify cleanup never follows it outside the canonical root.
- Sign in to a second interactive or RDP session as the same Windows user. While an in-app Delete is removing `%LOCALAPPDATA%\ContextMole` in the first session, repeatedly start an MCP client in the second session with the same data path. Verify the global per-user uninstall gate rejects every launch without recreating the data directory, including after the in-data shutdown marker disappears; after the helper exits, verify the gate is released (a reinstall can acquire a normal UI/MCP lease again).

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
