alter table public."Demanda"
  add column if not exists "legacy_id" text;

create unique index if not exists "Demanda_legacy_id_uidx"
  on public."Demanda" ("legacy_id")
  where "legacy_id" is not null;

create index if not exists "anexo_storage_path_idx"
  on public."anexo" ("storage_path");
