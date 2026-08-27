CREATE INDEX ix_documents_project_active_extension
ON documents(project_id,tombstoned,extension);
