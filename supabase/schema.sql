-- ============================================================
-- LUXUS DEMANDAS - Schema SQL para Supabase
-- Cole este conteúdo no SQL Editor do Supabase (Dashboard > SQL Editor)
-- e execute. Depois rode o seed (seed.sql) se quiser setores e perfis.
-- ============================================================

-- Enums (cria só se não existir, para poder rodar o script de novo sem erro)
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'RoleSlug') THEN
    CREATE TYPE "RoleSlug" AS ENUM ('admin', 'gestor', 'colaborador', 'cliente');
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'DemandaStatus') THEN
    CREATE TYPE "DemandaStatus" AS ENUM ('em_aberto', 'em_andamento', 'concluido', 'standby', 'cancelado');
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'RecorrenciaTipo') THEN
    CREATE TYPE "RecorrenciaTipo" AS ENUM ('diaria', 'semanal', 'quinzenal', 'mensal');
  END IF;
END
$$;

-- Tabelas (ordem respeitando FKs)
CREATE TABLE "User" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "email" TEXT NOT NULL UNIQUE,
  "password_hash" TEXT NOT NULL,
  "name" TEXT NOT NULL,
  "active" BOOLEAN NOT NULL DEFAULT true,
  "created_at" TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  "updated_at" TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE "Role" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "name" TEXT NOT NULL,
  "slug" "RoleSlug" NOT NULL UNIQUE
);

CREATE TABLE "user_role" (
  "user_id" UUID NOT NULL REFERENCES "User"("id") ON DELETE CASCADE,
  "role_id" UUID NOT NULL REFERENCES "Role"("id") ON DELETE CASCADE,
  PRIMARY KEY ("user_id", "role_id")
);

CREATE TABLE "Setor" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "name" TEXT NOT NULL,
  "slug" TEXT NOT NULL UNIQUE
);

CREATE TABLE "user_setor_permissao" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "user_id" UUID NOT NULL REFERENCES "User"("id") ON DELETE CASCADE,
  "setor_id" UUID NOT NULL REFERENCES "Setor"("id") ON DELETE CASCADE,
  "can_create" BOOLEAN NOT NULL DEFAULT false,
  "can_edit" BOOLEAN NOT NULL DEFAULT false,
  "can_delete" BOOLEAN NOT NULL DEFAULT false,
  "can_view" BOOLEAN NOT NULL DEFAULT true,
  UNIQUE ("user_id", "setor_id")
);

CREATE TABLE "Cliente" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "name" TEXT NOT NULL,
  "tipo_pessoa" TEXT,
  "documento" TEXT,
  CONSTRAINT "Cliente_tipo_pessoa_check" CHECK ("tipo_pessoa" IS NULL OR "tipo_pessoa" IN ('pf', 'pj')),
  "active" BOOLEAN NOT NULL DEFAULT true
);

CREATE TABLE "Demanda" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "protocolo" TEXT NOT NULL UNIQUE,
  "assunto" TEXT NOT NULL,
  "prioridade" BOOLEAN NOT NULL DEFAULT false,
  "prazo" DATE,
  "status" "DemandaStatus" NOT NULL DEFAULT 'em_aberto',
  "criador_id" UUID NOT NULL REFERENCES "User"("id"),
  "observacoes_gerais" TEXT,
  "is_recorrente" BOOLEAN NOT NULL DEFAULT false,
  "demanda_origem_id" UUID REFERENCES "Demanda"("id"),
  "resolvido_em" TIMESTAMP(3),
  "ultima_observacao_em" TIMESTAMP(3),
  "created_at" TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  "updated_at" TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE "demanda_setor" (
  "demanda_id" UUID NOT NULL REFERENCES "Demanda"("id") ON DELETE CASCADE,
  "setor_id" UUID NOT NULL REFERENCES "Setor"("id") ON DELETE CASCADE,
  PRIMARY KEY ("demanda_id", "setor_id")
);

CREATE TABLE "demanda_cliente" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "demanda_id" UUID NOT NULL REFERENCES "Demanda"("id") ON DELETE CASCADE,
  "cliente_id" UUID NOT NULL REFERENCES "Cliente"("id") ON DELETE CASCADE
);

