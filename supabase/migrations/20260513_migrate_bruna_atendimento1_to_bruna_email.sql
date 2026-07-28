-- =============================================================================
-- Migração: responsáveis da Bruna estavam no usuário atendimento1@luxustelefonia
--           e devem passar para bruna@luxustelefonia.com.br
--
-- Escopo: só toca linhas onde user_id = UUID de atendimento1@... (usa índice
--         demanda_responsavel_user_id_idx etc.) — não percorre 15 mil demandas.
--
-- 1) Rode o BLOCO DE PRÉ-VISUALIZAÇÃO abaixo no SQL Editor e confira os counts.
-- 2) Rode o BLOCO DE APLICAÇÃO (DO $$ ... $$).
-- =============================================================================

-- -----------------------------------------------------------------------------
-- BLOCO DE PRÉ-VISUALIZAÇÃO (rode antes; é só SELECT)
-- -----------------------------------------------------------------------------
/*
WITH u AS (
  SELECT
    (SELECT id FROM public."User" WHERE lower(email) = lower('atendimento1@luxustelefonia.com.br')) AS wrong_id,
    (SELECT id FROM public."User" WHERE lower(email) = lower('bruna@luxustelefonia.com.br')) AS bruna_id
)
SELECT 'demanda_responsavel' AS tabela, count(*)::bigint
FROM public.demanda_responsavel dr
CROSS JOIN u
WHERE u.wrong_id IS NOT NULL AND dr.user_id = u.wrong_id
UNION ALL
SELECT 'subtarefa', count(*)::bigint
FROM public.subtarefa s
CROSS JOIN u
WHERE u.wrong_id IS NOT NULL AND s.responsavel_user_id = u.wrong_id
UNION ALL
SELECT 'template_responsavel', count(*)::bigint
FROM public.template_responsavel tr
CROSS JOIN u
WHERE u.wrong_id IS NOT NULL AND tr.user_id = u.wrong_id
UNION ALL
SELECT 'template_subtarefa', count(*)::bigint
FROM public.template_subtarefa ts
CROSS JOIN u
WHERE u.wrong_id IS NOT NULL AND ts.responsavel_user_id = u.wrong_id
UNION ALL
SELECT 'demanda_private_viewer', count(*)::bigint
FROM public.demanda_private_viewer dpv
CROSS JOIN u
WHERE u.wrong_id IS NOT NULL AND dpv.user_id = u.wrong_id;

-- Lista de protocolos das demandas afetadas (apenas onde atendimento1 é responsável)
WITH u AS (
  SELECT (SELECT id FROM public."User" WHERE lower(email) = lower('atendimento1@luxustelefonia.com.br')) AS wrong_id
)
SELECT d.protocolo, d.id AS demanda_id, dr.is_principal
FROM public.demanda_responsavel dr
JOIN public."Demanda" d ON d.id = dr.demanda_id
CROSS JOIN u
WHERE u.wrong_id IS NOT NULL AND dr.user_id = u.wrong_id
ORDER BY d.created_at DESC;
*/

-- -----------------------------------------------------------------------------
-- BLOCO DE APLICAÇÃO
-- -----------------------------------------------------------------------------
DO $$
DECLARE
  v_wrong uuid;
  v_bruna uuid;
  n int;
