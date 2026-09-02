-- =============================================================================
-- LUXUS TASK — SQL COMPLETO DO DASHBOARD (copiar e colar)
-- =============================================================================
--
-- COMO O DASHBOARD FOI FEITO
-- -----------------------------------------------------------------------------
-- O dashboard NAO e uma tabela. E uma tela de admin (/dashboard) no frontend
-- Next.js que consulta as tabelas operacionais e monta os numeros no codigo.
--
-- Quem ve: somente usuario com perfil admin. Os demais sao redirecionados
-- para /demandas.
--
-- Baseline: so entram demandas criadas a partir de 01/04/2026
--   (created_at >= 2026-04-01 00:00:00).
--
-- A tela e montada em blocos. Cada bloco chama uma API, e cada API le tabelas
-- diferentes:
--
--   Bloco na tela                         API                              Tabelas
--   -----------------------------------   ------------------------------   ------------------------------------------
--   KPIs (total, status, tempo medio      /api/dashboard/kpis              "Demanda"
--   de resolucao, sem observacao ha 7     /api/dashboard/chart
--   dias) + grafico por status
--
--   Fila / "na vez" (quem e o             /api/dashboard/user-queue        demanda_responsavel + "Demanda"
--   responsavel principal de cada
--   demanda)
--
--   Grafico por responsavel, cliente      /api/dashboard/chart             demanda_responsavel, demanda_cliente,
--   e setor                                                                demanda_setor, "User", "Cliente", "Setor"
--
--   Passagens ("passou a vez")            /api/dashboard/handoffs          demanda_evento
--                                                                          (tipo = 'responsavel_da_vez_alterado')
--
--   KPIs de subtarefa (tempo ate          /api/dashboard/subtarefa-kpis    subtarefa (concluida_em) + "Demanda"
--   concluir e ate a proxima)
--
--   Equipe online                         /api/dashboard/presence          user_presence
--
--   Preferencia: quais responsaveis       /api/dashboard/subtarefa-kpi-    user_dashboard_preference
--   entram no KPI de subtarefa            preferences
--
-- Colunas de "Demanda" que o dashboard usa:
--   status, created_at, resolvido_em, ultima_observacao_em, prioridade.
--
-- O que ESTE arquivo cria no banco:
--   1) colunas de apoio em "Demanda" e subtarefa
--   2) duas tabelas extras (presenca e preferencia)
--   3) indices
--   4) funcao rpc_dashboard_kpis()  -- KPIs principais em 1 chamada
--   5) views para outro sistema consultar os mesmos numeros
--
-- PRE-REQUISITO: o outro banco ja precisa ter as tabelas operacionais
--   "User", "Demanda", demanda_responsavel, demanda_cliente, demanda_setor,
--   subtarefa e demanda_evento. Este script NAO recria o sistema inteiro.
--
-- Como usar no outro sistema:
--   SELECT * FROM public.rpc_dashboard_kpis();
--   -- ou
--   SELECT * FROM public.v_dashboard_kpis;
-- =============================================================================


-- =============================================================================
-- 1) Colunas de tempo na demanda
--    resolvido_em        = quando a demanda foi concluida (status = concluido)
--    ultima_observacao_em = ultima vez que alguem registrou observacao
--    Usadas para: tempo medio de resolucao e "demandas sem atualizacao ha 7 dias"
-- =============================================================================
ALTER TABLE public."Demanda"
  ADD COLUMN IF NOT EXISTS resolvido_em TIMESTAMP(3),
  ADD COLUMN IF NOT EXISTS ultima_observacao_em TIMESTAMP(3);

COMMENT ON COLUMN public."Demanda".resolvido_em IS
  'Data/hora em que a demanda foi concluida (status = concluido). Usado no KPI de tempo medio de resolucao do dashboard.';
COMMENT ON COLUMN public."Demanda".ultima_observacao_em IS
  'Data/hora da ultima observacao da demanda. Usado no KPI de demandas sem atualizacao recente (fila ativa, 7 dias).';