CREATE TABLE "demanda_responsavel" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "demanda_id" UUID NOT NULL REFERENCES "Demanda"("id") ON DELETE CASCADE,
  "user_id" UUID NOT NULL REFERENCES "User"("id") ON DELETE CASCADE,
  "is_principal" BOOLEAN NOT NULL DEFAULT false
);

CREATE TABLE "subtarefa" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "demanda_id" UUID NOT NULL REFERENCES "Demanda"("id") ON DELETE CASCADE,
  "titulo" TEXT NOT NULL,
  "concluida" BOOLEAN NOT NULL DEFAULT false,
  "ordem" INTEGER NOT NULL DEFAULT 0,
  "responsavel_user_id" UUID REFERENCES "User"("id") ON DELETE SET NULL
);

CREATE TABLE "observacao" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "demanda_id" UUID NOT NULL REFERENCES "Demanda"("id") ON DELETE CASCADE,
  "user_id" UUID NOT NULL REFERENCES "User"("id") ON DELETE CASCADE,
  "texto" TEXT NOT NULL,
  "created_at" TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE "demanda_evento" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "demanda_id" UUID NOT NULL REFERENCES "Demanda"("id") ON DELETE CASCADE,
  "user_id" UUID REFERENCES "User"("id") ON DELETE SET NULL,
  "tipo" TEXT NOT NULL,
  "descricao" TEXT NOT NULL,
  "metadata" JSONB,
  "created_at" TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE "anexo" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "demanda_id" UUID NOT NULL REFERENCES "Demanda"("id") ON DELETE CASCADE,
  "filename" TEXT NOT NULL,
  "mime_type" TEXT NOT NULL,
  "size" INTEGER NOT NULL,
  "storage_path" TEXT NOT NULL
);

CREATE TABLE "recorrencia_config" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "demanda_id" UUID NOT NULL UNIQUE REFERENCES "Demanda"("id") ON DELETE CASCADE,
  "data_base" DATE NOT NULL,
  "tipo" "RecorrenciaTipo" NOT NULL,
  "prazo_reabertura_dias" INTEGER NOT NULL
);

-- Templates
CREATE TABLE "Template" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "name" TEXT NOT NULL,
  "descricao" TEXT,
  "assunto_template" TEXT,
  "prioridade_default" BOOLEAN NOT NULL DEFAULT false,
  "observacoes_gerais_template" TEXT,
  "is_recorrente_default" BOOLEAN NOT NULL DEFAULT false,
  "recorrencia_tipo" "RecorrenciaTipo",
  "recorrencia_data_base_default" DATE,
  "recorrencia_prazo_reabertura_dias" INTEGER,
  "criador_id" UUID NOT NULL REFERENCES "User"("id") ON DELETE CASCADE,
  "created_at" TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  "updated_at" TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE "template_setor" (
  "template_id" UUID NOT NULL REFERENCES "Template"("id") ON DELETE CASCADE,
  "setor_id" UUID NOT NULL REFERENCES "Setor"("id") ON DELETE CASCADE,
  PRIMARY KEY ("template_id", "setor_id")
);

CREATE TABLE "template_cliente" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "template_id" UUID NOT NULL REFERENCES "Template"("id") ON DELETE CASCADE,
  "cliente_id" UUID NOT NULL REFERENCES "Cliente"("id") ON DELETE CASCADE
);

CREATE TABLE "template_responsavel" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "template_id" UUID NOT NULL REFERENCES "Template"("id") ON DELETE CASCADE,
  "user_id" UUID NOT NULL REFERENCES "User"("id") ON DELETE CASCADE,
  "is_principal" BOOLEAN NOT NULL DEFAULT false
);

CREATE TABLE "template_subtarefa" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "template_id" UUID NOT NULL REFERENCES "Template"("id") ON DELETE CASCADE,
  "titulo" TEXT NOT NULL,
  "ordem" INTEGER NOT NULL DEFAULT 0,
  "responsavel_user_id" UUID REFERENCES "User"("id") ON DELETE SET NULL
);

