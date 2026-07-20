/**
 * Normaliza CRLF, CR isolado e separadores Unicode para LF.
 * Preserva parágrafo com uma linha em branco (\n\n); 3+ quebras viram no máximo \n\n.
 */
export function normalizeDemandaMultilineText(value: string | null | undefined): string {
  return String(value ?? '')
    .replace(/\r\n/g, '\n')
    .replace(/\r/g, '\n')
    .replace(/\u2028/g, '\n')
    .replace(/\u2029/g, '\n')
    .replace(/\n{3,}/g, '\n\n');
}