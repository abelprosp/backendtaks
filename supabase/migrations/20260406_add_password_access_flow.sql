alter table public."User"
  add column if not exists needs_password_setup boolean not null default false;

update public."User"
set needs_password_setup = true
where needs_password_setup = false
  and password_hash = '$2a$06$T/2vNgiBvzUe1c0GvDZFyetzLYmz37qm73Yh2GBJo0r4hypfp/6BG';

create table if not exists public.user_password_token (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null references public."User"(id) on delete cascade,
  token_hash text not null,
  purpose text not null,
  created_by_user_id uuid references public."User"(id) on delete set null,
  delivery_method text,
  expires_at timestamp(3) not null,
  used_at timestamp(3),
  created_at timestamp(3) not null default current_timestamp
);

create unique index if not exists user_password_token_token_hash_idx on public.user_password_token(token_hash);
create index if not exists user_password_token_user_id_idx on public.user_password_token(user_id);
create index if not exists user_password_token_expires_at_idx on public.user_password_token(expires_at);

do $$
begin
  if not exists (
    select 1
    from pg_constraint
    where conname = 'user_password_token_purpose_check'
  ) then
    alter table public.user_password_token
      add constraint user_password_token_purpose_check
      check (purpose in ('first_access', 'reset_password'));
  end if;
end
$$;