CREATE TABLE "template_evento" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "template_id" UUID NOT NULL REFERENCES "Template"("id") ON DELETE CASCADE,
  "user_id" UUID REFERENCES "User"("id") ON DELETE SET NULL,
  "tipo" TEXT NOT NULL,
  "descricao" TEXT NOT NULL,
  "metadata" JSONB,
  "created_at" TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Índices sugeridos
CREATE INDEX "Demanda_criador_id_idx" ON "Demanda"("criador_id");
CREATE INDEX "Demanda_prazo_idx" ON "Demanda"("prazo");
CREATE INDEX "Demanda_status_idx" ON "Demanda"("status");
CREATE INDEX "Demanda_created_at_idx" ON "Demanda"("created_at");
CREATE INDEX "demanda_setor_setor_id_idx" ON "demanda_setor"("setor_id");
CREATE INDEX "demanda_responsavel_user_id_idx" ON "demanda_responsavel"("user_id");
CREATE INDEX "observacao_demanda_id_idx" ON "observacao"("demanda_id");
CREATE INDEX "demanda_evento_demanda_id_idx" ON "demanda_evento"("demanda_id");
CREATE INDEX "demanda_evento_created_at_idx" ON "demanda_evento"("created_at");
CREATE INDEX "Cliente_documento_idx" ON "Cliente"("documento");
CREATE INDEX "user_setor_permissao_user_setor_idx" ON "user_setor_permissao"("user_id", "setor_id");
CREATE INDEX "template_evento_template_id_idx" ON "template_evento"("template_id");
CREATE INDEX "template_evento_created_at_idx" ON "template_evento"("created_at");

-- Trigger para updated_at (opcional)
CREATE OR REPLACE FUNCTION update_updated_at()
RETURNS TRIGGER AS $$
BEGIN
  NEW."updated_at" = CURRENT_TIMESTAMP;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER "User_updated_at" BEFORE UPDATE ON "User" FOR EACH ROW EXECUTE FUNCTION update_updated_at();
CREATE TRIGGER "Demanda_updated_at" BEFORE UPDATE ON "Demanda" FOR EACH ROW EXECUTE FUNCTION update_updated_at();
CREATE TRIGGER "Template_updated_at" BEFORE UPDATE ON "Template" FOR EACH ROW EXECUTE FUNCTION update_updated_at();

-- Performance: índices e RPCs para reduzir round-trips do backend
CREATE INDEX IF NOT EXISTS "demanda_responsavel_demanda_id_idx" ON "demanda_responsavel"("demanda_id");
CREATE INDEX IF NOT EXISTS "demanda_cliente_demanda_id_idx" ON "demanda_cliente"("demanda_id");
CREATE INDEX IF NOT EXISTS "demanda_cliente_cliente_id_idx" ON "demanda_cliente"("cliente_id");
CREATE INDEX IF NOT EXISTS "subtarefa_demanda_id_idx" ON "subtarefa"("demanda_id");
CREATE INDEX IF NOT EXISTS "anexo_demanda_id_idx" ON "anexo"("demanda_id");

CREATE OR REPLACE FUNCTION public.rpc_visible_demanda_ids(p_user_id uuid)
RETURNS TABLE ("demanda_id" uuid)
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = public
AS $$
  WITH visible AS (
    SELECT d."id" AS demanda_id
    FROM public."Demanda" d
    WHERE d."criador_id" = p_user_id
    UNION
    SELECT dr."demanda_id"
    FROM public."demanda_responsavel" dr
    WHERE dr."user_id" = p_user_id
    UNION
    SELECT ds."demanda_id"
    FROM public."demanda_setor" ds
    JOIN public."user_setor_permissao" usp
      ON usp."setor_id" = ds."setor_id"
    WHERE usp."user_id" = p_user_id
      AND usp."can_view" = true
  )
  SELECT DISTINCT visible.demanda_id
  FROM visible;
$$;

