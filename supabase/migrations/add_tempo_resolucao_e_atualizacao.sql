-- Controles de tempo de resolução e atualização por demanda
-- Tempo de resolução: quando a demanda foi concluída (resolvido_em)
-- Tempo de atualização com observação: última vez que houve observação (ultima_observacao_em)

ALTER TABLE "Demanda"
  ADD COLUMN IF NOT EXISTS "resolvido_em" TIMESTAMP(3),
  ADD COLUMN IF NOT EXISTS "ultima_observacao_em" TIMESTAMP(3);

COMMENT ON COLUMN "Demanda"."resolvido_em" IS 'Data/hora em que a demanda foi concluída (status = concluido)';
COMMENT ON COLUMN "Demanda"."ultima_observacao_em" IS 'Data/hora da última observação registrada na demanda';
