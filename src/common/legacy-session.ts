type LegacySessionOptions = {
  extractInputValue: (html: string, name: string) => string;
};

export type LegacySession = {
  get: (path: string) => Promise<string>;
  fetch: (url: string, init?: RequestInit) => Promise<Response>;
};

function readSetCookie(response: Response): string {
  const raw = (response.headers as Headers & { getSetCookie?: () => string[] }).getSetCookie?.() || [];
  const values = Array.isArray(raw) ? raw : [];
  return values.map((item) => String(item).split(';')[0]).filter(Boolean).join('; ');
}

function mergeCookies(first: string, second: string): string {
  const map = new Map<string, string>();
  for (const cookie of `${first}; ${second}`.split(';')) {
    const trimmed = cookie.trim();
    const index = trimmed.indexOf('=');
    if (index > 0) {
      map.set(trimmed.slice(0, index), trimmed.slice(index + 1));
    }
  }
  return [...map.entries()].map(([key, value]) => `${key}=${value}`).join('; ');
}

function decodeHtml(value: string): string {
  return String(value || '')
    .replace(/&amp;/g, '&')
    .replace(/&quot;/g, '"')
    .replace(/&#039;/g, "'")
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&nbsp;/g, ' ');
}

function defaultExtractInputValue(html: string, name: string): string {
  const match = html.match(new RegExp(`<input[^>]*name=["']${name}["'][^>]*value=["']([^"']*)["']`, 'i'));
  return decodeHtml(match?.[1] || '');
}

export async function createAuthenticatedLegacySession(
  baseUrl: string,
  email: string,
  password: string,
  options: LegacySessionOptions = { extractInputValue: defaultExtractInputValue },
): Promise<LegacySession> {
  const normalizedBase = baseUrl.replace(/\/+$/, '');
  let cookie = '';

  const request = async (urlPath: string, init: RequestInit = {}): Promise<Response> => {
    const url = urlPath.startsWith('http')
      ? urlPath
      : `${normalizedBase}${urlPath.startsWith('/') ? '' : '/'}${urlPath}`;
    const headers = new Headers(init.headers);
    if (cookie) {
      headers.set('Cookie', cookie);
    }
    const response = await fetch(url, {
      ...init,
      headers,
      redirect: init.redirect ?? 'follow',
    });
    cookie = mergeCookies(cookie, readSetCookie(response));
    return response;
  };

  const get = async (path: string): Promise<string> => {
    const response = await request(path, { method: 'GET' });
    const text = await response.text();
    if (!response.ok) {
      throw new Error(`Legacy request failed ${response.status} ${path}`);
    }
    return text;
  };

  const loginPage = await get('/login');
  const token = options.extractInputValue(loginPage, '_token');
  if (!token) {
    throw new Error('Não foi possível ler o token de login do legado.');
  }

  const loginResponse = await request('/login', {
    method: 'POST',
    redirect: 'manual',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      _token: token,
      email,
      password,
    }),
  });
  if (loginResponse.status >= 400) {
    throw new Error(`Falha no login legado (${loginResponse.status}).`);
  }

  const painel = await get('/painel');
  if (!/logout|Sair|painel/i.test(painel)) {
    throw new Error('Login no legado não confirmou sessão autenticada.');
  }

  return {
    get,
    fetch: (url: string, init?: RequestInit) => request(url, init),
  };
}