CREATE OR REPLACE FUNCTION public.rpc_hydrate_demandas_list(p_ids uuid[])
RETURNS TABLE (
  "demanda_id" uuid,
  "criador" jsonb,
  "responsaveis" jsonb,
  "setores" jsonb,
  "clientes" jsonb
)
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = public
AS $$
  SELECT
    d."id" AS demanda_id,
    (
      SELECT jsonb_build_object('id', u."id", 'name', u."name", 'email', u."email")
      FROM public."User" u
      WHERE u."id" = d."criador_id"
    ) AS criador,
    COALESCE((
      SELECT jsonb_agg(
        jsonb_build_object(
          'userId', dr."user_id",
          'isPrincipal', dr."is_principal",
          'user', jsonb_build_object('id', u."id", 'name', u."name", 'email', u."email")
        )
        ORDER BY dr."is_principal" DESC, u."name" ASC
      )
      FROM public."demanda_responsavel" dr
      JOIN public."User" u ON u."id" = dr."user_id"
      WHERE dr."demanda_id" = d."id"
    ), '[]'::jsonb) AS responsaveis,
    COALESCE((
      SELECT jsonb_agg(
        jsonb_build_object(
          'setor', jsonb_build_object('id', s."id", 'name', s."name", 'slug', s."slug")
        )
        ORDER BY s."name" ASC
      )
      FROM public."demanda_setor" ds
      JOIN public."Setor" s ON s."id" = ds."setor_id"
      WHERE ds."demanda_id" = d."id"
    ), '[]'::jsonb) AS setores,
    COALESCE((
      SELECT jsonb_agg(
        jsonb_build_object(
          'cliente', jsonb_build_object('id', c."id", 'name', c."name")
        )
        ORDER BY c."name" ASC
      )
      FROM public."demanda_cliente" dc
      JOIN public."Cliente" c ON c."id" = dc."cliente_id"
      WHERE dc."demanda_id" = d."id"
    ), '[]'::jsonb) AS clientes
  FROM public."Demanda" d
  WHERE d."id" = ANY(p_ids);
$$;

CREATE OR REPLACE FUNCTION public.rpc_demanda_detail(p_demanda_id uuid)
RETURNS TABLE (
  "demanda_id" uuid,
  "criador" jsonb,
  "responsaveis" jsonb,
  "setores" jsonb,
  "clientes" jsonb,
  "subtarefas" jsonb,
  "observacoes" jsonb,
  "anexos" jsonb,
  "recorrencia_config" jsonb
)
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = public
AS $$
  SELECT
    d."id" AS demanda_id,
    (
      SELECT jsonb_build_object('id', u."id", 'name', u."name", 'email', u."email")
      FROM public."User" u
      WHERE u."id" = d."criador_id"
    ) AS criador,
    COALESCE((
      SELECT jsonb_agg(
        jsonb_build_object(
          'userId', dr."user_id",
          'isPrincipal', dr."is_principal",
          'user', jsonb_build_object('id', u."id", 'name', u."name", 'email', u."email")
        )
        ORDER BY dr."is_principal" DESC, u."name" ASC
      )
      FROM public."demanda_responsavel" dr
      JOIN public."User" u ON u."id" = dr."user_id"
      WHERE dr."demanda_id" = d."id"
    ), '[]'::jsonb) AS responsaveis,
    COALESCE((
      SELECT jsonb_agg(
        jsonb_build_object(
          'setor', jsonb_build_object('id', s."id", 'name', s."name", 'slug', s."slug")
        )
        ORDER BY s."name" ASC
      )
      FROM public."demanda_setor" ds
      JOIN public."Setor" s ON s."id" = ds."setor_id"
      WHERE ds."demanda_id" = d."id"
    ), '[]'::jsonb) AS setores,
    COALESCE((
      SELECT jsonb_agg(
        jsonb_build_object(
          'cliente', jsonb_build_object('id', c."id", 'name', c."name")
        )
        ORDER BY c."name" ASC
      )
      FROM public."demanda_cliente" dc
      JOIN public."Cliente" c ON c."id" = dc."cliente_id"
      WHERE dc."demanda_id" = d."id"
    ), '[]'::jsonb) AS clientes,
    COALESCE((
      SELECT jsonb_agg(
        jsonb_build_object(
          'id', s."id",
          'titulo', s."titulo",
          'concluida', s."concluida",
          'ordem', s."ordem",
          'responsavelUserId', s."responsavel_user_id",
          'responsavel',
            CASE
              WHEN u."id" IS NULL THEN NULL
              ELSE jsonb_build_object('id', u."id", 'name', u."name", 'email', u."email")
            END
        )
        ORDER BY s."ordem" ASC, s."id" ASC
      )
      FROM public."subtarefa" s
      LEFT JOIN public."User" u ON u."id" = s."responsavel_user_id"
      WHERE s."demanda_id" = d."id"
    ), '[]'::jsonb) AS subtarefas,
    COALESCE((
      SELECT jsonb_agg(
        jsonb_build_object(
          'id', o."id",
          'texto', o."texto",
          'createdAt', o."created_at",
          'user',
            CASE
              WHEN u."id" IS NULL THEN NULL
              ELSE jsonb_build_object('id', u."id", 'name', u."name")
            END
        )
        ORDER BY o."created_at" DESC
      )
      FROM public."observacao" o
      LEFT JOIN public."User" u ON u."id" = o."user_id"
      WHERE o."demanda_id" = d."id"
    ), '[]'::jsonb) AS observacoes,
    COALESCE((
      SELECT jsonb_agg(
        jsonb_build_object(
          'id', a."id",
          'filename', a."filename",
          'mime_type', a."mime_type",
          'size', a."size",
          'storage_path', a."storage_path"
        )
        ORDER BY a."id" ASC
      )
      FROM public."anexo" a
      WHERE a."demanda_id" = d."id"
    ), '[]'::jsonb) AS anexos,
    (
      SELECT jsonb_build_object(
        'data_base', rc."data_base",
        'tipo', rc."tipo",
        'prazo_reabertura_dias', rc."prazo_reabertura_dias"
      )
      FROM public."recorrencia_config" rc
      WHERE rc."demanda_id" = d."id"
    ) AS recorrencia_config
  FROM public."Demanda" d
  WHERE d."id" = p_demanda_id
  LIMIT 1;
