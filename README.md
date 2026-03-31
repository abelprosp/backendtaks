# LUXUS DEMANDAS Backend

API do sistema LUXUS DEMANDAS com duas camadas:

- backend principal em NestJS + TypeScript
- host de deploy em ASP.NET Core (C#) para Docker/VPS

## Stack

- NestJS 10
- TypeScript
- ASP.NET Core 8
- Supabase
- Prisma schema/client auxiliar
- JWT para autenticacao

## Arquitetura

- `backend-csharp`: host C# que sobe e proxya a API atual para deploy em Docker
- `src/auth`: login, refresh, guards e bootstrap de autenticacao
- `src/users`: usuarios e perfis
- `src/setores`: setores
- `src/clientes`: clientes
- `src/demandas`: regras principais de negocio, anexos, observacoes, dashboard e busca IA
- `src/templates`: templates de demandas
- `src/health`: healthcheck
- `supabase/`: schema SQL, seed e migrations

## Rodando localmente

```bash
cp .env.example .env
npm install
npm run build
npm run start:dev
```

API local padrao: `http://localhost:4000`

Healthcheck: `GET /health`

## Rodando localmente com o host C#

Se voce quiser testar o mesmo entrypoint usado no container:

```bash
cp .env.example .env
npm install
npm run build

dotnet publish backend-csharp/LuxusDemandas.Host.csproj -c Release -o /tmp/luxus-csharp-host
PORT=8080 NODE_BACKEND_PORT=5000 NODE_BACKEND_PATH=$(pwd) \
  dotnet /tmp/luxus-csharp-host/LuxusDemandas.Host.dll
```

Nesse modo:

- o host ASP.NET Core responde em `http://localhost:8080`
- a API NestJS sobe internamente em `http://127.0.0.1:5000`
- `GET /health` valida as duas camadas

## Deploy em Docker via C#

O caminho recomendado agora e:

- usar `Dockerfile` na raiz do backend
- usar `docker-compose.vps.yml` quando houver VPS com HTTPS
- subir o host ASP.NET Core
- deixar esse host iniciar internamente a API atual em Node

Isso preserva as funcionalidades existentes sem reescrever a regra de negocio inteira de uma vez.

## Scripts

- `npm run build`
- `npm run start`
- `npm run start:dev`
- `npm run start:prod`
- `npm run prisma:generate`
- `npm run prisma:migrate`
- `npm run prisma:studio`

## Variaveis de ambiente

Veja `.env.example`.

Principais:

- `NODE_ENV`
- `PORT`
- `FRONTEND_URL`
- `FRONTEND_ORIGIN`
- `SUPABASE_URL`
- `SUPABASE_SERVICE_ROLE_KEY`
- `DATABASE_URL`
- `DIRECT_URL`
- `JWT_SECRET`
- `JWT_REFRESH_SECRET`
- `SUPABASE_STORAGE_BUCKET`
- `NODE_BACKEND_PORT` (host C# / Docker)
- `NODE_BACKEND_PATH` (host C# / Docker)
- `API_DOMAIN` (HTTPS via Caddy no VPS)
- `CADDY_EMAIL` (HTTPS via Caddy no VPS)

## Deploy

Fluxo recomendado:

- backend em Docker em VPS/container
- frontend na Vercel
- banco e storage no Supabase

Guia detalhado em `DEPLOY.md`.

## Observacao importante sobre C#

O projeto nao teve a regra de negocio reescrita para C# de uma vez. Para manter compatibilidade e reduzir risco, o que foi preparado e:

- host de deploy em C# para Docker/VPS
- backend funcional existente preservado em NestJS + TypeScript

Isso permite publicar agora sem quebrar a API atual e deixa aberta uma migracao gradual para ASP.NET Core no futuro, se essa decisao for mantida.
