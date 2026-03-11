-- ============================================================
-- Luxus Tasks - Setup completo Supabase (schema + migrations + seed + admin)
-- Gerado automaticamente a partir dos SQLs do projeto
-- ============================================================

CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- ================= schema.sql =================
-- ============================================================
-- Luxus Tasks - Schema SQL para Supabase
-- Cole este conteúdo no SQL Editor do Supabase (Dashboard > SQL Editor)
-- e execute. Depois rode o seed (seed.sql) se quiser setores e perfis.
-- ============================================================

-- Enums (cria só se não existir, para poder rodar o script de novo sem erro)
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'RoleSlug') THEN
    CREATE TYPE "RoleSlug" AS ENUM ('admin', 'gestor', 'colaborador', 'cliente');
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'DemandaStatus') THEN
    CREATE TYPE "DemandaStatus" AS ENUM ('em_aberto', 'concluido', 'pendente', 'pendente_de_resposta');
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'RecorrenciaTipo') THEN
    CREATE TYPE "RecorrenciaTipo" AS ENUM ('diaria', 'semanal', 'quinzenal', 'mensal');
  END IF;
END
$$;

-- Tabelas (ordem respeitando FKs)
CREATE TABLE "User" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "email" TEXT NOT NULL UNIQUE,
  "password_hash" TEXT NOT NULL,
  "name" TEXT NOT NULL,
  "active" BOOLEAN NOT NULL DEFAULT true,
  "created_at" TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  "updated_at" TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE "Role" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "name" TEXT NOT NULL,
  "slug" "RoleSlug" NOT NULL UNIQUE
);

CREATE TABLE "user_role" (
  "user_id" UUID NOT NULL REFERENCES "User"("id") ON DELETE CASCADE,
  "role_id" UUID NOT NULL REFERENCES "Role"("id") ON DELETE CASCADE,
  PRIMARY KEY ("user_id", "role_id")
);

CREATE TABLE "Setor" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "name" TEXT NOT NULL,
  "slug" TEXT NOT NULL UNIQUE
);

CREATE TABLE "user_setor_permissao" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "user_id" UUID NOT NULL REFERENCES "User"("id") ON DELETE CASCADE,
  "setor_id" UUID NOT NULL REFERENCES "Setor"("id") ON DELETE CASCADE,
  "can_create" BOOLEAN NOT NULL DEFAULT false,
  "can_edit" BOOLEAN NOT NULL DEFAULT false,
  "can_delete" BOOLEAN NOT NULL DEFAULT false,
  "can_view" BOOLEAN NOT NULL DEFAULT true,
  UNIQUE ("user_id", "setor_id")
);

CREATE TABLE "Cliente" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "name" TEXT NOT NULL,
  "active" BOOLEAN NOT NULL DEFAULT true
);

CREATE TABLE "Demanda" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "protocolo" TEXT NOT NULL UNIQUE,
  "assunto" TEXT NOT NULL,
  "prioridade" BOOLEAN NOT NULL DEFAULT false,
  "prazo" DATE,
  "status" "DemandaStatus" NOT NULL DEFAULT 'em_aberto',
  "criador_id" UUID NOT NULL REFERENCES "User"("id"),
  "observacoes_gerais" TEXT,
  "is_recorrente" BOOLEAN NOT NULL DEFAULT false,
  "demanda_origem_id" UUID REFERENCES "Demanda"("id"),
  "resolvido_em" TIMESTAMP(3),
  "ultima_observacao_em" TIMESTAMP(3),
  "created_at" TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  "updated_at" TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE "demanda_setor" (
  "demanda_id" UUID NOT NULL REFERENCES "Demanda"("id") ON DELETE CASCADE,
  "setor_id" UUID NOT NULL REFERENCES "Setor"("id") ON DELETE CASCADE,
  PRIMARY KEY ("demanda_id", "setor_id")
);

CREATE TABLE "demanda_cliente" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "demanda_id" UUID NOT NULL REFERENCES "Demanda"("id") ON DELETE CASCADE,
  "cliente_id" UUID NOT NULL REFERENCES "Cliente"("id") ON DELETE CASCADE
);

