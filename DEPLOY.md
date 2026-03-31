# Deploy LUXUS DEMANDAS Backend

## Destino de producao

- Backend: Fly.io com Docker
- Banco e storage: Supabase
- Frontend consumidor: Vercel

## Preparacao ja aplicada

- `PORT` suportado no bootstrap
- bind em `0.0.0.0`
- `GET /health`
- CORS por `FRONTEND_URL` e `FRONTEND_ORIGIN`
- host C# em `backend-csharp/`
- `backend-csharp/Dockerfile`
- `fly.toml`
- `start:prod`
- `.env.example` padronizado

## Rodando localmente

```bash
cp .env.example .env
npm install
npm run build
npm run start:dev
```

## Variaveis de ambiente

Minimas:

- `SUPABASE_URL`
- `SUPABASE_SERVICE_ROLE_KEY`
- `DATABASE_URL`
- `DIRECT_URL`
- `JWT_SECRET`

Recomendadas em producao:

- `NODE_ENV=production`
- `PORT`
- `FRONTEND_URL`
- `JWT_REFRESH_SECRET`
- `SUPABASE_STORAGE_BUCKET`
- `NODE_BACKEND_PORT`
- `NODE_BACKEND_PATH`

## Fly.io

1. Instale `flyctl`
2. Rode `fly auth login`
3. Edite `fly.toml` e ajuste o nome da app se necessario
4. Configure os secrets
5. Rode `fly launch --copy-config --ha=false` se a app ainda nao existir
6. Rode `fly deploy`

Configuracao declarativa:

- `fly.toml`
- `backend-csharp/Dockerfile`

### Secrets recomendados

Use `fly secrets set` para definir:

- `SUPABASE_URL`
- `SUPABASE_SERVICE_ROLE_KEY`
- `SUPABASE_ANON_KEY`
- `DATABASE_URL`
- `DIRECT_URL`
- `JWT_SECRET`
- `JWT_REFRESH_SECRET`
- `JWT_EXPIRES_IN`
- `REFRESH_EXPIRES_IN`
- `FRONTEND_URL`
- `FRONTEND_ORIGIN`
- `SUPABASE_STORAGE_BUCKET`
- `OPENAI_API_KEY` se usar IA

Exemplo:

```bash
fly secrets set \
  SUPABASE_URL=... \
  SUPABASE_SERVICE_ROLE_KEY=... \
  DATABASE_URL=... \
  DIRECT_URL=... \
  JWT_SECRET=... \
  JWT_REFRESH_SECRET=... \
  FRONTEND_URL=https://seu-frontend.vercel.app
```

Variaveis declarativas ja previstas em `fly.toml`:

- `PORT=8080`
- `NODE_BACKEND_PORT=5000`
- `NODE_BACKEND_PATH=/app/node-backend`

Normalmente nao e necessario sobrescrever essas tres no Fly.io.

## Supabase

Projeto novo:

1. aplicar `supabase/schema.sql`
2. aplicar `supabase/seed.sql`

Projeto existente:

1. aplicar `supabase/migrations/*.sql` em ordem alfabetica

## Checklist

- [ ] envs configuradas
- [ ] SQL aplicado no Supabase
- [ ] backend respondendo em `/health`
- [ ] host C# subindo a API Node interna sem erro
- [ ] CORS apontando para a URL final do frontend
- [ ] `fly deploy` concluido sem erro
