-- Preferências do admin: quais responsáveis entram nos KPIs de subtarefas.

CREATE TABLE IF NOT EXISTS public.user_dashboard_preference (
  user_id UUID PRIMARY KEY REFERENCES public."User"(id) ON DELETE CASCADE,
  subtarefa_kpi_responsavel_ids JSONB NOT NULL DEFAULT '[]'::jsonb,
  updated_at TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS user_dashboard_preference_updated_at_idx
  ON public.user_dashboard_preference(updated_at);
