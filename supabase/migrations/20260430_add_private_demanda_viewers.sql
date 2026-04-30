create table if not exists public.demanda_private_viewer (
  id uuid primary key default gen_random_uuid(),
  demanda_id uuid not null references public."Demanda"(id) on delete cascade,
  user_id uuid not null references public."User"(id) on delete cascade,
  created_at timestamp(3) not null default current_timestamp,
  unique (demanda_id, user_id)
);

create index if not exists demanda_private_viewer_demanda_id_idx
  on public.demanda_private_viewer(demanda_id);

create index if not exists demanda_private_viewer_user_id_idx
  on public.demanda_private_viewer(user_id);

create or replace function public.rpc_visible_demanda_ids(p_user_id uuid)
returns table(demanda_id uuid)
language sql
stable
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
