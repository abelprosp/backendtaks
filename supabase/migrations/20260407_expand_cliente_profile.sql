alter table public."Cliente"
  add column if not exists "legacy_id" text,
  add column if not exists "nome_fantasia" text,
  add column if not exists "ramo_atividade" text,
  add column if not exists "inscricao_estadual" text,
  add column if not exists "cep" text,
  add column if not exists "endereco" text,
  add column if not exists "numero" text,
  add column if not exists "complemento" text,
  add column if not exists "bairro" text,
  add column if not exists "cidade" text,
  add column if not exists "uf" text,
  add column if not exists "telefone" text,
  add column if not exists "celular" text,
  add column if not exists "contato" text,
  add column if not exists "email" text,
  add column if not exists "observacoes_cadastro" text;

create unique index if not exists "Cliente_legacy_id_uidx"
  on public."Cliente" ("legacy_id")
  where "legacy_id" is not null;
