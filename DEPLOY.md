# Deploy LUXUS DEMANDAS Backend

## Destino de producao

- Backend: Docker em VPS/container generico
- Banco e storage: Supabase
- Frontend consumidor: Vercel

## Preparacao ja aplicada

- `PORT` suportado no bootstrap
- bind em `0.0.0.0`
- `GET /health`
- CORS por `FRONTEND_URL` e `FRONTEND_ORIGIN`
- host C# em `backend-csharp/`
- `Dockerfile` na raiz
- `docker-compose.vps.yml`
- `Caddyfile`
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

Minimas para o backend:

- `SUPABASE_URL`
- `SUPABASE_SERVICE_ROLE_KEY`
- `JWT_SECRET`

Recomendadas em producao:

- `NODE_ENV=production`
- `PORT`
- `FRONTEND_URL`
- `JWT_REFRESH_SECRET`
- `SUPABASE_STORAGE_BUCKET`
- `NODE_BACKEND_PORT`
- `NODE_BACKEND_PATH`

Somente para o proxy HTTPS do VPS:

- `API_DOMAIN`
- `CADDY_EMAIL`

## Docker local

Build:

```bash
docker build -t luxus-demandas-api .
```

Run:

```bash
docker run --rm -p 8080:8080 --env-file .env \
  -e NODE_ENV=production \
  -e PORT=8080 \
  -e NODE_BACKEND_PORT=5000 \
  -e NODE_BACKEND_PATH=/app/node-backend \
  luxus-demandas-api
```

Healthcheck:

```bash
curl http://localhost:8080/health
```

## VPS com HTTPS

Arquivos prontos:

- `Dockerfile`
- `docker-compose.vps.yml`
- `Caddyfile`

Passos:

1. Crie uma VPS com Docker e Docker Compose
2. Aponte um dominio ou subdominio para o IP da VPS
3. Copie o projeto para a VPS
4. Crie `.env` a partir de `.env.example`
5. Ajuste `FRONTEND_URL` para a URL da Vercel
6. Ajuste `API_DOMAIN` para o dominio da API
7. Rode `docker compose -f docker-compose.vps.yml up -d --build`

Exemplo:

```bash
cp .env.example .env
nano .env
docker compose -f docker-compose.vps.yml up -d --build
```

O `Caddyfile` vai:

- responder em HTTPS
- renovar certificado automaticamente
- fazer proxy para o container `api`

## Provedor recomendado

Para VPS gratis de verdade, o caminho mais consistente tende a ser Oracle Cloud Always Free.

Alternativa mais simples, mas menos estavel para producao:

- um container gratuito em plataforma gerenciada que rode Docker, como Koyeb Free

## Supabase

Projeto novo:

1. aplicar `supabase/schema.sql`
2. aplicar `supabase/seed.sql`

Projeto existente:

1. aplicar `supabase/migrations/*.sql` em ordem alfabetica

## Checklist

- [ ] envs configuradas
- [ ] SQL aplicado no Supabase
- [ ] dominio apontando para a VPS
- [ ] backend respondendo em `/health`
- [ ] host C# subindo a API Node interna sem erro
- [ ] CORS apontando para a URL final do frontend
- [ ] containers `api` e `caddy` saudaveis
