# backend-csharp-api

Base inicial da migracao real do backend para ASP.NET Core.

Estado atual desta pasta:

- health implementado
- auth implementado
- bootstrap implementado
- users completo implementado
- setores completo implementado
- clientes completo implementado
- templates completo implementado
- demandas principal implementado: create, createFromTemplate, list, detail, update, delete, dashboard, exportacao e observacoes
- pendente nesta base: busca por IA e anexos

Objetivo:

- migrar a API atual do NestJS para C# modulo por modulo
- manter os contratos HTTP o mais proximos possivel
- evitar uma reescrita destrutiva do sistema que ja esta em producao

Importante:

- esta pasta ainda nao substitui o backend atual em producao
- o backend que esta publicado continua dependendo da regra principal em TypeScript
- a migracao completa ainda exige portar anexos, busca por IA e trocar o deploy para esta API C#
