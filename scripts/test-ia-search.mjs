#!/usr/bin/env node

/**
 * Smoke test da busca IA por HTTP.
 *
 * Uso:
 * 1) Backend rodando localmente.
 * 2) Defina IA_TEST_TOKEN ou IA_TEST_EMAIL + IA_TEST_PASSWORD.
 * 3) Rode: npm run test:ia
 */

const API_URL = process.env.API_URL || 'http://localhost:4000';
const IA_SCOPE = process.env.IA_TEST_SCOPE || 'all';
const RAW_QUERIES = process.env.IA_TEST_QUERIES || '';
const DEFAULT_QUERIES = ['demandas pendentes', 'dessas, quais estão com prioridade?'];

function toQueries(raw) {
  if (!raw.trim()) return DEFAULT_QUERIES;
  const split = raw.includes('||') ? raw.split('||') : raw.split('\n');
  return split.map((q) => q.trim()).filter(Boolean);
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function extractProtocols(text) {
  return [...new Set((String(text || '').match(/\b[A-Z]{2,}-\d{4}-\d{3,6}\b/g) || []).map((p) => p.toUpperCase()))];
}

async function loginAndGetToken() {
  if (process.env.IA_TEST_TOKEN?.trim()) return process.env.IA_TEST_TOKEN.trim();
  const email = process.env.IA_TEST_EMAIL?.trim();
  const password = process.env.IA_TEST_PASSWORD?.trim();
  if (!email || !password) {
    throw new Error(
      'Defina IA_TEST_TOKEN ou IA_TEST_EMAIL + IA_TEST_PASSWORD para rodar o teste.',
    );
  }

  const res = await fetch(`${API_URL}/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password }),
  });
  if (!res.ok) {
    const text = await res.text();
    throw new Error(`Falha no login (${res.status}): ${text}`);
  }
  const json = await res.json();
  const token = json?.accessToken;
  assert(typeof token === 'string' && token.length > 20, 'Token inválido no login.');
  return token;
}

async function callIa(token, query, context) {
  const res = await fetch(`${API_URL}/demandas/buscar-ia`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${token}`,
    },
    body: JSON.stringify({
      query,
      scope: IA_SCOPE,
      context: context || undefined,
    }),
  });
  const payload = await res.json().catch(() => ({}));
  if (!res.ok) {
    throw new Error(`Erro ${res.status} em /demandas/buscar-ia: ${JSON.stringify(payload)}`);
  }
  return payload;
}

function validateResponseShape(resp) {
  assert(resp && typeof resp === 'object', 'Resposta vazia da IA.');
  assert(resp.filters && typeof resp.filters === 'object', 'Resposta sem objeto "filters".');
  assert(typeof resp.message === 'string' && resp.message.trim().length > 0, 'Resposta sem mensagem natural.');
  assert(resp.preview && typeof resp.preview.total === 'number', 'Resposta sem "preview.total".');
  if (resp.evidence) {
    assert(Array.isArray(resp.evidence.fieldCounts), '"evidence.fieldCounts" inválido.');
    assert(Array.isArray(resp.evidence.moduleCounts), '"evidence.moduleCounts" inválido.');
    assert(Array.isArray(resp.evidence.topMatches), '"evidence.topMatches" inválido.');
    assert(Array.isArray(resp.evidence.globalMatches), '"evidence.globalMatches" inválido.');
  }
}

function validateMessageConsistency(resp) {
  const normalized = String(resp.message || '').toLowerCase();
  const matchDemandas = normalized.match(/(\d+)\s+demandas?/);
  if (matchDemandas?.[1]) {
    const cited = Number(matchDemandas[1]);
    assert(cited === resp.preview.total, `Mensagem citou ${cited} demandas, mas preview.total=${resp.preview.total}.`);
  }

  const allowed = new Set([
    ...(resp.preview?.protocolos || []).map((p) => String(p || '').toUpperCase()),
    ...((resp.evidence?.topMatches || []).map((m) => String(m?.protocolo || '').toUpperCase())),
  ]);
  const cited = extractProtocols(resp.message);
  const invalid = cited.filter((p) => !allowed.has(p));
  assert(invalid.length === 0, `Protocolos na mensagem não encontrados no preview/evidence: ${invalid.join(', ')}.`);
}

async function main() {
  const queries = toQueries(RAW_QUERIES);
  const token = await loginAndGetToken();
  let context = null;

  console.log(`\n[IA TEST] API: ${API_URL}`);
  console.log(`[IA TEST] Escopo: ${IA_SCOPE}`);
  console.log(`[IA TEST] Consultas: ${queries.length}\n`);

  for (const [index, query] of queries.entries()) {
    const resp = await callIa(token, query, context);
    validateResponseShape(resp);
    validateMessageConsistency(resp);

    const total = resp.preview?.total ?? 0;
    const protocolos = Array.isArray(resp.preview?.protocolos) ? resp.preview.protocolos.join(', ') : '';
    console.log(`[${index + 1}/${queries.length}] "${query}" -> total=${total}${protocolos ? ` | protocolos=${protocolos}` : ''}`);

    context = {
      previousQuery: query,
      previousScope: IA_SCOPE,
      previousSearchTerm: resp?.evidence?.searchTerm || resp?.filters?.pesquisaGeral || '',
      previousFilters: resp?.filters || {},
    };
  }

  console.log('\n[IA TEST] OK: validação concluída sem inconsistências.\n');
}

main().catch((err) => {
  console.error(`\n[IA TEST] FALHA: ${err instanceof Error ? err.message : String(err)}\n`);
  process.exit(1);
});
