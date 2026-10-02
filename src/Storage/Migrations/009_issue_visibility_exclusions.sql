ALTER TABLE projects ADD COLUMN issue_generation INTEGER NOT NULL DEFAULT 0;
ALTER TABLE documents ADD COLUMN failure_source_fingerprint TEXT NULL;
ALTER TABLE project_errors ADD COLUMN root_path_key TEXT NOT NULL DEFAULT '';
ALTER TABLE project_errors ADD COLUMN component_key TEXT NOT NULL DEFAULT 'root';
ALTER TABLE project_errors ADD COLUMN component_name TEXT NULL;
ALTER TABLE project_errors ADD COLUMN source_fingerprint TEXT NOT NULL DEFAULT '';
ALTER TABLE project_errors ADD COLUMN signature TEXT NOT NULL DEFAULT '';

UPDATE documents SET failure_source_fingerprint=lower(sha256) WHERE sha256 IS NOT NULL
  AND EXISTS(SELECT 1 FROM project_errors WHERE document_id=documents.id);
UPDATE project_errors SET
  root_path_key=COALESCE((SELECT path_key FROM documents WHERE id=project_errors.document_id),source_path,''),
  component_name=(SELECT name FROM content_nodes WHERE id=project_errors.content_id),
  source_fingerprint=COALESCE((SELECT lower(sha256) FROM documents WHERE id=project_errors.document_id),
    (SELECT 'metadata:' || size || ':' || modified_utc FROM documents WHERE id=project_errors.document_id),'unknown');
WITH RECURSIVE components(id,component_key) AS (
  SELECT id,'root' FROM content_nodes WHERE parent_id IS NULL
  UNION ALL
  SELECT n.id,p.component_key || '/' || name_key(n.relationship) || char(31) || name_key(n.name) || char(31) ||
    (SELECT COUNT(*) FROM content_nodes s WHERE s.parent_id=n.parent_id AND s.ordinal<n.ordinal
      AND name_key(s.relationship)=name_key(n.relationship) AND name_key(s.name)=name_key(n.name))
  FROM content_nodes n JOIN components p ON n.parent_id=p.id
)
UPDATE project_errors SET component_key=COALESCE((SELECT component_key FROM components WHERE id=project_errors.content_id),'root');
UPDATE project_errors SET signature=
  length(root_path_key)||':'||root_path_key||length(source_fingerprint)||':'||source_fingerprint||
  length(component_key)||':'||component_key||length(code)||':'||code||
  length(CASE WHEN component_name IS NOT NULL AND instr(message,component_name||': ')=1
    THEN trim(substr(message,length(component_name)+3)) ELSE trim(message) END)||':'||
  CASE WHEN component_name IS NOT NULL AND instr(message,component_name||': ')=1
    THEN trim(substr(message,length(component_name)+3)) ELSE trim(message) END;

CREATE TABLE issue_acknowledgements (
  id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  signature TEXT NOT NULL, root_path_key TEXT NOT NULL, acknowledged_utc TEXT NOT NULL,
  UNIQUE(project_id,signature)
);
CREATE INDEX ix_acknowledgements_root ON issue_acknowledgements(project_id,root_path_key);
CREATE TABLE issue_lifecycles (
  project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE, signature TEXT NOT NULL,
  first_seen_utc TEXT NOT NULL, last_seen_utc TEXT NOT NULL, occurrence_count INTEGER NOT NULL,
  PRIMARY KEY(project_id,signature)
);
INSERT INTO issue_lifecycles SELECT project_id,signature,MIN(created_utc),MAX(created_utc),COUNT(*)
  FROM project_errors GROUP BY project_id,signature;
CREATE INDEX ix_project_errors_root_signature ON project_errors(project_id,root_path_key,signature);
CREATE TABLE file_exclusions (
  project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  path_key TEXT NOT NULL, source_path TEXT NOT NULL, excluded_utc TEXT NOT NULL,
  PRIMARY KEY(project_id,path_key)
);

CREATE TRIGGER project_issue_insert AFTER INSERT ON project_errors BEGIN
  UPDATE projects SET issue_generation=issue_generation+1 WHERE id=NEW.project_id;
END;
CREATE TRIGGER project_issue_update AFTER UPDATE ON project_errors BEGIN
  UPDATE projects SET issue_generation=issue_generation+1 WHERE id=NEW.project_id;
END;
CREATE TRIGGER project_issue_delete AFTER DELETE ON project_errors BEGIN
  UPDATE projects SET issue_generation=issue_generation+1 WHERE id=OLD.project_id;
END;
CREATE TRIGGER issue_ack_insert AFTER INSERT ON issue_acknowledgements BEGIN
  UPDATE projects SET issue_generation=issue_generation+1 WHERE id=NEW.project_id;
END;
CREATE TRIGGER issue_ack_delete AFTER DELETE ON issue_acknowledgements BEGIN
  UPDATE projects SET issue_generation=issue_generation+1 WHERE id=OLD.project_id;
END;
