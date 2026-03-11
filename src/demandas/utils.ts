export function stringToBool(value: unknown): boolean | undefined {
  if (value === undefined || value === null || value === '') return undefined;
  if (typeof value === 'boolean') return value;
  if (typeof value === 'string') {
    const v = value.toLowerCase();
    if (v === 'true' || v === '1' || v === 'sim') return true;
    if (v === 'false' || v === '0' || v === 'nao') return false;
  }
  return undefined;
}