$$;

GRANT EXECUTE ON FUNCTION public.rpc_visible_demanda_ids(uuid) TO authenticated, service_role;
GRANT EXECUTE ON FUNCTION public.rpc_hydrate_demandas_list(uuid[]) TO authenticated, service_role;
GRANT EXECUTE ON FUNCTION public.rpc_demanda_detail(uuid) TO authenticated, service_role;
-- Funcoes RPC para reduzir round-trips de templates, dashboard e busca IA.
-- Rode este SQL no Supabase SQL Editor.

create index if not exists idx_template_criador_id on public."Template" (criador_id);
create index if not exists idx_template_updated_at on public."Template" (updated_at desc);
create index if not exists idx_template_setor_template_id on public.template_setor (template_id);
create index if not exists idx_template_cliente_template_id on public.template_cliente (template_id);
create index if not exists idx_template_responsavel_template_id on public.template_responsavel (template_id);
create index if not exists idx_template_subtarefa_template_id on public.template_subtarefa (template_id);

create or replace function public.rpc_templates_list()
returns table (
  id uuid,
  name text,
  descricao text,
  assunto_template text,
  prioridade_default boolean,
  observacoes_gerais_template text,
  is_recorrente_default boolean,
  recorrencia_tipo text,
  recorrencia_data_base_default date,
  recorrencia_prazo_reabertura_dias integer,
  criador_id uuid,
  created_at timestamp,
  updated_at timestamp,
  criador jsonb,
  setores jsonb,
  clientes jsonb,
  responsaveis jsonb,
  subtarefas jsonb
)
language sql
stable
security definer
set search_path = public
as $$
  select
    t.id,
    t.name,
    t.descricao,
    t.assunto_template,
    t.prioridade_default,
    t.observacoes_gerais_template,
    t.is_recorrente_default,
    t.recorrencia_tipo::text,
    t.recorrencia_data_base_default,
    t.recorrencia_prazo_reabertura_dias,
    t.criador_id,
    t.created_at,
    t.updated_at,
    (
      select jsonb_build_object(
        'id', u.id,
        'name', u.name,
        'email', u.email
      )
      from public."User" u
      where u.id = t.criador_id
    ) as criador,
    coalesce((
      select jsonb_agg(
        jsonb_build_object(
          'setor', jsonb_build_object(
            'id', s.id,
            'name', s.name,
            'slug', s.slug
          )
        )
        order by s.name asc
      )
      from public.template_setor ts
      join public."Setor" s on s.id = ts.setor_id
      where ts.template_id = t.id
    ), '[]'::jsonb) as setores,
    coalesce((
      select jsonb_agg(
        jsonb_build_object(
          'cliente', jsonb_build_object(
            'id', c.id,
            'name', c.name,
            'active', c.active,
            'tipoPessoa', c.tipo_pessoa,
            'documento', c.documento
          )
        )
        order by c.name asc
      )
      from public.template_cliente tc
      join public."Cliente" c on c.id = tc.cliente_id
      where tc.template_id = t.id
    ), '[]'::jsonb) as clientes,
    coalesce((
      select jsonb_agg(
        jsonb_build_object(
          'userId', tr.user_id,
          'isPrincipal', tr.is_principal,
          'user', jsonb_build_object(
            'id', u.id,
            'name', u.name,
            'email', u.email
          )
        )
        order by tr.is_principal desc, u.name asc
      )
      from public.template_responsavel tr
      join public."User" u on u.id = tr.user_id
      where tr.template_id = t.id
    ), '[]'::jsonb) as responsaveis,
    coalesce((
      select jsonb_agg(
        jsonb_build_object(
          'id', ts.id,
          'titulo', ts.titulo,
          'ordem', ts.ordem,
          'responsavelUserId', ts.responsavel_user_id,
          'responsavel',
            case
              when u.id is null then null
              else jsonb_build_object(
                'id', u.id,
                'name', u.name,
                'email', u.email
              )
            end
        )
        order by ts.ordem asc, ts.id asc
      )
      from public.template_subtarefa ts
      left join public."User" u on u.id = ts.responsavel_user_id
      where ts.template_id = t.id
    ), '[]'::jsonb) as subtarefas
  from public."Template" t
  order by t.updated_at desc;
