-- Rebuild only derived data. Source files, project configuration and downloaded models are retained.
UPDATE projects SET search_generation=search_generation+1;
UPDATE documents SET active_revision_id=NULL,sha256=NULL,observation_epoch=observation_epoch+1
WHERE tombstoned=0;
DELETE FROM document_revisions;
DROP TABLE passages_fts;
CREATE VIRTUAL TABLE passages_fts USING fts5(
    body_text,title,heading,filename,path,content_name,sheet,email_subject,
    tokenize='ascii tokenchars _'
);
ALTER TABLE passages ADD COLUMN section_id TEXT NULL;
ALTER TABLE passages ADD COLUMN section_offset INTEGER NOT NULL DEFAULT 0;
ALTER TABLE passages ADD COLUMN location_json TEXT NOT NULL DEFAULT '';
ALTER TABLE passages ADD COLUMN semantic_eligible INTEGER NOT NULL DEFAULT 1;
ALTER TABLE document_revisions ADD COLUMN preparation_version TEXT NOT NULL DEFAULT 'layout-v2/spans-v2/body-context-v2';
ALTER TABLE project_errors ADD COLUMN content_id TEXT NULL;
CREATE TABLE sections (
    rowid INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT NOT NULL UNIQUE,
    revision_id TEXT NOT NULL REFERENCES document_revisions(id) ON DELETE CASCADE,
    content_id TEXT NOT NULL REFERENCES content_nodes(id) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL,display_text TEXT NOT NULL,heading TEXT NOT NULL,
    heading_path_json TEXT NOT NULL,kind TEXT NOT NULL,location_json TEXT NOT NULL,
    UNIQUE(content_id,ordinal)
);
CREATE INDEX ix_sections_revision ON sections(revision_id);
CREATE INDEX ix_passages_section ON passages(section_id,ordinal);
CREATE VIRTUAL TABLE sections_fts USING fts5(
    body_text,title,heading,filename,path,content_name,sheet,email_subject,
    tokenize='ascii tokenchars _'
);
UPDATE index_jobs SET kind=1,state='queued',
 expected_epoch=(SELECT observation_epoch FROM documents WHERE id=index_jobs.document_id),
 attempt=0,not_before_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now'),lease_until_utc=NULL,last_error=NULL,
 target_policy_key=NULL
WHERE state IN ('queued','retry_wait','running') AND document_id IN (SELECT id FROM documents WHERE tombstoned=0);
INSERT INTO index_jobs(id,project_id,document_id,kind,state,expected_epoch,attempt,not_before_utc,
 lease_until_utc,last_error,created_utc,updated_utc,target_policy_key)
SELECT lower(hex(randomblob(16))),project_id,id,1,'queued',observation_epoch,0,
 strftime('%Y-%m-%dT%H:%M:%fZ','now'),NULL,NULL,
 strftime('%Y-%m-%dT%H:%M:%fZ','now'),strftime('%Y-%m-%dT%H:%M:%fZ','now'),NULL
FROM documents d WHERE tombstoned=0 AND NOT EXISTS(
 SELECT 1 FROM index_jobs j WHERE j.document_id=d.id AND j.state IN ('queued','retry_wait','running'));
