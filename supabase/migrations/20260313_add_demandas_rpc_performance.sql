-- Funcoes RPC para reduzir round-trips do backend com Supabase REST.
-- Rode este SQL no Supabase SQL Editor.

create index if not exists idx_demanda_responsavel_demanda_id on public.demanda_responsavel (demanda_id);
create index if not exists idx_demanda_cliente_demanda_id on public.demanda_cliente (demanda_id);
create index if not exists idx_demanda_cliente_cliente_id on public.demanda_cliente (cliente_id);
create index if not exists idx_subtarefa_demanda_id on public.subtarefa (demanda_id);
create index if not exists idx_anexo_demanda_id on public.anexo (demanda_id);

create or replace function public.rpc_visible_demanda_ids(p_user_id uuid)
returns table (demanda_id uuid)
language sql
stable
security definer
set search_path = public
as $$
  with visible as (
    select d.id as demanda_id
    from public."Demanda" d
    where d.criador_id = p_user_id

    union

    select dr.demanda_id
    from public.demanda_responsavel dr
    where dr.user_id = p_user_id

    union

    select ds.demanda_id
    from public.demanda_setor ds
    join public.user_setor_permissao usp
      on usp.setor_id = ds.setor_id
    where usp.user_id = p_user_id
      and usp.can_view = true
  )
  select distinct visible.demanda_id
  from visible;
$$;

create or replace function public.rpc_hydrate_demandas_list(p_ids uuid[])
returns table (
  demanda_id uuid,
  criador jsonb,
  responsaveis jsonb,
  setores jsonb,
  clientes jsonb
)
language sql
stable
security definer
set search_path = public
as $$
  select
    d.id as demanda_id,
    (
      select jsonb_build_object(
        'id', u.id,
        'name', u.name,
        'email', u.email
      )
      from public."User" u
      where u.id = d.criador_id
    ) as criador,
    coalesce((
      select jsonb_agg(
        jsonb_build_object(
          'userId', dr.user_id,
          'isPrincipal', dr.is_principal,
          'user', jsonb_build_object(
            'id', u.id,
            'name', u.name,
            'email', u.email
          )
        )
        order by dr.is_principal desc, u.name asc
      )
      from public.demanda_responsavel dr
      join public."User" u on u.id = dr.user_id
      where dr.demanda_id = d.id
    ), '[]'::jsonb) as responsaveis,
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
      from public.demanda_setor ds
      join public."Setor" s on s.id = ds.setor_id
      where ds.demanda_id = d.id
    ), '[]'::jsonb) as setores,
    coalesce((
      select jsonb_agg(
        jsonb_build_object(
          'cliente', jsonb_build_object(
            'id', c.id,
            'name', c.name
          )
        )
        order by c.name asc
      )
      from public.demanda_cliente dc
      join public."Cliente" c on c.id = dc.cliente_id
      where dc.demanda_id = d.id
    ), '[]'::jsonb) as clientes
  from public."Demanda" d
  where d.id = any(p_ids);
$$;

create or replace function public.rpc_demanda_detail(p_demanda_id uuid)
returns table (
  demanda_id uuid,
  criador jsonb,
  responsaveis jsonb,
  setores jsonb,
  clientes jsonb,
  subtarefas jsonb,
  observacoes jsonb,
  anexos jsonb,
  recorrencia_config jsonb
)
language sql
stable
security definer
set search_path = public
as $$
  select
    d.id as demanda_id,
    (
      select jsonb_build_object(
        'id', u.id,
        'name', u.name,
        'email', u.email
      )
      from public."User" u
      where u.id = d.criador_id
    ) as criador,
    coalesce((
      select jsonb_agg(
        jsonb_build_object(
          'userId', dr.user_id,
          'isPrincipal', dr.is_principal,
          'user', jsonb_build_object(
            'id', u.id,
            'name', u.name,
            'email', u.email
          )
        )
        order by dr.is_principal desc, u.name asc
      )
      from public.demanda_responsavel dr
      join public."User" u on u.id = dr.user_id
      where dr.demanda_id = d.id
    ), '[]'::jsonb) as responsaveis,
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
      from public.demanda_setor ds
      join public."Setor" s on s.id = ds.setor_id
      where ds.demanda_id = d.id
    ), '[]'::jsonb) as setores,
    coalesce((
      select jsonb_agg(
        jsonb_build_object(
          'cliente', jsonb_build_object(
            'id', c.id,
            'name', c.name
          )
        )
        order by c.name asc
      )
      from public.demanda_cliente dc
      join public."Cliente" c on c.id = dc.cliente_id
      where dc.demanda_id = d.id
    ), '[]'::jsonb) as clientes,
    coalesce((
      select jsonb_agg(
        jsonb_build_object(
          'id', s.id,
          'titulo', s.titulo,
          'concluida', s.concluida,
          'ordem', s.ordem,
          'responsavelUserId', s.responsavel_user_id,
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
        order by s.ordem asc, s.id asc
      )
      from public.subtarefa s
      left join public."User" u on u.id = s.responsavel_user_id
      where s.demanda_id = d.id
    ), '[]'::jsonb) as subtarefas,
    coalesce((
      select jsonb_agg(
        jsonb_build_object(
          'id', o.id,
          'texto', o.texto,
          'createdAt', o.created_at,
          'user',
            case
              when u.id is null then null
              else jsonb_build_object(
                'id', u.id,
                'name', u.name
              )
            end
        )
        order by o.created_at desc
      )
      from public.observacao o
      left join public."User" u on u.id = o.user_id
      where o.demanda_id = d.id
    ), '[]'::jsonb) as observacoes,
    coalesce((
      select jsonb_agg(
        jsonb_build_object(
          'id', a.id,
          'filename', a.filename,
          'mime_type', a.mime_type,
          'size', a.size,
          'storage_path', a.storage_path
        )
        order by a.id asc
      )
      from public.anexo a
      where a.demanda_id = d.id
    ), '[]'::jsonb) as anexos,
    (
      select jsonb_build_object(
        'data_base', rc.data_base,
        'tipo', rc.tipo,
        'prazo_reabertura_dias', rc.prazo_reabertura_dias
      )
      from public.recorrencia_config rc
      where rc.demanda_id = d.id
    ) as recorrencia_config
  from public."Demanda" d
  where d.id = p_demanda_id
  limit 1;
$$;

grant execute on function public.rpc_visible_demanda_ids(uuid) to authenticated, service_role;
grant execute on function public.rpc_hydrate_demandas_list(uuid[]) to authenticated, service_role;
grant execute on function public.rpc_demanda_detail(uuid) to authenticated, service_role;
