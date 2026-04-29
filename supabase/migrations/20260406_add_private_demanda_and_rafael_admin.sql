alter table public."Demanda"
  add column if not exists is_privada boolean not null default false,
  add column if not exists private_owner_user_id uuid references public."User"(id) on delete set null;

create index if not exists demanda_private_owner_user_id_idx on public."Demanda"(private_owner_user_id);

insert into public.user_role (user_id, role_id)
select u.id, r.id
from public."User" u
join public."Role" r on r.slug = 'admin'
where lower(u.email) = 'rafael@luxustelefonia.com.br'
  and not exists (
    select 1
    from public.user_role ur
    where ur.user_id = u.id
      and ur.role_id = r.id
  );
