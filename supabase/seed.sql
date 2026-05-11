-- ============================================================
-- LUXUS DEMANDAS - Dados iniciais (opcional)
-- Execute depois do schema.sql. Insere setores e perfis (roles).
-- ============================================================

INSERT INTO "Setor" ("id", "name", "slug") VALUES
  (gen_random_uuid(), 'Assessoria Fixa', 'assessoria_fixa'),
  (gen_random_uuid(), 'Assessoria Móvel', 'assessoria_movel'),
  (gen_random_uuid(), 'Comercial', 'comercial'),
  (gen_random_uuid(), 'Corretora', 'corretora'),
  (gen_random_uuid(), 'Financeiro', 'financeiro'),
  (gen_random_uuid(), 'Gestão', 'gestao'),
  (gen_random_uuid(), 'Jurídico', 'juridico'),
  (gen_random_uuid(), 'Marketing', 'marketing'),
  (gen_random_uuid(), 'TI', 'ti'),
  (gen_random_uuid(), 'Outro', 'outro')
ON CONFLICT ("slug") DO UPDATE SET "name" = EXCLUDED."name";

INSERT INTO "Role" ("id", "name", "slug") VALUES
  (gen_random_uuid(), 'Administrador', 'admin'::"RoleSlug"),
  (gen_random_uuid(), 'Gestor', 'gestor'::"RoleSlug"),
  (gen_random_uuid(), 'Colaborador', 'colaborador'::"RoleSlug"),
  (gen_random_uuid(), 'Cliente', 'cliente'::"RoleSlug")
ON CONFLICT ("slug") DO UPDATE SET "name" = EXCLUDED."name";
