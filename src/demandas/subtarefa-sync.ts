import type { SupabaseClient } from '@supabase/supabase-js';

export type SubtarefaSyncInput = {
  id?: string;
  titulo: string;
  concluida?: boolean;
  ordem?: number;
  responsavelUserId?: string | null;
};

type ExistingSubtarefaRow = {
  id: string;
  titulo: string;
  concluida: boolean;
  ordem: number;
  responsavel_user_id: string | null;
  concluida_em: string | null;
};

function buildMatchKey(titulo: string, responsavelUserId: string | null | undefined): string {
  return `${titulo.trim().toLowerCase()}|${responsavelUserId ?? ''}`;
}

function resolveConcluidaEm(
  concluida: boolean,
  wasConcluida: boolean,
  previousConcluidaEm: string | null,
): string | null {
  if (concluida && !wasConcluida) return new Date().toISOString();
  if (!concluida && wasConcluida) return null;
  return previousConcluidaEm;
}

export async function syncDemandaSubtarefas(
  sb: SupabaseClient,
  demandaId: string,
  items: SubtarefaSyncInput[],
): Promise<void> {
  const { data: existing = [] } = await sb
    .from('subtarefa')
    .select('id, titulo, concluida, ordem, responsavel_user_id, concluida_em')
    .eq('demanda_id', demandaId);

  const rows = (existing ?? []) as ExistingSubtarefaRow[];
  const existingById = new Map(rows.map((row) => [row.id, row]));
  const unmatchedExisting = [...rows];
  const keepIds = new Set<string>();
  const toInsert: Array<Record<string, unknown>> = [];

  const takeUnmatchedByKey = (key: string): ExistingSubtarefaRow | undefined => {
    const index = unmatchedExisting.findIndex(
      (row) => buildMatchKey(row.titulo, row.responsavel_user_id) === key,
    );
    if (index < 0) return undefined;
    return unmatchedExisting.splice(index, 1)[0];
  };

  for (const [index, item] of items.entries()) {
    const concluida = Boolean(item.concluida);
    const ordem = item.ordem ?? index;
    const responsavelUserId = item.responsavelUserId ?? null;
    const matched =
      (item.id ? existingById.get(item.id) : undefined)
      ?? takeUnmatchedByKey(buildMatchKey(item.titulo, responsavelUserId));

    if (matched) {
      keepIds.add(matched.id);
      await sb
        .from('subtarefa')
        .update({
          titulo: item.titulo,
          concluida,
          ordem,
          responsavel_user_id: responsavelUserId,
          concluida_em: resolveConcluidaEm(concluida, matched.concluida, matched.concluida_em),
        })
        .eq('id', matched.id);
      continue;
    }

    toInsert.push({
      demanda_id: demandaId,
      titulo: item.titulo,
      concluida,
      ordem,
      responsavel_user_id: responsavelUserId,
      concluida_em: concluida ? new Date().toISOString() : null,
    });
  }

  const deleteIds = rows.filter((row) => !keepIds.has(row.id)).map((row) => row.id);
  if (deleteIds.length) {
    await sb.from('subtarefa').delete().in('id', deleteIds);
  }
  if (toInsert.length) {
    await sb.from('subtarefa').insert(toInsert);
  }
}