BEGIN
  SELECT id INTO v_wrong FROM public."User" WHERE lower(email) = lower('atendimento1@luxustelefonia.com.br');
  SELECT id INTO v_bruna FROM public."User" WHERE lower(email) = lower('bruna@luxustelefonia.com.br');

  IF v_bruna IS NULL THEN
    RAISE EXCEPTION 'Usuário com email bruna@luxustelefonia.com.br não existe na tabela "User".';
  END IF;

  IF v_wrong IS NULL THEN
    RAISE NOTICE 'Nenhum usuário atendimento1@luxustelefonia.com.br encontrado; nada a migrar.';
    RETURN;
  END IF;

  IF v_wrong = v_bruna THEN
    RAISE EXCEPTION 'IDs de origem e destino coincidem; abortando.';
  END IF;

  -- demanda_responsavel: se já existir linha da Bruna, preservar e fundir is_principal
  UPDATE public.demanda_responsavel drb
  SET is_principal = true
  FROM public.demanda_responsavel drw
  WHERE drb.demanda_id = drw.demanda_id
    AND drb.user_id = v_bruna
    AND drw.user_id = v_wrong
    AND drw.is_principal = true
    AND drb.is_principal = false;
  GET DIAGNOSTICS n = ROW_COUNT;
  RAISE NOTICE 'demanda_responsavel: % linhas Bruna receberam is_principal=true (merge)', n;

  UPDATE public.demanda_responsavel dr
  SET user_id = v_bruna
  WHERE dr.user_id = v_wrong
    AND NOT EXISTS (
      SELECT 1
      FROM public.demanda_responsavel dr2
      WHERE dr2.demanda_id = dr.demanda_id
        AND dr2.user_id = v_bruna
    );
  GET DIAGNOSTICS n = ROW_COUNT;
  RAISE NOTICE 'demanda_responsavel: % linhas atualizadas user_id -> Bruna', n;

  DELETE FROM public.demanda_responsavel drw
  WHERE drw.user_id = v_wrong
    AND EXISTS (
      SELECT 1
      FROM public.demanda_responsavel drb
      WHERE drb.demanda_id = drw.demanda_id
        AND drb.user_id = v_bruna
    );
  GET DIAGNOSTICS n = ROW_COUNT;
  RAISE NOTICE 'demanda_responsavel: % linhas duplicadas (atendimento1) removidas', n;

  -- subtarefas
  UPDATE public.subtarefa s
  SET responsavel_user_id = v_bruna
  WHERE s.responsavel_user_id = v_wrong;
  GET DIAGNOSTICS n = ROW_COUNT;
  RAISE NOTICE 'subtarefa: % linhas atualizadas', n;

  -- template_responsavel
  UPDATE public.template_responsavel trb
  SET is_principal = true
  FROM public.template_responsavel trw
  WHERE trb.template_id = trw.template_id
    AND trb.user_id = v_bruna
    AND trw.user_id = v_wrong
    AND trw.is_principal = true
    AND trb.is_principal = false;
  GET DIAGNOSTICS n = ROW_COUNT;
  RAISE NOTICE 'template_responsavel: % merges is_principal', n;

  UPDATE public.template_responsavel tr
  SET user_id = v_bruna
  WHERE tr.user_id = v_wrong
    AND NOT EXISTS (
      SELECT 1
      FROM public.template_responsavel tr2
      WHERE tr2.template_id = tr.template_id
        AND tr2.user_id = v_bruna
    );
  GET DIAGNOSTICS n = ROW_COUNT;
  RAISE NOTICE 'template_responsavel: % linhas atualizadas', n;

  DELETE FROM public.template_responsavel trw
  WHERE trw.user_id = v_wrong
    AND EXISTS (
      SELECT 1
      FROM public.template_responsavel trb
      WHERE trb.template_id = trw.template_id
        AND trb.user_id = v_bruna
    );
  GET DIAGNOSTICS n = ROW_COUNT;
  RAISE NOTICE 'template_responsavel: % linhas duplicadas removidas', n;

  -- template_subtarefa
  UPDATE public.template_subtarefa ts
  SET responsavel_user_id = v_bruna
  WHERE ts.responsavel_user_id = v_wrong;
  GET DIAGNOSTICS n = ROW_COUNT;
  RAISE NOTICE 'template_subtarefa: % linhas atualizadas', n;

  -- demandas privadas: espectadores
  DELETE FROM public.demanda_private_viewer dpv
  WHERE dpv.user_id = v_wrong
    AND EXISTS (
      SELECT 1
      FROM public.demanda_private_viewer d2
      WHERE d2.demanda_id = dpv.demanda_id
        AND d2.user_id = v_bruna
    );
  GET DIAGNOSTICS n = ROW_COUNT;
  RAISE NOTICE 'demanda_private_viewer: % duplicatas removidas', n;

  UPDATE public.demanda_private_viewer dpv
  SET user_id = v_bruna
  WHERE dpv.user_id = v_wrong;
  GET DIAGNOSTICS n = ROW_COUNT;
  RAISE NOTICE 'demanda_private_viewer: % linhas atualizadas', n;

  RAISE NOTICE 'Migração concluída (origem atendimento1 -> bruna@).';
END $$;