-- =============================================================================
-- 2) Colunas de conclusao da subtarefa
--    concluida_em = quando a subtarefa foi marcada como concluida
--    Usada para: tempo desde a criacao da demanda ate concluir a subtarefa,
--    e intervalo ate a proxima subtarefa da mesma demanda.
-- =============================================================================
ALTER TABLE public.subtarefa
  ADD COLUMN IF NOT EXISTS concluida_em TIMESTAMP(3),
  ADD COLUMN IF NOT EXISTS created_at TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP;

COMMENT ON COLUMN public.subtarefa.concluida_em IS
  'Data/hora em que a subtarefa foi concluida. Usado nos KPIs de subtarefa do dashboard.';


-- =============================================================================
-- 3) Presenca (bloco "Equipe ativa" do dashboard)
--    O frontend faz heartbeat em /api/presence/heartbeat.
--    Online = last_seen_at nos ultimos 2 minutos.
-- =============================================================================
CREATE TABLE IF NOT EXISTS public.user_presence (
  user_id uuid PRIMARY KEY REFERENCES public."User"(id) ON DELETE CASCADE,
  status text NOT NULL DEFAULT 'online',
  pathname text,
  page_label text,
  activity text,
  last_seen_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE public.user_presence IS
  'Presenca da equipe no dashboard: ultima tela, atividade e last_seen_at. Online = visto ha menos de 2 minutos.';


-- =============================================================================
-- 4) Preferencia do admin
--    Quais responsaveis entram nos KPIs de subtarefa (filtro persistido por usuario).
-- =============================================================================
CREATE TABLE IF NOT EXISTS public.user_dashboard_preference (
  user_id uuid PRIMARY KEY REFERENCES public."User"(id) ON DELETE CASCADE,
  subtarefa_kpi_responsavel_ids jsonb NOT NULL DEFAULT '[]'::jsonb,
  updated_at timestamp(3) NOT NULL DEFAULT CURRENT_TIMESTAMP
);

COMMENT ON TABLE public.user_dashboard_preference IS
  'Preferencia por usuario admin: lista de user_id que entram no ranking de subtarefas do dashboard.';


-- =============================================================================
-- 5) Indices usados pelas consultas do dashboard
-- =============================================================================
CREATE INDEX IF NOT EXISTS demanda_created_at_idx
  ON public."Demanda" (created_at);
CREATE INDEX IF NOT EXISTS demanda_status_idx
  ON public."Demanda" (status);
CREATE INDEX IF NOT EXISTS demanda_ultima_observacao_em_idx
  ON public."Demanda" (ultima_observacao_em);
CREATE INDEX IF NOT EXISTS demanda_resolvido_em_idx
  ON public."Demanda" (resolvido_em);

CREATE INDEX IF NOT EXISTS demanda_evento_demanda_id_idx
  ON public.demanda_evento (demanda_id);
CREATE INDEX IF NOT EXISTS demanda_evento_created_at_idx
  ON public.demanda_evento (created_at);
CREATE INDEX IF NOT EXISTS demanda_evento_tipo_created_at_idx
  ON public.demanda_evento (tipo, created_at);

CREATE INDEX IF NOT EXISTS subtarefa_demanda_id_idx
  ON public.subtarefa (demanda_id);
CREATE INDEX IF NOT EXISTS subtarefa_concluida_em_idx
  ON public.subtarefa (concluida_em)
  WHERE concluida_em IS NOT NULL;
CREATE INDEX IF NOT EXISTS subtarefa_responsavel_user_id_idx
  ON public.subtarefa (responsavel_user_id)
  WHERE responsavel_user_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS user_presence_last_seen_at_idx
  ON public.user_presence (last_seen_at DESC);
CREATE INDEX IF NOT EXISTS user_dashboard_preference_updated_at_idx
  ON public.user_dashboard_preference (updated_at);


