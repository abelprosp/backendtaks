# backend-csharp-api

API principal do backend em ASP.NET Core 8.

Estado atual desta pasta:

- health implementado
- auth implementado
- users completo implementado
- setores completo implementado
- clientes completo implementado
- templates completo implementado
- demandas completo implementado
- busca por IA implementada
- anexos com Supabase Storage implementados
- revisão de mensagens com IA implementada em `POST /assistente/revisar-mensagem`

Objetivo desta base:

- manter o backend principal em C#
- preservar contratos HTTP próximos da implementação legada
- concentrar a inteligência de negócio e de comunicação em ASP.NET Core

Contexto institucional da IA:

- arquivos em `Knowledge/`
- conteúdo extraído do onboarding interno e resumido com apoio de informações públicas oficiais do Grupo Luxus

Importante:

- esta pasta é a origem do deploy principal do backend via `Dockerfile` da raiz
- o backend NestJS antigo permanece no repositório apenas como legado técnico
- para revisão de mensagens e busca IA, `OPENAI_API_KEY` precisa estar configurada