$$;

create or replace function public.rpc_template_detail(p_template_id uuid)
returns table (
  id uuid,
  name text,
  descricao text,
  assunto_template text,
  prioridade_default boolean,
  observacoes_gerais_template text,
  is_recorrente_default boolean,
  recorrencia_tipo text,
  recorrencia_data_base_default date,
  recorrencia_prazo_reabertura_dias integer,
  criador_id uuid,
  created_at timestamp,
  updated_at timestamp,
  criador jsonb,
  setores jsonb,
  clientes jsonb,
  responsaveis jsonb,
  subtarefas jsonb
)
language sql
stable
security definer
set search_path = public
as $$
  select *
  from public.rpc_templates_list()
  where id = p_template_id
  limit 1;
$$;

create or replace function public.rpc_dashboard_kpis()
returns table (
  total_demandas integer,
  concluidas integer,
  em_aberto integer,
  tempo_medio_resolucao_horas numeric,
  demandas_sem_observacao_recente integer,
  tempo_medio_desde_ultima_observacao_horas numeric,
  por_status jsonb
)
language sql
stable
security definer
set search_path = public
as $$
  with base as (
    select d.id, d.status, d.created_at, d.resolvido_em, d.ultima_observacao_em
    from public."Demanda" d
  ),
  status_counts as (
    select status, count(*)::int as total
    from base
    group by status
  )
  select
    count(*)::int as total_demandas,
    count(*) filter (where status = 'concluido')::int as concluidas,
    count(*) filter (where status = 'em_aberto')::int as em_aberto,
    round(avg(
      case
        when status = 'concluido' and resolvido_em is not null
          then extract(epoch from (resolvido_em - created_at)) / 3600.0
        else null
      end
    )::numeric, 1) as tempo_medio_resolucao_horas,
    count(*) filter (
      where ultima_observacao_em is null
         or now() - ultima_observacao_em > interval '7 days'
    )::int as demandas_sem_observacao_recente,
    round(avg(
      case
        when ultima_observacao_em is not null
          then extract(epoch from (now() - ultima_observacao_em)) / 3600.0
        else null
      end
    )::numeric, 1) as tempo_medio_desde_ultima_observacao_horas,
    coalesce((select jsonb_object_agg(status, total) from status_counts), '{}'::jsonb) as por_status
  from base;