-- =============================================================================
-- 6) Funcao RPC dos KPIs principais
--    Retorna 1 linha com:
--      total_demandas
--      concluidas
--      em_aberto
--      tempo_medio_resolucao_horas     (so demandas concluidas com resolvido_em)
--      demandas_sem_observacao_recente (fila ativa: aberto/andamento/standby,
--                                       sem observacao ou observacao > 7 dias)
--      tempo_medio_desde_ultima_observacao_horas (mesma fila ativa)
--      por_status                      (jsonb: { em_aberto: n, ... })
--    Baseline: created_at >= 2026-04-01
-- =============================================================================
CREATE OR REPLACE FUNCTION public.rpc_dashboard_kpis()
RETURNS TABLE (
  total_demandas integer,
  concluidas integer,
  em_aberto integer,
  tempo_medio_resolucao_horas numeric,
  demandas_sem_observacao_recente integer,
  tempo_medio_desde_ultima_observacao_horas numeric,
  por_status jsonb
)
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = public
AS $$
  WITH base AS (
    SELECT d.id, d.status, d.created_at, d.resolvido_em, d.ultima_observacao_em
    FROM public."Demanda" d
    WHERE d.created_at >= TIMESTAMP '2026-04-01 00:00:00'
  ),
  status_counts AS (
    SELECT status, count(*)::int AS total
    FROM base
    GROUP BY status
  ),
  active_base AS (
    SELECT *
    FROM base
    WHERE status IN ('em_aberto', 'em_andamento', 'standby')
  )
  SELECT
    count(*)::int AS total_demandas,
    count(*) FILTER (WHERE status = 'concluido')::int AS concluidas,
    count(*) FILTER (WHERE status = 'em_aberto')::int AS em_aberto,
    round(avg(
      CASE
        WHEN status = 'concluido' AND resolvido_em IS NOT NULL
          THEN extract(epoch FROM (resolvido_em - created_at)) / 3600.0
        ELSE NULL
      END
    )::numeric, 1) AS tempo_medio_resolucao_horas,
    (
      SELECT count(*)::int
      FROM active_base
      WHERE ultima_observacao_em IS NULL
         OR now() - ultima_observacao_em > interval '7 days'
    ) AS demandas_sem_observacao_recente,
    round(avg(
      CASE
        WHEN status IN ('em_aberto', 'em_andamento', 'standby')
         AND ultima_observacao_em IS NOT NULL
          THEN extract(epoch FROM (now() - ultima_observacao_em)) / 3600.0
        ELSE NULL
      END
    )::numeric, 1) AS tempo_medio_desde_ultima_observacao_horas,
    coalesce((SELECT jsonb_object_agg(status, total) FROM status_counts), '{}'::jsonb) AS por_status
  FROM base;
$$;

GRANT EXECUTE ON FUNCTION public.rpc_dashboard_kpis() TO authenticated, service_role;


-- =============================================================================
-- 7) Views para outro sistema (Power BI, Metabase, outro app, SQL direto)
--    Espelham o que o dashboard calcula no TypeScript.
-- =============================================================================

-- 7.1 KPIs principais (1 linha) — mesmo retorno de rpc_dashboard_kpis()
CREATE OR REPLACE VIEW public.v_dashboard_kpis AS
SELECT * FROM public.rpc_dashboard_kpis();

COMMENT ON VIEW public.v_dashboard_kpis IS
  'KPIs principais do dashboard Luxus Task (baseline 01/04/2026). Equivale a SELECT * FROM rpc_dashboard_kpis().';


-- 7.2 Fila: quem esta "na vez" (is_principal = true) vs so participa da demanda
CREATE OR REPLACE VIEW public.v_dashboard_fila AS
SELECT
  u.id AS user_id,
  u.name,
  u.email,
  count(*) FILTER (WHERE dr.is_principal) AS total_na_vez,
  count(*) AS total_participacoes,
  count(*) FILTER (WHERE dr.is_principal AND d.status = 'em_aberto') AS na_vez_em_aberto,
  count(*) FILTER (WHERE dr.is_principal AND d.status = 'em_andamento') AS na_vez_em_andamento,
  count(*) FILTER (WHERE dr.is_principal AND d.status = 'standby') AS na_vez_standby,
  count(*) FILTER (WHERE dr.is_principal AND d.status = 'concluido') AS na_vez_concluido
