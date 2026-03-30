# SQL e migrations do Supabase

## Projeto novo

Execute no **SQL Editor** do Supabase nesta ordem:

1. `schema.sql`
2. `seed.sql`
3. `create-master-user.sql` se quiser provisionar o usuário inicial por SQL

## Projeto já existente

Se o banco já possui o schema base, aplique também os arquivos de `migrations/` em ordem alfabética:

```bash
ls -1 migrations/*.sql | sort
```

Arquivos atuais:

- `migrations/add_subtarefa_ordem.sql`
- `migrations/add_tempo_resolucao_e_atualizacao.sql`
- `migrations/20260313_add_demandas_rpc_performance.sql`
- `migrations/20260313_add_ia_busca_log.sql`
- `migrations/20260313_add_ia_search_indexes.sql`
- `migrations/20260313_add_templates_dashboard_ia_rpc_performance.sql`
- `migrations/20260313_template_subtarefa_responsavel_recorrencia.sql`
- `migrations/20260317_add_demandas_listing_detail_rpc.sql`
- `migrations/20260317_update_demanda_statuses.sql`

## Fluxo recomendado para produção

1. aplique SQL no Supabase antes do deploy da aplicação
2. valide as mudanças em staging ou em um projeto de homologação
3. publique o backend na Railway
4. publique o frontend na Vercel

## Observações

- O projeto não usa `prisma migrate` como fonte principal de verdade do banco.
- O Prisma continua útil para `schema.prisma` e geração do client, mas o schema operacional está nos arquivos SQL desta pasta.
- O backend precisa de `SUPABASE_URL`, `SUPABASE_SERVICE_ROLE_KEY`, `DATABASE_URL` e `DIRECT_URL` corretamente preenchidos.
