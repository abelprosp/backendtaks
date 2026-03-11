# SQL para Supabase

Execute no **SQL Editor** do Supabase (Dashboard do projeto > SQL Editor > New query), nesta ordem:

1. **schema.sql** — Cria enums, tabelas, FKs, índices e triggers.
2. **seed.sql** — Insere setores (Assessoria Fixa, Comercial, TI, etc.) e perfis (Administrador, Gestor, Colaborador, Cliente).

Depois configure o `.env` do backend com a URL do Supabase, chave anônima, chave restrita (service role) e as connection strings do banco (`DATABASE_URL` e `DIRECT_URL`).
