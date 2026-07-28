CREATE TABLE IF NOT EXISTS public.luxus_parceiros_demanda (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  demanda_id uuid NOT NULL UNIQUE REFERENCES public."Demanda"(id) ON DELETE CASCADE,
  external_request_id uuid NOT NULL UNIQUE,
  external_protocol text NOT NULL,
  last_callback_at timestamptz,
  last_callback_error text,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS luxus_parceiros_demanda_external_protocol_idx
  ON public.luxus_parceiros_demanda (external_protocol);

ALTER TABLE public.luxus_parceiros_demanda ENABLE ROW LEVEL SECURITY;

REVOKE ALL ON TABLE public.luxus_parceiros_demanda FROM anon, authenticated;
