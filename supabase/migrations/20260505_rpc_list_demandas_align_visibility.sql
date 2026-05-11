-- Alinha rpc_list_demanda_ids com rpc_visible_demanda_ids e corrige lista zerada quando
-- o backend já enviou p_ids (interseção com o CTE "visible" antigo removia tudo para colaboradores
-- que viam demandas apenas via visibilidade estendida, ex.: observador de privadas).
--
-- Inclui subtarefas atribuídas ao usuário, coerente com canViewDemanda no Nest.

create or replace function public.rpc_visible_demanda_ids(p_user_id uuid)
returns table(demanda_id uuid)
language sql
stable
security definer
set search_path = public
as $$
  select distinct visible.demanda_id
  from (
    select d.id as demanda_id
    from public."Demanda" d
    where coalesce(d.is_privada, false) = false
       or d.criador_id = p_user_id
       or d.private_owner_user_id = p_user_id

    union

    select dr.demanda_id
    from public.demanda_responsavel dr
    join public."Demanda" d on d.id = dr.demanda_id
    where dr.user_id = p_user_id
      and (
        coalesce(d.is_privada, false) = false
        or d.criador_id = p_user_id
        or d.private_owner_user_id = p_user_id
        or exists (
          select 1
          from public.demanda_private_viewer dpv
          where dpv.demanda_id = d.id
            and dpv.user_id = p_user_id
        )
      )

    union

    select st.demanda_id
    from public.subtarefa st
    join public."Demanda" d on d.id = st.demanda_id
    where st.responsavel_user_id = p_user_id
      and (
        coalesce(d.is_privada, false) = false
        or d.criador_id = p_user_id
        or d.private_owner_user_id = p_user_id
        or exists (
          select 1
          from public.demanda_private_viewer dpv
          where dpv.demanda_id = d.id
            and dpv.user_id = p_user_id
        )
      )

    union

    select ds.demanda_id
    from public.demanda_setor ds
    join public.user_setor_permissao usp on usp.setor_id = ds.setor_id
    join public."Demanda" d on d.id = ds.demanda_id
    where usp.user_id = p_user_id
      and usp.can_view = true
      and (
        coalesce(d.is_privada, false) = false
        or d.criador_id = p_user_id
        or d.private_owner_user_id = p_user_id
        or exists (
          select 1
          from public.demanda_private_viewer dpv
          where dpv.demanda_id = d.id
            and dpv.user_id = p_user_id
        )
      )

    union

    select dpv.demanda_id
    from public.demanda_private_viewer dpv
    where dpv.user_id = p_user_id
  ) visible;
$$;

