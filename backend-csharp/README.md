# backend-csharp

Host em ASP.NET Core para deploy em Docker.

Objetivo:

- servir como entrypoint em C#
- subir o backend atual em Node internamente
- expor a API existente sem quebrar o contrato do frontend

Isso evita uma reescrita arriscada de toda a regra de negocio de uma vez.

## Variaveis

- `PORT`: porta publica do container
- `NODE_BACKEND_PORT`: porta interna do backend Node
- `NODE_BACKEND_PATH`: caminho interno do backend Node no container

As demais variaveis de banco, JWT, Supabase e OpenAI continuam as mesmas do backend atual.
