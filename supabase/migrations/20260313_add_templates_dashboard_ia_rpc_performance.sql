-- Funcoes RPC para reduzir round-trips de templates, dashboard e busca IA.
-- Rode este SQL no Supabase SQL Editor.

create index if not exists idx_template_criador_id on public."Template" (criador_id);
create index if not exists idx_template_updated_at on public."Template" (updated_at desc);
create index if not exists idx_template_setor_template_id on public.template_setor (template_id);
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
