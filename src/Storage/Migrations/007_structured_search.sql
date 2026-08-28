ALTER TABLE passages ADD COLUMN body_text TEXT NOT NULL DEFAULT '';
ALTER TABLE passages ADD COLUMN title TEXT NOT NULL DEFAULT '';
ALTER TABLE passages ADD COLUMN heading TEXT NOT NULL DEFAULT '';
ALTER TABLE passages ADD COLUMN filename TEXT NOT NULL DEFAULT '';
ALTER TABLE passages ADD COLUMN path TEXT NOT NULL DEFAULT '';
ALTER TABLE passages ADD COLUMN content_name TEXT NOT NULL DEFAULT '';
ALTER TABLE passages ADD COLUMN email_subject TEXT NOT NULL DEFAULT '';

DROP TABLE passages_fts;
CREATE VIRTUAL TABLE passages_fts USING fts5(
    body_text, title, heading, filename, path, content_name, sheet, email_subject,
    content='passages', content_rowid='rowid', tokenize='unicode61 remove_diacritics 2'
);

-- v0.2 deliberately starts with a fresh search index. Legacy passages cannot provide the
-- structural metadata or deterministic public IDs promised by the structured API, so never
-- expose them while background indexing catches up. Project/folder settings and source files
-- remain untouched; only application-owned derived revisions are discarded and rebuilt.
UPDATE projects
SET search_generation=search_generation+1,
    updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now');
UPDATE documents
SET active_revision_id=NULL,sha256=NULL,observation_epoch=observation_epoch+1,
    updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
WHERE tombstoned=0;
DELETE FROM document_revisions;
UPDATE index_jobs
SET kind=1,state='queued',expected_epoch=(SELECT d.observation_epoch FROM documents d WHERE d.id=index_jobs.document_id),
    attempt=0,not_before_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now'),lease_until_utc=NULL,last_error=NULL,
    target_policy_key=NULL,updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
WHERE document_id IN (
  SELECT d.id FROM documents d WHERE d.tombstoned=0
) AND state IN ('queued','retry_wait','running');
INSERT INTO index_jobs(id,project_id,document_id,kind,state,expected_epoch,attempt,not_before_utc,
                       lease_until_utc,last_error,created_utc,updated_utc,target_policy_key)
SELECT lower(hex(randomblob(16))),d.project_id,d.id,1,'queued',d.observation_epoch,0,
       strftime('%Y-%m-%dT%H:%M:%fZ','now'),NULL,NULL,
       strftime('%Y-%m-%dT%H:%M:%fZ','now'),strftime('%Y-%m-%dT%H:%M:%fZ','now'),NULL
FROM documents d
WHERE d.tombstoned=0
  AND NOT EXISTS(
    SELECT 1 FROM index_jobs j WHERE j.document_id=d.id AND j.state IN ('queued','retry_wait','running')
  );
