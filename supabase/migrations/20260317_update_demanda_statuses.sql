-- Atualiza os status legados de demandas para a nova taxonomia.
-- Rode este SQL no Supabase SQL Editor apos a migration base.

do $$
begin
  if exists (
    select 1
    from pg_type t
    join pg_namespace n on n.oid = t.typnamespace
    join pg_enum e on e.enumtypid = t.oid
    where n.nspname = 'public'
      and t.typname = 'DemandaStatus'
      and e.enumlabel = 'pendente'
  ) and not exists (
    select 1
    from pg_type t
    join pg_namespace n on n.oid = t.typnamespace
    join pg_enum e on e.enumtypid = t.oid
    where n.nspname = 'public'
      and t.typname = 'DemandaStatus'
      and e.enumlabel = 'em_andamento'
  ) then
    alter type public."DemandaStatus" rename value 'pendente' to 'em_andamento';
  end if;
end $$;

do $$
begin
  if exists (
    select 1
    from pg_type t
    join pg_namespace n on n.oid = t.typnamespace
    join pg_enum e on e.enumtypid = t.oid
    where n.nspname = 'public'
      and t.typname = 'DemandaStatus'
      and e.enumlabel = 'pendente_de_resposta'
  ) and not exists (
    select 1
    from pg_type t
    join pg_namespace n on n.oid = t.typnamespace
    join pg_enum e on e.enumtypid = t.oid
    where n.nspname = 'public'
      and t.typname = 'DemandaStatus'
      and e.enumlabel = 'standby'
  ) then
    alter type public."DemandaStatus" rename value 'pendente_de_resposta' to 'standby';
  end if;
end $$;

do $$
begin
  if not exists (
    select 1
    from pg_type t
    join pg_namespace n on n.oid = t.typnamespace
    join pg_enum e on e.enumtypid = t.oid
    where n.nspname = 'public'
      and t.typname = 'DemandaStatus'
      and e.enumlabel = 'cancelado'
  ) then
    alter type public."DemandaStatus" add value 'cancelado';
  end if;
end $$;
