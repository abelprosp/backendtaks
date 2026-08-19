# LUXUS DEMANDAS Backend

API do sistema LUXUS DEMANDAS com backend principal em ASP.NET Core (C#).

## Stack

- ASP.NET Core 8
- C#
- Supabase
- JWT para autenticacao
- OpenAI opcional para busca IA
- NestJS/TypeScript legado preservado no repositório como referencia técnica

## Arquitetura

- `backend-csharp-api/`: API principal em ASP.NET Core 8
- `backend-csharp-api/Controllers`: rotas HTTP da API C#
- `backend-csharp-api/Services`: regra de negocio migrada
- `backend-csharp-api/Models`: contratos HTTP
- `backend-csharp-api/Security`: auth JWT e claims
- `src/health`: healthcheck
- `supabase/`: schema SQL, seed e migrations
- `src/`: backend NestJS legado mantido apenas para referencia e fallback tecnico

## Rodando localmente

```bash
cp .env.example .env
export PATH=/home/abel/.dotnet:$PATH
dotnet build backend-csharp-api/LuxusDemandas.Api.csproj
ASPNETCORE_URLS=http://127.0.0.1:4000 dotnet run --project backend-csharp-api/LuxusDemandas.Api.csproj --no-build
```

API local padrao: `http://localhost:4000`

Healthcheck: `GET /health`

## Deploy em container gratis

O caminho recomendado agora e:

- usar `Dockerfile` na raiz do backend
- usar `render.yaml` para criar um Web Service no Render
- publicar diretamente a API `backend-csharp-api`

O backend NestJS antigo permanece no repositório, mas nao e mais o alvo principal do container.

## Variaveis de ambiente

Veja `.env.example`.

Principais:

- `NODE_ENV`
- `PORT`
- `FRONTEND_URL`
- `FRONTEND_ORIGIN`
- `SUPABASE_URL`
- `SUPABASE_ANON_KEY`
- `SUPABASE_SERVICE_ROLE_KEY`
- `JWT_SECRET`
- `JWT_REFRESH_SECRET`
- `JWT_EXPIRES_IN`
- `REFRESH_EXPIRES_IN`
- `SUPABASE_STORAGE_BUCKET`
- `OPENAI_API_KEY` (opcional)
- `LUXUS_PARCEIROS_INTEGRATION_KEY` para aceitar demandas do Parceiros
- `LUXUS_PARCEIROS_CALLBACK_URL` para devolver status e resolução
- `LUXUS_PARCEIROS_TECHNICAL_USER_EMAIL` para o criador automático

Opcional/legado:

- `DATABASE_URL`
- `DIRECT_URL`

## Deploy

Fluxo recomendado:

- backend em container gratuito no Render
- frontend na Vercel
- banco e storage no Supabase

Guia detalhado em `DEPLOY.md`.

## Deploy em VPS compartilhada (docker-compose.vps.yml)

Quando o host já usa Nginx/Caddy na 80/443, configure portas alternativas:

- `CADDY_HTTP_PORT` / `CADDY_HTTPS_PORT` — portas expostas pelo Caddy no compose
- `CADDY_HTTP_BIND` / `CADDY_HTTPS_BIND` — bind (padrão 0.0.0.0)

Exemplo: `CADDY_HTTP_PORT=8080` e proxy reverso no Nginx do host apontando para essa porta.

## Estado atual da migracao

- a API C# em `backend-csharp-api/` cobre auth, users, setores, clientes, templates, demandas, observacoes, anexos, dashboard e busca por IA
- o Dockerfile da raiz foi ajustado para publicar a API C# como backend principal
- o backend NestJS legado continua versionado apenas para consulta tecnica e rollback controlado

## Integração Luxus Parceiros

A integração adiciona endpoints servidor-a-servidor sem alterar o frontend nem
o fluxo normal do Luxus Task. Demandas internas continuam sendo criadas
normalmente. Demandas externas aparecem com o criador automático
`LUXUSPARCEIROS` e com o responsável selecionado no sistema Parceiros.

Antes do deploy, aplique no Supabase:

`supabase/migrations/20260728_luxus_parceiros_integration.sql`

Endpoints protegidos pelo header `x-integration-key`:

- `GET /integrations/luxus-parceiros/responsaveis`
- `POST /integrations/luxus-parceiros/demandas`
- `GET /integrations/luxus-parceiros/demandas/{externalRequestId}`

Atualizações e conclusão enviam callback assinado ao Luxus Parceiros. Falhas no
callback não bloqueiam o trabalho nem a conclusão dentro do Task.
