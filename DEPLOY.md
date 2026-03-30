# Deploy LUXUS DEMANDAS Backend

## Destino de producao

- Backend: Railway
- Banco e storage: Supabase
- Frontend consumidor: Vercel

## Preparacao ja aplicada

- `PORT` suportado no bootstrap
- bind em `0.0.0.0`
- `GET /health`
- CORS por `FRONTEND_URL` e `FRONTEND_ORIGIN`
- `railway.json`
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

## Railway

1. Criar servico apontando para este repositorio
2. Definir root directory como `.`
3. Cadastrar as envs
4. Fazer deploy
5. Validar `GET /health`

Configuracao declarativa:

- `railway.json`

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
- [ ] CORS apontando para a URL final do frontend
