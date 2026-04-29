-- Melhorias de performance para busca textual da IA e filtros de demandas.
-- Rode este SQL no Supabase SQL Editor.

create extension if not exists pg_trgm;

create index if not exists idx_demanda_status on "Demanda" (status);
create index if not exists idx_demanda_created_at on "Demanda" (created_at desc);
create index if not exists idx_demanda_prazo on "Demanda" (prazo);

create index if not exists idx_demanda_assunto_trgm
  on "Demanda" using gin (assunto gin_trgm_ops);

create index if not exists idx_demanda_protocolo_trgm
  on "Demanda" using gin (protocolo gin_trgm_ops);

create index if not exists idx_demanda_observacoes_gerais_trgm
  on "Demanda" using gin (observacoes_gerais gin_trgm_ops);

create index if not exists idx_subtarefa_titulo_trgm
  on subtarefa using gin (titulo gin_trgm_ops);

create index if not exists idx_observacao_texto_trgm
  on observacao using gin (texto gin_trgm_ops);

create index if not exists idx_demanda_setor_setor on demanda_setor (setor_id, demanda_id);
create index if not exists idx_demanda_cliente_cliente on demanda_cliente (cliente_id, demanda_id);
create index if not exists idx_demanda_responsavel_user on demanda_responsavel (user_id, demanda_id, is_principal);
create index if not exists idx_recorrencia_tipo on recorrencia_config (tipo, demanda_id);
