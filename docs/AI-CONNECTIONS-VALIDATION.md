# AI connection safety fixes and validation

Validated on Linux on 2 October 2026. This change fixes the reviewed client-configuration safety issues in the delivered source checkout. It does not certify conversations inside third-party AI clients or broker-backed searches.

## Changes

- OpenAI TOML configuration now uses Tomlyn 2.10.1 for syntax and semantic validation. Valid inline, dotted, quoted, and spaced key forms are recognized. Ownership markers must be actual whole-line comments with one unambiguous range. Marker text inside strings cannot claim ownership. Malformed or ambiguous files remain unchanged.
- OpenAI updates patch supported launch values by source span. Custom arguments, environment values, tool policies, comments, unrelated text, and already-correct literal/escaped paths survive. Removing a proven-owned block preserves the remainder. Exotic arrangements that cannot be safely edited return Conflict without normalizing the file.
- JSON clients reject deliberately disabled entries, incompatible/ambiguous transports, non-string environment values, duplicate properties at any depth, and invalid server maps. Relative owned paths require repair. Valid Copilot local/stdio aliases are accepted.
- JSON updates preserve custom environment values, timeouts, approvals, tool restrictions, and other custom fields. Context Mole owns its command, empty argument list, required local transport defaults, data-directory value, and management marker. Restricted Copilot tool lists are preserved rather than expanded.
- On Linux/macOS, safe saves use supported atomic exchange operations to retain the actual displaced file in a recovery backup. New files use atomic no-replace creation. Windows uses the operating system's replacement-with-backup operation. Cancellation stops before commit; after commit, verification is completed so cancellation cannot conceal a conflict.
- If a competing save is detected after commit, both the competing contents and, when possible, the previously read text are retained. The error names recovery paths and warns that Context Mole's update may already be active. There is no blind rollback that could destroy an even newer save.

## Final verification

- Locked solution restore passed with the pinned .NET 10.0.400 SDK
- Integrated Release build passed with zero warnings and zero errors
- All 126 focused AI connection, status, TOML, JSON, and safe-save tests passed
- All 775 permitted aggregate tests passed on the final build
- The broader aggregate attempt encountered 15 broker/embedding IPC tests blocked by SocketException Permission denied. Those exact calls were excluded from the subsequent permitted run and were not retried. Seven Windows MCP desktop tests were not run
- The new regression coverage consists of 39 TOML cases, 53 JSON cases, and 10 safe-save cases. The existing malformed-TOML expectation was changed to fail closed
- Independent review exercised 19 additional TOML edge cases and verified the relative-path correction; no remaining blocking finding was identified
- All 12 Linux-available automatic client configurations passed configure, repeated configure, status, owned-only remove, repeated remove, reconnect, and pre-cancellation preservation in disposable profiles
- The actual final MCP process initialized over stdio, advertised all nine tools with object input schemas, kept stdout protocol-only, exited successfully, and released its lifecycle lease
- Whole-operation final-read races at 1 MiB and 16 MiB retained the actual competing save plus expected-original recovery text, reported Conflict, and left no partial files

## Native UI verification

The final Linux app ran with disposable client homes and data only. No home PC or actual user-client settings were accessed.

- A user-owned inline OpenAI entry and a disabled managed Cursor entry appeared as Conflict and retained their exact original hashes without backups or edits
- Cursor Update repaired the launch path/data directory while preserving custom environment values, disabledTools, autoApprove, timeout, disabled:false, and an unrelated server
- OpenAI Configure preserved string-literal marker contents and added an actual root MCP entry outside the string
- Owned-only OpenAI removal preserved the original string contents
- Projects to Settings navigation retained the correct configuration state
- Double-click Configure produced one registration and one confirmation, with no unintended removal
- The previous Development build label remained correct
- The disposable app was closed and lifecycle leases were released

The test fixture deliberately blocked model downloads through an unusable proxy. Its OCR download warning was expected and unrelated to these configuration fixes.

## Remaining limits

- Windows and macOS native replacement branches are implemented but unexecuted here. Their native smoke checks remain necessary before a platform release. Windows behavior is bounded by its OS replacement-with-backup API rather than a claimed universal atomic content guarantee
- No implementation can provide a cross-process content compare-and-swap guarantee against uncoordinated writers here. A late conflicting update may already be active; recovery contents are retained and the conflict is surfaced. Arbitrary later in-place writers remain outside this protection
- Unsupported atomic exchange/no-replace operations fail safely rather than falling back to unsafe overwrites
- Deliberately disabled entries are not silently enabled. The user must explicitly enable them in the client configuration before updating
- Broker-backed tool calls, shared-broker concurrency, and authenticated third-party-client end-to-end behavior remain unverified because of the IPC restriction and the absence of authorized real-client sessions
- Claude Desktop was unavailable on Linux. VS Code, Roo Code, and OpenCode remain manual setup rows
- A Linux Avalonia DBus tray cancellation exception was observed during application shutdown; the UI closed and leases were released. It is separate from the connection fixes

## Reproduction

From the repository with the pinned SDK installed:

```bash
dotnet restore ContextMole.slnx --locked-mode --disable-parallel -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false
dotnet build tests/ContextMole.Tests/ContextMole.Tests.csproj -c Release --no-restore -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false
dotnet tests/ContextMole.Tests/bin/Release/net10.0/ContextMole.Tests.dll --filter-class ContextMole.Tests.AiConnectionTests ContextMole.Tests.CodexConfigurationTests ContextMole.Tests.CodexTomlSafetyTests ContextMole.Tests.JsonMcpConfigurationRegressionTests ContextMole.Tests.SafeConfigurationFileTests ContextMole.Tests.AppStatusTests --no-ansi --progress off
```

The delivery's validation folder contains the exact blocked-case list, permitted aggregate filter, final build/test summaries, MCP discovery result, and race results. Run native platform checks in docs/NATIVE-SMOKE.md before releasing that platform.
