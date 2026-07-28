-- Auditoria dedicada para demandas e templates.
-- Mantem trilha de eventos separada das observacoes operacionais.

create table if not exists public.demanda_evento (
  id uuid primary key default gen_random_uuid(),
  demanda_id uuid not null references public."Demanda"(id) on delete cascade,
  user_id uuid references public."User"(id) on delete set null,
  tipo text not null,
  descricao text not null,
  metadata jsonb,
  created_at timestamp(3) not null default current_timestamp
);

create index if not exists demanda_evento_demanda_id_idx on public.demanda_evento(demanda_id);
create index if not exists demanda_evento_created_at_idx on public.demanda_evento(created_at);

create table if not exists public.template_evento (
  id uuid primary key default gen_random_uuid(),
  template_id uuid not null references public."Template"(id) on delete cascade,
  user_id uuid references public."User"(id) on delete set null,
  tipo text not null,
  descricao text not null,
  metadata jsonb,
  created_at timestamp(3) not null default current_timestamp
);

create index if not exists template_evento_template_id_idx on public.template_evento(template_id);
create index if not exists template_evento_created_at_idx on public.template_evento(created_at);
