ALTER TABLE public.luxus_parceiros_demanda
  ADD COLUMN IF NOT EXISTS turn_request_from text,
  ADD COLUMN IF NOT EXISTS turn_request_reason text,
  ADD COLUMN IF NOT EXISTS turn_request_at timestamptz;
