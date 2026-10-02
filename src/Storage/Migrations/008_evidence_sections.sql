-- Retain existing evidence until a successful source-backed replacement. Old preparation
-- remains explicitly stale, so its vectors cannot enter the current-model search path.
UPDATE projects SET search_generation=search_generation+1;
UPDATE documents SET observation_epoch=observation_epoch+1
WHERE tombstoned=0;
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
-- Older schemas only know passage boundaries. Represent each exact retained passage as
-- its own section; never fabricate cross-passage offsets or source geometry. This matches
-- the normal writer's one-passage fallback while keeping coverage explicitly identifiable.
INSERT INTO sections(id,revision_id,content_id,ordinal,display_text,heading,heading_path_json,kind,location_json)
SELECT id,revision_id,content_id,ordinal,display_text,heading,'[]','legacy-passage',
       json_object('Kind',location_kind,'Page',page,'Sheet',sheet,'CellRange',cell_range,
                   'Slide',slide,'StructurePath',structure_path,'EmailPart',email_part,'ImageFrame',image_frame)
FROM passages;
UPDATE passages SET section_id=id,section_offset=0;
-- Only the exact active revision belongs in FTS. Interrupted staging rows are removed by
-- startup recovery; superseded/tombstoned evidence stays stored but must not be reindexed.
INSERT INTO passages_fts(rowid,body_text,title,heading,filename,path,content_name,sheet,email_subject)
SELECT p.rowid,lexical(p.body_text),lexical(p.title),lexical(p.heading),lexical(p.filename),lexical(p.path),
       lexical(p.content_name),lexical(p.sheet),lexical(p.email_subject)
FROM passages p
JOIN document_revisions r ON r.id=p.revision_id AND r.status='active'
JOIN documents d ON d.id=r.document_id AND d.active_revision_id=r.id AND d.tombstoned=0;
INSERT INTO sections_fts(rowid,body_text,title,heading,filename,path,content_name,sheet,email_subject)
SELECT s.rowid,lexical(s.display_text),lexical(p.title),lexical(s.heading),lexical(p.filename),
       lexical(p.path),lexical(p.content_name),lexical(p.sheet),lexical(p.email_subject)
FROM sections s JOIN passages p ON p.id=s.id
JOIN document_revisions r ON r.id=s.revision_id AND r.status='active'
JOIN documents d ON d.id=r.document_id AND d.active_revision_id=r.id AND d.tombstoned=0;
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
