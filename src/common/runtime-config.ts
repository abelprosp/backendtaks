export const APP_NAME = 'LUXUS DEMANDAS API';
export const APP_SLUG = 'luxus-demandas-backend';

function normalizeOrigin(origin: string): string {
  return origin.trim().replace(/\/$/, '');
}

function splitOrigins(raw?: string | null): string[] {
  if (!raw?.trim()) return [];
  return raw
    .split(',')
    .map((value) => normalizeOrigin(value))
    .filter(Boolean);
}

export function getNodeEnv(): string {
  return process.env.NODE_ENV?.trim() || 'development';
}

export function getPort(): number {
  const parsed = Number(process.env.PORT);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : 4000;
}

export function getAllowedOrigins(): string[] {
  const configured = [
    ...splitOrigins(process.env.FRONTEND_URL),
    ...splitOrigins(process.env.FRONTEND_ORIGIN),
  ];
  const unique = [...new Set(configured)];
  return unique.length ? unique : ['http://localhost:3000'];
}

function isKnownVercelPreview(normalizedOrigin: string): boolean {
  return (
    normalizedOrigin.endsWith('vercel.app') &&
    (
      normalizedOrigin.includes('luxustasks') ||
      normalizedOrigin.includes('luxus-tasks') ||
      normalizedOrigin.includes('luxusdemandas') ||
      normalizedOrigin.includes('luxus-demandas')
    )
  );
}

export function resolveAllowedOrigin(
  origin: string | undefined,
  allowedOrigins: string[],
): string | false {
  if (!origin) return false;
  const normalized = normalizeOrigin(origin);
  if (allowedOrigins.includes(normalized)) return origin;
  if (isKnownVercelPreview(normalized)) return origin;
  return false;
}
