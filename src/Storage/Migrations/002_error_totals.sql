ALTER TABLE projects ADD COLUMN error_total INTEGER NOT NULL DEFAULT 0;
UPDATE projects SET error_total=(SELECT COUNT(*) FROM project_errors WHERE project_id=projects.id);
