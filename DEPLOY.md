# Deploy LUXUS DEMANDAS Backend

## Destino de producao

- Backend: container gratuito no Render
- Banco e storage: Supabase
- Frontend consumidor: Vercel

## Preparacao ja aplicada

- `PORT` suportado no bootstrap
- bind em `0.0.0.0`
- `GET /health`
- CORS por `FRONTEND_URL` e `FRONTEND_ORIGIN`
- host C# em `backend-csharp/`
- `Dockerfile` na raiz
- `render.yaml`
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

## Render Free

Arquivos prontos:

- `Dockerfile`
- `render.yaml`

Passos:

1. Crie uma conta no Render
2. Clique em `New +` > `Blueprint`
3. Conecte o repositório `abelprosp/backendtaks`
4. Selecione a branch `codex/prepare-production-deploy`
5. Deixe o Render ler `render.yaml`
6. Preencha os secrets pedidos no dashboard
7. Crie o serviço

Secrets que o Render vai pedir:

- `FRONTEND_URL`
- `FRONTEND_ORIGIN`
- `SUPABASE_URL`
- `SUPABASE_SERVICE_ROLE_KEY`
- `SUPABASE_ANON_KEY`
- `DATABASE_URL`
- `DIRECT_URL`
- `OPENAI_API_KEY` se usar IA

Observacoes:

- o `render.yaml` gera `JWT_SECRET` e `JWT_REFRESH_SECRET`
- o plano configurado e `free`
- o healthcheck configurado e `/health`

## Limitacoes do plano gratis

O proprio Render informa que:

- o web service gratuito pode entrar em sleep apos 15 minutos sem trafego
- a volta pode levar cerca de 1 minuto
- o filesystem e efemero
- nao recomendam free web service para producao critica

Essas limitacoes nao impedem teste e uso leve, e o projeto ja usa Supabase para persistencia principal.

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

## Alternativa futura

Se depois voce quiser sair do free container e ir para algo mais estavel:

- usar uma VPS propria com `docker-compose.vps.yml`
- ou migrar para um container pago/sem sleep

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
- [ ] deploy do Render concluido sem erro
