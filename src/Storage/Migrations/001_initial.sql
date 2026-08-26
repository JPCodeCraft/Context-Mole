CREATE TABLE projects (
    id TEXT PRIMARY KEY, name TEXT NOT NULL, name_key TEXT NOT NULL UNIQUE, state INTEGER NOT NULL,
    search_generation INTEGER NOT NULL DEFAULT 0, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL
);
CREATE TABLE project_folders (
    id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    path TEXT NOT NULL, path_key TEXT NOT NULL, created_utc TEXT NOT NULL, UNIQUE(project_id,path_key)
);
CREATE TABLE document_revisions (
    id TEXT PRIMARY KEY, document_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
    sha256 TEXT NOT NULL, status TEXT NOT NULL, embedding_policy_json TEXT NULL,
    created_utc TEXT NOT NULL, activated_utc TEXT NULL
);
CREATE TABLE documents (
    id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    folder_id TEXT NOT NULL REFERENCES project_folders(id) ON DELETE CASCADE, path TEXT NOT NULL,
    path_key TEXT NOT NULL, file_name TEXT NOT NULL, extension TEXT NOT NULL, size INTEGER NOT NULL,
    modified_utc TEXT NOT NULL, sha256 TEXT NULL, observation_epoch INTEGER NOT NULL DEFAULT 1,
    tombstoned INTEGER NOT NULL DEFAULT 0, available INTEGER NOT NULL DEFAULT 1,
    active_revision_id TEXT NULL REFERENCES document_revisions(id) ON DELETE SET NULL,
    last_seen_token TEXT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL, UNIQUE(project_id,path_key)
);
CREATE INDEX ix_documents_project_active ON documents(project_id,tombstoned,active_revision_id);
CREATE INDEX ix_documents_folder_seen ON documents(folder_id,last_seen_token,tombstoned);
CREATE TABLE content_nodes (
    id TEXT PRIMARY KEY, revision_id TEXT NOT NULL REFERENCES document_revisions(id) ON DELETE CASCADE,
    parent_id TEXT NULL REFERENCES content_nodes(id) ON DELETE CASCADE, ordinal INTEGER NOT NULL,
    name TEXT NOT NULL, mime_type TEXT NULL, relationship TEXT NOT NULL, depth INTEGER NOT NULL
);
CREATE INDEX ix_content_nodes_revision ON content_nodes(revision_id,depth,ordinal);
CREATE TABLE passages (
    rowid INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT NOT NULL UNIQUE,
    revision_id TEXT NOT NULL REFERENCES document_revisions(id) ON DELETE CASCADE,
    content_id TEXT NOT NULL REFERENCES content_nodes(id) ON DELETE CASCADE, ordinal INTEGER NOT NULL,
    display_text TEXT NOT NULL, search_text TEXT NOT NULL, location_kind INTEGER NOT NULL,
    page INTEGER NULL, sheet TEXT NULL, cell_range TEXT NULL, slide INTEGER NULL,
    structure_path TEXT NULL, email_part TEXT NULL, image_frame INTEGER NULL,
    extraction_method INTEGER NOT NULL, ocr_confidence REAL NULL, UNIQUE(content_id,ordinal)
);
CREATE INDEX ix_passages_revision ON passages(revision_id);
CREATE INDEX ix_passages_content_ordinal ON passages(content_id,ordinal);
CREATE VIRTUAL TABLE passages_fts USING fts5(
    search_text, content='passages', content_rowid='rowid', tokenize='unicode61 remove_diacritics 2'
);
CREATE TABLE embeddings (
    passage_rowid INTEGER PRIMARY KEY REFERENCES passages(rowid) ON DELETE CASCADE,
    passage_id TEXT NOT NULL, revision_id TEXT NOT NULL REFERENCES document_revisions(id) ON DELETE CASCADE,
    vector BLOB NOT NULL, policy_key TEXT NOT NULL, CHECK(length(vector)=1536)
);
CREATE INDEX ix_embeddings_revision ON embeddings(revision_id);
CREATE TABLE index_jobs (
    id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    document_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE, kind INTEGER NOT NULL,
    state TEXT NOT NULL, expected_epoch INTEGER NOT NULL, attempt INTEGER NOT NULL DEFAULT 0,
    not_before_utc TEXT NOT NULL, lease_until_utc TEXT NULL, last_error TEXT NULL,
    created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL
);
CREATE INDEX ix_jobs_ready ON index_jobs(state,not_before_utc,created_utc);
CREATE UNIQUE INDEX ux_jobs_document_open ON index_jobs(document_id) WHERE state IN ('queued','retry_wait','running');
CREATE TABLE index_runs (
    id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    document_id TEXT NULL REFERENCES documents(id) ON DELETE CASCADE, started_utc TEXT NOT NULL,
    completed_utc TEXT NULL, state TEXT NOT NULL
);
CREATE TABLE project_errors (
    id INTEGER PRIMARY KEY AUTOINCREMENT, project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    document_id TEXT NULL REFERENCES documents(id) ON DELETE CASCADE, code TEXT NOT NULL,
    message TEXT NOT NULL, retryable INTEGER NOT NULL, attempt INTEGER NOT NULL,
    source_path TEXT NULL, created_utc TEXT NOT NULL
);
CREATE INDEX ix_project_errors_project ON project_errors(project_id,created_utc DESC);