FROM public.demanda_responsavel dr
JOIN public."Demanda" d ON d.id = dr.demanda_id
JOIN public."User" u ON u.id = dr.user_id
WHERE d.created_at >= TIMESTAMP '2026-04-01 00:00:00'
  AND u.active = true
GROUP BY u.id, u.name, u.email;

COMMENT ON VIEW public.v_dashboard_fila IS
  'Fila do dashboard: total_na_vez = demandas em que o usuario e o responsavel principal (da vez).';


-- 7.3 Passagens de vez (quando alguem "passa a demanda adiante")
--     Evento gravado em demanda_evento.tipo = 'responsavel_da_vez_alterado'
CREATE OR REPLACE VIEW public.v_dashboard_passagens AS
SELECT
  e.user_id,
  u.name,
  count(*) AS total_passadas,
  count(DISTINCT e.demanda_id) AS demandas_distintas
FROM public.demanda_evento e
LEFT JOIN public."User" u ON u.id = e.user_id
WHERE e.tipo = 'responsavel_da_vez_alterado'
  AND e.created_at >= TIMESTAMP '2026-04-01 00:00:00'
GROUP BY e.user_id, u.name;

COMMENT ON VIEW public.v_dashboard_passagens IS
  'Ranking de passagens de vez do dashboard (demanda_evento.tipo = responsavel_da_vez_alterado).';


-- 7.4 Demandas que mais foram passadas adiante
CREATE OR REPLACE VIEW public.v_dashboard_demandas_mais_passadas AS
SELECT
  d.id AS demanda_id,
  d.protocolo,
  d.assunto,
  count(*) AS total_passadas
FROM public.demanda_evento e
JOIN public."Demanda" d ON d.id = e.demanda_id
WHERE e.tipo = 'responsavel_da_vez_alterado'
  AND e.created_at >= TIMESTAMP '2026-04-01 00:00:00'
GROUP BY d.id, d.protocolo, d.assunto
ORDER BY total_passadas DESC;

COMMENT ON VIEW public.v_dashboard_demandas_mais_passadas IS
  'Demandas com mais passagens de vez no periodo do dashboard (baseline 01/04/2026).';


-- 7.5 Subtarefas concluidas com tempo desde a criacao da demanda
CREATE OR REPLACE VIEW public.v_dashboard_subtarefas AS
SELECT
  s.id AS subtarefa_id,
  s.titulo,
  s.ordem,
  s.concluida_em,
  s.responsavel_user_id,
  u.name AS responsavel_name,
  d.id AS demanda_id,
  d.protocolo,
  d.assunto,
  d.status AS demanda_status,
  d.created_at AS demanda_created_at,
  round(extract(epoch FROM (s.concluida_em - d.created_at)) / 3600.0, 1)
    AS tempo_desde_criacao_horas
FROM public.subtarefa s
JOIN public."Demanda" d ON d.id = s.demanda_id
LEFT JOIN public."User" u ON u.id = s.responsavel_user_id
WHERE s.concluida = true
  AND s.concluida_em IS NOT NULL
  AND d.created_at >= TIMESTAMP '2026-04-01 00:00:00';

COMMENT ON VIEW public.v_dashboard_subtarefas IS
  'Subtarefas concluidas do dashboard, com horas desde a criacao da demanda.';


-- =============================================================================
-- CONSULTAS RAPIDAS (exemplos — descomente se quiser testar depois de aplicar)
-- =============================================================================
-- SELECT * FROM public.rpc_dashboard_kpis();
-- SELECT * FROM public.v_dashboard_kpis;
-- SELECT * FROM public.v_dashboard_fila ORDER BY total_na_vez DESC;
-- SELECT * FROM public.v_dashboard_passagens ORDER BY total_passadas DESC;
-- SELECT * FROM public.v_dashboard_demandas_mais_passadas LIMIT 10;
-- SELECT * FROM public.v_dashboard_subtarefas ORDER BY concluida_em DESC LIMIT 50;
