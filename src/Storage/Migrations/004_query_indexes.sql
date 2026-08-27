CREATE INDEX ix_jobs_project_document_updated
ON index_jobs(project_id,document_id,updated_utc DESC,id DESC);

CREATE INDEX ix_errors_project_document_created
ON project_errors(project_id,document_id,created_utc DESC,id DESC);

CREATE INDEX ix_runs_project_state_completed
ON index_runs(project_id,state,completed_utc DESC);