CREATE TABLE "demanda_responsavel" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "demanda_id" UUID NOT NULL REFERENCES "Demanda"("id") ON DELETE CASCADE,
  "user_id" UUID NOT NULL REFERENCES "User"("id") ON DELETE CASCADE,
  "is_principal" BOOLEAN NOT NULL DEFAULT false
);

CREATE TABLE "subtarefa" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "demanda_id" UUID NOT NULL REFERENCES "Demanda"("id") ON DELETE CASCADE,
  "titulo" TEXT NOT NULL,
  "concluida" BOOLEAN NOT NULL DEFAULT false,
  "ordem" INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE "observacao" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "demanda_id" UUID NOT NULL REFERENCES "Demanda"("id") ON DELETE CASCADE,
  "user_id" UUID NOT NULL REFERENCES "User"("id") ON DELETE CASCADE,
  "texto" TEXT NOT NULL,
  "created_at" TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE "anexo" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "demanda_id" UUID NOT NULL REFERENCES "Demanda"("id") ON DELETE CASCADE,
  "filename" TEXT NOT NULL,
  "mime_type" TEXT NOT NULL,
  "size" INTEGER NOT NULL,
  "storage_path" TEXT NOT NULL
);

CREATE TABLE "recorrencia_config" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "demanda_id" UUID NOT NULL UNIQUE REFERENCES "Demanda"("id") ON DELETE CASCADE,
  "data_base" DATE NOT NULL,
  "tipo" "RecorrenciaTipo" NOT NULL,
  "prazo_reabertura_dias" INTEGER NOT NULL
);

-- Templates
CREATE TABLE "Template" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "name" TEXT NOT NULL,
  "descricao" TEXT,
  "assunto_template" TEXT,
  "prioridade_default" BOOLEAN NOT NULL DEFAULT false,
  "observacoes_gerais_template" TEXT,
  "is_recorrente_default" BOOLEAN NOT NULL DEFAULT false,
  "recorrencia_tipo" "RecorrenciaTipo",
  "recorrencia_prazo_reabertura_dias" INTEGER,
  "criador_id" UUID NOT NULL REFERENCES "User"("id") ON DELETE CASCADE,
  "created_at" TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  "updated_at" TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE "template_setor" (
  "template_id" UUID NOT NULL REFERENCES "Template"("id") ON DELETE CASCADE,
  "setor_id" UUID NOT NULL REFERENCES "Setor"("id") ON DELETE CASCADE,
  PRIMARY KEY ("template_id", "setor_id")
);

CREATE TABLE "template_responsavel" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "template_id" UUID NOT NULL REFERENCES "Template"("id") ON DELETE CASCADE,
  "user_id" UUID NOT NULL REFERENCES "User"("id") ON DELETE CASCADE,
  "is_principal" BOOLEAN NOT NULL DEFAULT false
);

CREATE TABLE "template_subtarefa" (
  "id" UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "template_id" UUID NOT NULL REFERENCES "Template"("id") ON DELETE CASCADE,
  "titulo" TEXT NOT NULL,
  "ordem" INTEGER NOT NULL DEFAULT 0
);

-- Índices sugeridos
CREATE INDEX "Demanda_criador_id_idx" ON "Demanda"("criador_id");
CREATE INDEX "Demanda_prazo_idx" ON "Demanda"("prazo");
CREATE INDEX "Demanda_status_idx" ON "Demanda"("status");
CREATE INDEX "Demanda_created_at_idx" ON "Demanda"("created_at");
CREATE INDEX "demanda_setor_setor_id_idx" ON "demanda_setor"("setor_id");
CREATE INDEX "demanda_responsavel_user_id_idx" ON "demanda_responsavel"("user_id");
CREATE INDEX "observacao_demanda_id_idx" ON "observacao"("demanda_id");
CREATE INDEX "user_setor_permissao_user_setor_idx" ON "user_setor_permissao"("user_id", "setor_id");

-- Trigger para updated_at (opcional)
CREATE OR REPLACE FUNCTION update_updated_at()
RETURNS TRIGGER AS $$
BEGIN
  NEW."updated_at" = CURRENT_TIMESTAMP;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER "User_updated_at" BEFORE UPDATE ON "User" FOR EACH ROW EXECUTE FUNCTION update_updated_at();