$$;

create or replace function public.rpc_ia_global_catalog()
returns table (
  module text,
  title text,
  snippet text,
  searchable text,
  route text
)
language sql
stable
security definer
set search_path = public
as $$
  with user_roles as (
    select
      ur.user_id,
      string_agg(coalesce(r.name, r.slug::text), ', ' order by coalesce(r.name, r.slug::text)) as roles
    from public.user_role ur
    join public."Role" r on r.id = ur.role_id
    group by ur.user_id
  )
  select
    'setores'::text as module,
    s.name::text as title,
    concat('Slug: ', coalesce(s.slug, '—')) as snippet,
    concat_ws(' ', s.name, s.slug) as searchable,
    '/cadastros'::text as route
  from public."Setor" s

  union all

  select
    'clientes'::text as module,
    c.name::text as title,
    case when c.active then 'Cliente ativo' else 'Cliente inativo' end as snippet,
    concat_ws(' ', c.name, case when c.active then 'ativo cliente' else 'inativo cliente' end) as searchable,
    '/cadastros'::text as route
  from public."Cliente" c

  union all

  select
    'templates'::text as module,
    t.name::text as title,
    concat_ws(
      ' | ',
      nullif(t.descricao, ''),
      case when coalesce(t.assunto_template, '') <> '' then concat('Assunto: ', t.assunto_template) end,
      case when t.is_recorrente_default then concat('Recorrente: ', coalesce(t.recorrencia_tipo::text, 'sim')) end
    ) as snippet,
    concat_ws(
      ' ',
      t.name,
      t.descricao,
      t.assunto_template,
      t.observacoes_gerais_template,
      case when t.is_recorrente_default then concat('recorrente ', coalesce(t.recorrencia_tipo::text, '')) end
    ) as searchable,
    '/templates'::text as route
  from public."Template" t

  union all

  select
    'usuarios'::text as module,
    coalesce(u.name, u.email, 'Usuário')::text as title,
    concat_ws(
      ' | ',
      nullif(u.email, ''),
      case when coalesce(ur.roles, '') <> '' then concat('Perfis: ', ur.roles) end,
      case when u.active then 'Ativo' else 'Inativo' end
    ) as snippet,
    concat_ws(
      ' ',
      u.name,
      u.email,
      case when u.active then 'ativo' else 'inativo' end,
      ur.roles
    ) as searchable,
    '/cadastros'::text as route
  from public."User" u
  left join user_roles ur on ur.user_id = u.id;
$$;

