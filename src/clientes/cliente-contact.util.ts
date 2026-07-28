/**
 * Normaliza listas digitadas com separador ";".
 * Aceita espaços antes/depois de cada ";" e entre itens; remove vazios;
 * grava no padrão "a; b; c" (um espaço após cada ponto e vírgula).
 */
export function normalizeClienteMultivalueField(raw: string | null | undefined): string | null {
  if (raw == null) return null;
  const parts = String(raw)
    .split(';')
    .map((s) => s.trim())
    .filter(Boolean);
  if (!parts.length) return null;
  return parts.join('; ');
}