CREATE TRIGGER "Demanda_updated_at" BEFORE UPDATE ON "Demanda" FOR EACH ROW EXECUTE FUNCTION update_updated_at();
CREATE TRIGGER "Template_updated_at" BEFORE UPDATE ON "Template" FOR EACH ROW EXECUTE FUNCTION update_updated_at();

-- ================= migrations/add_tempo_resolucao_e_atualizacao.sql =================
-- Controles de tempo de resolução e atualização por demanda
-- Tempo de resolução: quando a demanda foi concluída (resolvido_em)
-- Tempo de atualização com observação: última vez que houve observação (ultima_observacao_em)

ALTER TABLE "Demanda"
  ADD COLUMN IF NOT EXISTS "resolvido_em" TIMESTAMP(3),
  ADD COLUMN IF NOT EXISTS "ultima_observacao_em" TIMESTAMP(3);

COMMENT ON COLUMN "Demanda"."resolvido_em" IS 'Data/hora em que a demanda foi concluída (status = concluido)';
COMMENT ON COLUMN "Demanda"."ultima_observacao_em" IS 'Data/hora da última observação registrada na demanda';

-- ================= migrations/add_subtarefa_ordem.sql =================
-- Ordem das subtarefas para permitir inserir entre existentes e reordenar
ALTER TABLE "subtarefa"
  ADD COLUMN IF NOT EXISTS "ordem" INTEGER NOT NULL DEFAULT 0;

-- Garante que subtarefas existentes tenham ordem sequencial (opcional)
WITH numbered AS (
  SELECT id, ROW_NUMBER() OVER (PARTITION BY demanda_id ORDER BY id) - 1 AS rn
  FROM "subtarefa"
)
UPDATE "subtarefa" s SET ordem = numbered.rn FROM numbered WHERE s.id = numbered.id;

-- ================= seed.sql =================
-- ============================================================
-- Luxus Tasks - Dados iniciais (opcional)
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

-- ================= create-master-user.sql =================
-- ============================================================
-- Luxus Tasks - Criar usuário master
-- Execute no SQL Editor do Supabase (após schema.sql e seed.sql).
-- Login: redobrai@gmail.com / Amocarro4587@
-- ============================================================

-- Habilita extensão para bcrypt (Supabase já costuma ter)
CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- Insere o usuário master (senha hasheada com bcrypt)
INSERT INTO "User" ("id", "email", "password_hash", "name", "active", "created_at", "updated_at")
VALUES (
  gen_random_uuid(),
  'redobrai@gmail.com',
  crypt('Amocarro4587@', gen_salt('bf')),
  'Master',
  true,
  CURRENT_TIMESTAMP,
  CURRENT_TIMESTAMP
)
ON CONFLICT ("email") DO UPDATE SET
  "password_hash" = EXCLUDED."password_hash",
  "name" = EXCLUDED."name",
  "active" = EXCLUDED."active",
  "updated_at" = CURRENT_TIMESTAMP;

-- Atribui o perfil Administrador ao usuário master
INSERT INTO "user_role" ("user_id", "role_id")
SELECT u."id", r."id"
FROM "User" u
CROSS JOIN "Role" r
WHERE u."email" = 'redobrai@gmail.com'
  AND r."slug" = 'admin'::"RoleSlug"
ON CONFLICT ("user_id", "role_id") DO NOTHING;

-- ================= set-admin-redobrai.sql =================
-- ============================================================
-- Atribui perfil Administrador (master) ao usuário redobrai@gmail.com
-- Execute no SQL Editor do Supabase. O usuário precisa já existir na tabela "User".
-- Depois faça login novamente para o sistema reconhecer o perfil e exibir o Dashboard.
-- ============================================================

INSERT INTO "user_role" ("user_id", "role_id")
SELECT u."id", r."id"
FROM "User" u
CROSS JOIN "Role" r
WHERE u."email" = 'redobrai@gmail.com'
  AND r."slug" = 'admin'::"RoleSlug"
ON CONFLICT ("user_id", "role_id") DO NOTHING;
