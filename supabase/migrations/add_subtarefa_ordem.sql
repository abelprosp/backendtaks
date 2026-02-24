-- Ordem das subtarefas para permitir inserir entre existentes e reordenar
ALTER TABLE "subtarefa"
  ADD COLUMN IF NOT EXISTS "ordem" INTEGER NOT NULL DEFAULT 0;

-- Garante que subtarefas existentes tenham ordem sequencial (opcional)
WITH numbered AS (
  SELECT id, ROW_NUMBER() OVER (PARTITION BY demanda_id ORDER BY id) - 1 AS rn
  FROM "subtarefa"
)
UPDATE "subtarefa" s SET ordem = numbered.rn FROM numbered WHERE s.id = numbered.id;
