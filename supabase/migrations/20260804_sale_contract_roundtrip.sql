ALTER TABLE public.luxus_parceiros_demanda
  ADD COLUMN IF NOT EXISTS entity_type text NOT NULL DEFAULT 'request',
  ADD COLUMN IF NOT EXISTS workflow_stage text NOT NULL DEFAULT 'TASK_PROCESSING',
  ADD COLUMN IF NOT EXISTS source_attachment_ids jsonb NOT NULL DEFAULT '[]'::jsonb;

CREATE INDEX IF NOT EXISTS luxus_parceiros_demanda_workflow_stage_idx
  ON public.luxus_parceiros_demanda (workflow_stage);
