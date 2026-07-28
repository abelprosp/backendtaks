-- Conta observacao recente do dashboard apenas na fila ativa.
-- Concluidas/canceladas continuam entrando em total, status e tempo de resolucao.

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
  ),
  active_base as (
    select *
    from base
    where status in ('em_aberto', 'em_andamento', 'standby')
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
    (select count(*)::int
     from active_base
     where ultima_observacao_em is null
        or now() - ultima_observacao_em > interval '7 days') as demandas_sem_observacao_recente,
    round(avg(
      case
        when status in ('em_aberto', 'em_andamento', 'standby') and ultima_observacao_em is not null
          then extract(epoch from (now() - ultima_observacao_em)) / 3600.0
        else null
      end
    )::numeric, 1) as tempo_medio_desde_ultima_observacao_horas,
    coalesce((select jsonb_object_agg(status, total) from status_counts), '{}'::jsonb) as por_status
  from base;
$$;

grant execute on function public.rpc_dashboard_kpis() to authenticated, service_role;