create or replace function public.rpc_list_demandas_page(
  p_user_id uuid,
  p_limit integer default 100,
  p_offset integer default 0,
  p_ids uuid[] default null,
  p_cliente_id uuid default null,
  p_assunto text default null,
  p_status text default null,
  p_tipo_recorrencia text default null,
  p_protocolo text default null,
  p_prioridade boolean default null,
  p_criador_id uuid default null,
  p_responsavel_principal_id uuid default null,
  p_setor_ids uuid[] default null,
  p_condicao_prazo text default null,
  p_pesquisa_tarefa_ou_observacao text default null,
  p_pesquisa_geral text default null,
  p_data_criacao_de date default null,
  p_data_criacao_ate date default null,
  p_prazo_de date default null,
  p_prazo_ate date default null
)
returns table (
  total_count integer,
  id uuid,
  protocolo text,
  assunto text,
  prioridade boolean,
  prazo date,
  status text,
  criador_id uuid,
  observacoes_gerais text,
  is_recorrente boolean,
  demanda_origem_id uuid,
  created_at timestamp,
  updated_at timestamp,
  resolvido_em timestamp,
  ultima_observacao_em timestamp,
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
  with visible as (
    select v.demanda_id
    from public.rpc_visible_demanda_ids(p_user_id) v
  ),
  filtered as (
    select d.*
    from public."Demanda" d
    where (
      (p_ids is not null and cardinality(p_ids) > 0 and d.id = any(p_ids))
      or (
        (p_ids is null or cardinality(p_ids) = 0)
        and exists (select 1 from visible vv where vv.demanda_id = d.id)
      )
    )
      and (p_cliente_id is null or exists (
        select 1
        from public.demanda_cliente dc
        where dc.demanda_id = d.id
          and dc.cliente_id = p_cliente_id
      ))
      and (p_assunto is null or d.assunto ilike concat('%', p_assunto, '%'))
      and (p_status is null or d.status::text = p_status)
      and (p_tipo_recorrencia is null or exists (
        select 1
        from public.recorrencia_config rc
        where rc.demanda_id = d.id
          and rc.tipo::text = p_tipo_recorrencia
      ))
      and (p_protocolo is null or d.protocolo ilike concat('%', p_protocolo, '%'))
      and (p_prioridade is null or d.prioridade = p_prioridade)
      and (p_criador_id is null or d.criador_id = p_criador_id)
      and (p_responsavel_principal_id is null or exists (
        select 1
        from public.demanda_responsavel dr
        where dr.demanda_id = d.id
          and dr.user_id = p_responsavel_principal_id
          and dr.is_principal = true
      ))
      and (p_setor_ids is null or cardinality(p_setor_ids) = 0 or exists (
        select 1
        from public.demanda_setor ds
        where ds.demanda_id = d.id
          and ds.setor_id = any(p_setor_ids)
      ))
      and (
        p_condicao_prazo is null
        or (p_condicao_prazo = 'finalizada' and d.status::text in ('concluido', 'cancelado'))
        or (p_condicao_prazo = 'vencido' and d.status::text in ('em_aberto', 'em_andamento') and d.prazo is not null and d.prazo < current_date)
        or (p_condicao_prazo = 'no_prazo' and d.status::text in ('em_aberto', 'em_andamento') and d.prazo is not null and d.prazo >= current_date)
      )
      and (p_data_criacao_de is null or d.created_at::date >= p_data_criacao_de)
      and (p_data_criacao_ate is null or d.created_at::date <= p_data_criacao_ate)
      and (p_prazo_de is null or d.prazo >= p_prazo_de)
      and (p_prazo_ate is null or d.prazo <= p_prazo_ate)
      and (
        p_pesquisa_tarefa_ou_observacao is null
        or exists (
          select 1
          from public.subtarefa st
          where st.demanda_id = d.id
            and st.titulo ilike concat('%', p_pesquisa_tarefa_ou_observacao, '%')
        )
        or exists (
          select 1
          from public.observacao o
          where o.demanda_id = d.id
            and o.texto ilike concat('%', p_pesquisa_tarefa_ou_observacao, '%')
        )
      )
      and (
        p_pesquisa_geral is null
        or concat_ws(
          ' ',
          d.protocolo,
          d.assunto,
          d.status::text,
          case when d.prioridade then 'prioridade urgente sim' else 'prioridade nao' end,
          coalesce(d.observacoes_gerais, ''),
          coalesce(d.prazo::text, ''),
          coalesce(d.created_at::text, ''),
          coalesce(d.resolvido_em::text, ''),
          coalesce(d.ultima_observacao_em::text, ''),
          case when d.is_recorrente then 'recorrente' else 'nao recorrente' end
        ) ilike concat('%', p_pesquisa_geral, '%')
        or exists (
          select 1
          from public."User" u
          where u.id = d.criador_id
            and concat_ws(' ', u.name, u.email) ilike concat('%', p_pesquisa_geral, '%')
        )
        or exists (
          select 1
          from public.demanda_responsavel dr
          join public."User" u on u.id = dr.user_id
          where dr.demanda_id = d.id
            and concat_ws(' ', u.name, u.email) ilike concat('%', p_pesquisa_geral, '%')
        )
        or exists (
          select 1
          from public.demanda_setor ds
          join public."Setor" s on s.id = ds.setor_id
          where ds.demanda_id = d.id
            and concat_ws(' ', s.name, s.slug) ilike concat('%', p_pesquisa_geral, '%')
        )
        or exists (
          select 1
          from public.demanda_cliente dc
          join public."Cliente" c on c.id = dc.cliente_id
          where dc.demanda_id = d.id
            and c.name ilike concat('%', p_pesquisa_geral, '%')
        )
        or exists (
          select 1
          from public.subtarefa st
          where st.demanda_id = d.id
            and st.titulo ilike concat('%', p_pesquisa_geral, '%')
        )
        or exists (
          select 1
          from public.observacao o
          where o.demanda_id = d.id
            and o.texto ilike concat('%', p_pesquisa_geral, '%')
        )
        or exists (
          select 1
          from public.recorrencia_config rc
          where rc.demanda_id = d.id
            and concat_ws(' ', rc.tipo::text, rc.data_base::text, rc.prazo_reabertura_dias::text)
              ilike concat('%', p_pesquisa_geral, '%')
        )
      )
  ),
  paged as (
    select
      count(*) over()::int as total_count,
      filtered.*
    from filtered
    order by filtered.created_at desc
    limit greatest(coalesce(p_limit, 100), 1)
    offset greatest(coalesce(p_offset, 0), 0)
  )
  select
    d.total_count,
    d.id,
    d.protocolo,
    d.assunto,
    d.prioridade,
    d.prazo,
    d.status::text,
    d.criador_id,
    d.observacoes_gerais,
    d.is_recorrente,
    d.demanda_origem_id,
    d.created_at,
    d.updated_at,
    d.resolvido_em,
    d.ultima_observacao_em,
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
  from paged d;
$$;
