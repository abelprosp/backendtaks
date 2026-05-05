ALTER TABLE public."Cliente"
  ADD COLUMN IF NOT EXISTS "tipo_pessoa" text,
  ADD COLUMN IF NOT EXISTS "documento" text;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conname = 'Cliente_tipo_pessoa_check'
  ) THEN
    ALTER TABLE public."Cliente"
      ADD CONSTRAINT "Cliente_tipo_pessoa_check"
      CHECK ("tipo_pessoa" IS NULL OR "tipo_pessoa" IN ('pf', 'pj'));
  END IF;
END
$$;

CREATE INDEX IF NOT EXISTS "Cliente_documento_idx" ON public."Cliente"("documento");
