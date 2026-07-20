-- Rastreamento de quando cada subtarefa foi concluída (KPIs de tempo no dashboard).

ALTER TABLE public.subtarefa
  ADD COLUMN IF NOT EXISTS concluida_em TIMESTAMP(3);

ALTER TABLE public.subtarefa
  ADD COLUMN IF NOT EXISTS created_at TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP;

CREATE INDEX IF NOT EXISTS subtarefa_concluida_em_idx
  ON public.subtarefa (concluida_em)
  WHERE concluida_em IS NOT NULL;

CREATE INDEX IF NOT EXISTS subtarefa_responsavel_user_id_idx
  ON public.subtarefa (responsavel_user_id)
  WHERE responsavel_user_id IS NOT NULL;

-- Demandas já concluídas: subtarefas ainda abertas são só layout; não recebem concluida_em.
-- Subtarefas já marcadas como concluídas antes desta migração: usa updated_at da demanda como estimativa.
UPDATE public.subtarefa s
SET concluida_em = d.updated_at
FROM public."Demanda" d
WHERE s.demanda_id = d.id
  AND s.concluida = true
  AND s.concluida_em IS NULL
  AND d.created_at >= '2026-04-01';
