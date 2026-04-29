-- Tabela de observabilidade da busca IA.
-- Rode este SQL no Supabase SQL Editor.

create extension if not exists pgcrypto;

create table if not exists public.ia_busca_log (
  id uuid primary key default gen_random_uuid(),
  created_at timestamptz not null default now(),
  user_id uuid null,
  query text not null,
  scope text not null default 'all',
  search_mode text not null default 'all',
  filters_json jsonb not null default '{}'::jsonb,
  preview_total integer not null default 0,
  preview_protocolos jsonb not null default '[]'::jsonb,
  module_counts_json jsonb not null default '[]'::jsonb,
  success boolean not null,
  error_message text null,
  latency_ms integer not null default 0
);

create index if not exists idx_ia_busca_log_created_at on public.ia_busca_log (created_at desc);
create index if not exists idx_ia_busca_log_user_id on public.ia_busca_log (user_id);
create index if not exists idx_ia_busca_log_success on public.ia_busca_log (success);
