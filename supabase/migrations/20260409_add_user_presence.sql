create table if not exists public.user_presence (
  user_id uuid primary key references public."User"(id) on delete cascade,
  status text not null default 'online',
  pathname text,
  page_label text,
  activity text,
  last_seen_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create index if not exists user_presence_last_seen_at_idx
  on public.user_presence (last_seen_at desc);