create or replace function public.rpc_ia_demanda_dataset(p_ids uuid[])
returns table (
  demanda_id uuid,
  protocolo text,
  assunto text,
  status text,
  prioridade boolean,
  observacoes_gerais text,
  prazo date,
  created_at timestamp,
  resolvido_em timestamp,
  ultima_observacao_em timestamp,
  is_recorrente boolean,
  criador text,
  responsaveis text,
  setores text,
  clientes text,
  subtarefas text,
  observacoes text,
  recorrencia text
)
language sql
stable
security definer
set search_path = public
as $$
  select
    d.id as demanda_id,
    d.protocolo,
    d.assunto,
    d.status::text,
    d.prioridade,
    d.observacoes_gerais,
    d.prazo,
    d.created_at,
    d.resolvido_em,
    d.ultima_observacao_em,
    d.is_recorrente,
    coalesce(criador.name, '') as criador,
    coalesce(resp.nomes, '') as responsaveis,
    coalesce(sets.nomes, '') as setores,
    coalesce(cls.nomes, '') as clientes,
    coalesce(subs.texto, '') as subtarefas,
    coalesce(obs.texto, '') as observacoes,
    coalesce(rec.texto, '') as recorrencia
  from public."Demanda" d
  left join public."User" criador on criador.id = d.criador_id
  left join lateral (
    select string_agg(u.name, ' | ' order by dr.is_principal desc, u.name asc) as nomes
    from public.demanda_responsavel dr
    join public."User" u on u.id = dr.user_id
    where dr.demanda_id = d.id
  ) resp on true
  left join lateral (
    select string_agg(s.name, ' | ' order by s.name asc) as nomes
    from public.demanda_setor ds
    join public."Setor" s on s.id = ds.setor_id
    where ds.demanda_id = d.id
  ) sets on true
  left join lateral (
    select string_agg(c.name, ' | ' order by c.name asc) as nomes
    from public.demanda_cliente dc
    join public."Cliente" c on c.id = dc.cliente_id
    where dc.demanda_id = d.id
  ) cls on true
  left join lateral (
    select string_agg(
      concat_ws(' ', s.titulo, case when u.name is not null then concat('responsavel', ' ', u.name) end),
      ' | '
      order by s.ordem asc, s.id asc
    ) as texto
    from public.subtarefa s
    left join public."User" u on u.id = s.responsavel_user_id
    where s.demanda_id = d.id
  ) subs on true
  left join lateral (
    select string_agg(o.texto, ' | ' order by o.created_at desc) as texto
    from public.observacao o
    where o.demanda_id = d.id
  ) obs on true
  left join lateral (
    select concat_ws(
      ' ',
      case when d.is_recorrente then 'recorrente' else null end,
      coalesce(rc.tipo::text, ''),
      case when rc.data_base is not null then concat('data base ', rc.data_base::text) end,
      case when rc.prazo_reabertura_dias is not null then concat('prazo reabertura ', rc.prazo_reabertura_dias::text, ' dias') end
    ) as texto
    from public.recorrencia_config rc
    where rc.demanda_id = d.id
  ) rec on true
  where d.id = any(p_ids);
$$;

create or replace function public.rpc_ia_system_context(p_user_id uuid)
returns table (
  total_demandas_visiveis integer,
  por_status jsonb,
  demandas_recentes jsonb,
  setores jsonb,
  clientes_ativos jsonb,
  templates jsonb
)
language sql
stable
security definer
set search_path = public
as $$
  with visible as (
    select demanda_id
    from public.rpc_visible_demanda_ids(p_user_id)
  ),
  visible_demandas as (
    select d.id, d.protocolo, d.assunto, d.status, d.created_at
    from public."Demanda" d
    join visible v on v.demanda_id = d.id
  ),
  status_counts as (
    select status, count(*)::int as total
    from visible_demandas
    group by status
  ),
  recent_rows as (
    select protocolo, assunto, status, created_at
    from visible_demandas
    order by created_at desc
    limit 5
  )
  select
    (select count(*)::int from visible) as total_demandas_visiveis,
    coalesce((select jsonb_object_agg(status, total) from status_counts), '{}'::jsonb) as por_status,
    coalesce((
      select jsonb_agg(
        jsonb_build_object(
          'protocolo', rr.protocolo,
          'assunto', rr.assunto,
          'status', rr.status
        )
        order by rr.created_at desc
      )
      from recent_rows rr
    ), '[]'::jsonb) as demandas_recentes,
    coalesce((
      select jsonb_agg(s.name order by s.name asc)
      from (
        select name
        from public."Setor"
        order by name asc
        limit 12
      ) s
    ), '[]'::jsonb) as setores,
    coalesce((
      select jsonb_agg(c.name order by c.name asc)
      from (
        select name
        from public."Cliente"
        where active = true
        order by name asc
        limit 12
      ) c
    ), '[]'::jsonb) as clientes_ativos,
    coalesce((
      select jsonb_agg(t.name order by t.updated_at desc)
      from (
        select name, updated_at
        from public."Template"
        order by updated_at desc
        limit 8
      ) t
    ), '[]'::jsonb) as templates;
$$;

grant execute on function public.rpc_templates_list() to authenticated, service_role;
grant execute on function public.rpc_template_detail(uuid) to authenticated, service_role;
grant execute on function public.rpc_dashboard_kpis() to authenticated, service_role;
grant execute on function public.rpc_ia_global_catalog() to authenticated, service_role;
grant execute on function public.rpc_ia_demanda_dataset(uuid[]) to authenticated, service_role;
grant execute on function public.rpc_ia_system_context(uuid) to authenticated, service_role;
