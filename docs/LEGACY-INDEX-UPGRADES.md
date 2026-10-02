# Legacy index upgrades and evidence retention

Pending SQLite migrations 007 and 008 preserve an existing active revision until a successful source-backed rebuild commits. These database migrations retain project and folder records, paused state, source files, and downloaded assets. The app selects the pinned Granite Multilingual 97M model. Native UI startup separately removes only allowlisted retired 311M installer-cache files under the [bounded managed-cache cleanup policy](../README.md); this does not delete indexed evidence or source files.

## What remains available during a rebuild

- Existing document, revision, content, and passage IDs remain intact, together with exact display text, legacy search text, extraction method/confidence, old location fields, embedding policy, and vector bytes
- Passage reading, literal search excerpts, and lexical search remain available through migration and writer startup, including when the project is paused or the source is missing or unavailable
- Migration 007 adds body search text from the exact retained display text and document/content metadata from existing joins. Unknown legacy titles or headings remain empty
- Migration 008 canonicalizes the retained lexical index with the current tokenizer and supplies one exact section per retained passage. Each has the same ID as its passage, section offset zero, and kind `legacy-passage`

These legacy sections describe only the retained passage boundary. They do not claim a recovered original source span, heading hierarchy, cross-passage offset, or page geometry. Locations contain only information recorded by the older schema; a source-backed rebuild can provide richer current extraction metadata.

## Semantic compatibility and replacement

Legacy revisions receive the explicit stale preparation marker `layout-v2/spans-v2/body-context-v2`. Old vectors and policy keys remain stored, but current-model snapshot, metadata, and streaming paths require both the exact current 97M policy and the independently stored current preparation version. An embedding-only refresh cannot relabel stale extracted evidence as current preparation.

Migrations advance the observation epoch and search generation and queue a source-backed reindex. Paused projects keep their pending work without leasing it. A failed rebuild leaves the old active evidence intact. A successful replacement switches the active revision and replaces its lexical/section/vector data atomically. Failed commits roll back the whole replacement. Interrupted staging work is discarded on restart while the old active revision remains readable, and the job can be retried.

Ordinary source-deletion semantics remain in effect. An unavailable root or incomplete scan retains the last successful revision, but when a file is absent inside an available, successfully scanned root, normal reconciliation or indexing can mark it deleted and remove it from active search. Migration/startup retention does not preserve that file as active forever or override an intentional deletion.

## Recovery boundaries

These safeguards apply when migration 007 or 008 is still pending. They cannot recover evidence already deleted by an older application that applied the previous destructive migration scripts. For such an index, available source files can be rebuilt with the current application. If a source is no longer available, recovery requires a preserved source copy or an index backup from before the destructive upgrade.

If startup reports `migration_failed`, the failing migration transaction is rolled back and the last committed schema/evidence remains in place. Keep that database and its SQLite companions, resolve the reported cause, and retry startup. Do not delete the database as a migration recovery step. Missing, malformed, gapped, or newer migration histories are rejected rather than silently recreating the index. Close the application before making a filesystem backup of an index and retain any existing WAL/SHM companions with it.

## Mixed-format extraction preparation upgrade

The `layout-v5` preparation adds canonical recognized XLSX dates alongside raw
serials, explicit uncached-formula evidence, and bounded context grouping for
standalone/attached image OCR. Existing v4 sources queue source-backed
re-extraction. Current preparation differs for all formats even where PDF
extraction is unchanged; previous active evidence stays available during the
upgrade. Regression cases cover successful and failed v4→v5 replacements.

## Regression coverage

`MigrationRegressionTests` exercises representative schema 6 and 7 indexes with active evidence, attachments, old vectors, paused projects, unavailable and missing sources, exact literal excerpts, passage/section search and reading, old location fallback, foreign-key/FTS integrity, failed migration rollback and retry, repeated startup, failed atomic replacement, interrupted staging recovery (including staging child rows), superseded-evidence exclusion, and a successful source-backed replacement. Separate preparation refresh tests verify that stale preparation cannot be relabeled by embedding-only repair. These are local fixture tests; they do not claim recovery of data deleted by a previously released upgrade.
