# Code Review LUXUS DEMANDAS

## Estado atual

- Frontend principal em Next.js + TypeScript, fora deste repositório, publicado na Vercel.
- Backend principal em ASP.NET Core 8 + C# em `backend-csharp-api/`.
- Banco e storage em Supabase.
- Backend NestJS antigo mantido em `src/` apenas como legado técnico e referência de migração.

## Alocação por parte

- `backend-csharp-api/`
  API principal em C#.
  Controllers, services, models, auth JWT, healthcheck, busca IA e anexos.

- `Dockerfile`
  Container principal do backend.
  Agora publica diretamente a API `backend-csharp-api`.

- `render.yaml`
  Declaração do serviço no Render.
  Mantém `PORT`, `/health`, envs do Supabase e CORS por `FRONTEND_URL` e `FRONTEND_ORIGIN`.

- `supabase/`
  SQL, schema base, seed e migrations.

- `src/`
  Backend NestJS legado.
  Não é mais o alvo principal do deploy.

## Linguagens usadas e por quê

- C#
  Usado em `backend-csharp-api/**/*.cs`.
  Foi escolhido para consolidar o backend principal em ASP.NET Core com tipagem forte, autenticação nativa e deploy limpo em container.

- TypeScript
  Ainda existe em `src/**/*.ts` como backend legado e no frontend fora deste repositório.
  Foi mantido para referência técnica e rollback controlado, sem apagar a implementação anterior.

- SQL
  Usado em `supabase/schema.sql` e `supabase/migrations/*.sql`.
  Continua sendo a forma mais controlada para schema, índices, enums e RPCs no Supabase/Postgres.

- Dockerfile/YAML/Markdown
  Usados para containerização, deploy e documentação.

## Pontos fortes

- API C# cobre o conjunto principal de rotas do sistema.
- `/health` está padronizado.
- Upload e download de anexos foram validados localmente na API C#.
- Busca por IA foi validada localmente com normalização de filtros mais restrita.
- O deploy ficou mais simples porque o container sobe direto a API C#.

## Pontos de atenção

- O Render Free continua sujeito a sleep.
- O backend legado continua no repositório e exige disciplina para não confundir a origem do deploy.
- Segredos expostos durante testes precisam ser rotacionados.

## Conclusão

O backend principal deste repositório agora está alocado em `backend-csharp-api/` e em C#. O NestJS foi preservado apenas como legado técnico. O próximo passo operacional é publicar esse estado no GitHub e forçar um novo deploy do Render para que a produção passe a usar a API ASP.NET Core como origem real.
