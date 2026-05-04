#!/usr/bin/env node

import fs from 'node:fs';
import fsp from 'node:fs/promises';
import path from 'node:path';

const LEGACY_BASE_URL = process.env.LEGACY_BASE_URL || 'http://luxusweb.com.br';
const CACHE_DIR = path.join(process.cwd(), 'scripts', '.legacy-cache');
const MAP_FILE = path.join(CACHE_DIR, 'map.json');
const SNAPSHOT_FILE = path.join(CACHE_DIR, 'snapshot.json');
const LEGACY_MIGRATION_EMAIL = 'legacy-migration@luxus.local';
const LEGACY_PLACEHOLDER_DOMAIN = 'luxus.local';
const DEFAULT_IMPORTED_PASSWORD_HASH =
  '$2a$06$T/2vNgiBvzUe1c0GvDZFyetzLYmz37qm73Yh2GBJo0r4hypfp/6BG';

const DEFAULT_OPTIONS = {
  apply: false,
  phases: new Set(['users', 'clients', 'templates', 'demandas']),
  limitUsers: null,
  limitClients: null,
  limitTemplates: null,
  limitDemandPages: null,
  limitDemands: null,
  demandIds: null,
  demandBatchSize: 100,
  concurrency: 4,
  skipAnexos: false,
  linkAnexos: false,
  forceAnexos: false,
  writeSnapshot: true,
};

const HTML_ENTITIES = {
  amp: '&',
  lt: '<',
  gt: '>',
  quot: '"',
  apos: "'",
  nbsp: ' ',
  ndash: '-',
  mdash: '-',
  hellip: '...',
  copy: '(c)',
  reg: '(r)',
  trade: '(tm)',
  ordm: 'o',
  ordf: 'a',
  deg: 'o',
  sect: '§',
  para: '¶',
  middot: '·',
  bull: '•',
  lsquo: "'",
  rsquo: "'",
  ldquo: '"',
  rdquo: '"',
  aacute: 'á',
  Aacute: 'Á',
  agrave: 'à',
  Agrave: 'À',
  acirc: 'â',
  Acirc: 'Â',
  atilde: 'ã',
  Atilde: 'Ã',
  auml: 'ä',
  Auml: 'Ä',
  aring: 'å',
  Aring: 'Å',
  eacute: 'é',
  Eacute: 'É',
  egrave: 'è',
  Egrave: 'È',
  ecirc: 'ê',
  Ecirc: 'Ê',
  euml: 'ë',
  Euml: 'Ë',
  iacute: 'í',
  Iacute: 'Í',
  igrave: 'ì',
  Igrave: 'Ì',
  icirc: 'î',
  Icirc: 'Î',
  iuml: 'ï',
  Iuml: 'Ï',
  oacute: 'ó',
  Oacute: 'Ó',
  ograve: 'ò',
  Ograve: 'Ò',
  ocirc: 'ô',
  Ocirc: 'Ô',
  otilde: 'õ',
  Otilde: 'Õ',
  ouml: 'ö',
  Ouml: 'Ö',
  uacute: 'ú',
  Uacute: 'Ú',
  ugrave: 'ù',
  Ugrave: 'Ù',
  ucirc: 'û',
  Ucirc: 'Û',
  uuml: 'ü',
  Uuml: 'Ü',
  ccedil: 'ç',
  Ccedil: 'Ç',
  ntilde: 'ñ',
  Ntilde: 'Ñ',
  acute: '´',
  cedil: '¸',
  sup1: '1',
  sup2: '2',
  sup3: '3',
  frac14: '1/4',
  frac12: '1/2',
  frac34: '3/4',
};

function loadDotEnv(filePath) {
  if (!fs.existsSync(filePath)) return;
  const lines = fs.readFileSync(filePath, 'utf8').split(/\r?\n/);
  for (const line of lines) {
    if (!line || line.trim().startsWith('#')) continue;
    const separator = line.indexOf('=');
    if (separator <= 0) continue;
    const key = line.slice(0, separator).trim();
    const rawValue = line.slice(separator + 1).trim();
    const value = rawValue.replace(/^"(.*)"$/, '$1');
    if (!(key in process.env)) process.env[key] = value;
  }
}

function parseArgs(argv) {
  const options = {
    ...DEFAULT_OPTIONS,
    phases: new Set(DEFAULT_OPTIONS.phases),
  };

  for (const arg of argv) {
    if (arg === '--apply') {
      options.apply = true;
      continue;
    }
    if (arg === '--dry-run') {
      options.apply = false;
      continue;
    }
    if (arg.startsWith('--phases=')) {
      const raw = arg.slice('--phases='.length).trim();
      if (!raw || raw === 'all') {
        options.phases = new Set(DEFAULT_OPTIONS.phases);
      } else {
        options.phases = new Set(raw.split(',').map((item) => item.trim()).filter(Boolean));
      }
      continue;
    }
    if (arg.startsWith('--limit-users=')) {
      options.limitUsers = toNullableInt(arg.slice('--limit-users='.length));
      continue;
    }
    if (arg.startsWith('--limit-clients=')) {
      options.limitClients = toNullableInt(arg.slice('--limit-clients='.length));
      continue;
    }
    if (arg.startsWith('--limit-templates=')) {
      options.limitTemplates = toNullableInt(arg.slice('--limit-templates='.length));
      continue;
    }
    if (arg.startsWith('--limit-demand-pages=')) {
      options.limitDemandPages = toNullableInt(arg.slice('--limit-demand-pages='.length));
      continue;
    }
    if (arg.startsWith('--limit-demands=')) {
      options.limitDemands = toNullableInt(arg.slice('--limit-demands='.length));
      continue;
    }
    if (arg.startsWith('--demand-ids=')) {
      options.demandIds = arg.slice('--demand-ids='.length)
        .split(',')
        .map((item) => item.trim())
        .filter(Boolean);
      continue;
    }
    if (arg.startsWith('--demand-batch-size=')) {
      options.demandBatchSize = Math.max(1, Number.parseInt(arg.slice('--demand-batch-size='.length), 10) || 1);
      continue;
    }
    if (arg.startsWith('--concurrency=')) {
      options.concurrency = Math.max(1, Number.parseInt(arg.slice('--concurrency='.length), 10) || 1);
      continue;
    }
    if (arg === '--skip-anexos') {
      options.skipAnexos = true;
      continue;
    }
    if (arg === '--link-anexos') {
      options.linkAnexos = true;
      continue;
    }
    if (arg === '--force-anexos') {
      options.forceAnexos = true;
      continue;
    }
    if (arg === '--no-snapshot') {
      options.writeSnapshot = false;
    }
  }

  return options;
}

function toNullableInt(value) {
  if (!value || value === 'all' || value === 'null') return null;
  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}

function decodeHtml(text) {
  if (!text) return '';
  return String(text)
    .replace(/&#(\d+);/g, (_, digits) => safeFromCodePoint(Number.parseInt(digits, 10)))
    .replace(/&#x([0-9a-f]+);/gi, (_, hex) => safeFromCodePoint(Number.parseInt(hex, 16)))
    .replace(/&([A-Za-z][A-Za-z0-9]+);/g, (match, name) => HTML_ENTITIES[name] ?? match);
}

function safeFromCodePoint(codePoint) {
  if (!Number.isFinite(codePoint)) return '';
  try {
    return String.fromCodePoint(codePoint);
  } catch {
    return '';
  }
}

function stripTags(html) {
  return decodeHtml(
    String(html || '')
      .replace(/<!--[\s\S]*?-->/g, ' ')
      .replace(/<br\s*\/?>/gi, '\n')
      .replace(/<\/p>/gi, '\n')
      .replace(/<[^>]+>/g, ' ')
      .replace(/\r/g, ''),
  )
    .replace(/\u00a0/g, ' ')
    .replace(/[ \t]+\n/g, '\n')
    .replace(/\n[ \t]+/g, '\n')
    .replace(/[ \t]{2,}/g, ' ')
    .replace(/\n{3,}/g, '\n\n')
    .trim();
}

function normalizeWhitespace(text) {
  return decodeHtml(String(text || ''))
    .replace(/\u00a0/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}

function normalizeText(text) {
  return normalizeWhitespace(text)
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase();
}

function slugify(text) {
  return normalizeText(text)
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
}

function normalizeEmail(email) {
  const normalized = normalizeWhitespace(email).toLowerCase();
  return normalized || null;
}

function normalizeDocumento(documento) {
  const digits = String(documento || '').replace(/\D/g, '');
  return digits || null;
}

function parseBrDate(value) {
  if (!value) return null;
  const match = String(value).match(/(\d{2})\/(\d{2})\/(\d{4})/);
  if (!match) return null;
  const [, day, month, year] = match;
  const numericYear = Number.parseInt(year, 10);
  if (numericYear < 1900) return null;
  return `${year}-${month}-${day}`;
}

function parseBrDateTime(value) {
  if (!value) return null;
  const match = String(value).match(/(\d{2})\/(\d{2})\/(\d{4})\s+(\d{2}):(\d{2})(?::(\d{2}))?/);
  if (!match) return null;
  const [, day, month, year, hour, minute, second = '00'] = match;
  const numericYear = Number.parseInt(year, 10);
  if (numericYear < 1900) return null;
  return `${year}-${month}-${day}T${hour}:${minute}:${second}`;
}

function isTruthyLegacy(value) {
  const normalized = normalizeText(value);
  return normalized === 'sim' || normalized === '1' || normalized === 'true';
}

function mapLegacyStatus(value) {
  const normalized = normalizeText(value);
  if (normalized === 'em aberto') return 'em_aberto';
  if (normalized === 'em andamento') return 'em_andamento';
  if (normalized === 'concluido' || normalized === 'concluído') return 'concluido';
  if (normalized === 'standby' || normalized === 'stand by') return 'standby';
  if (normalized === 'cancelado') return 'cancelado';
  return 'em_aberto';
}

function mapLegacyRecorrenciaTipo(value) {
  const normalized = normalizeText(value);
  if (!normalized) return null;
  if (normalized === 'diaria' || normalized === 'diária') return 'diaria';
  if (normalized === 'semanal') return 'semanal';
  if (normalized === 'quinzenal') return 'quinzenal';
  if (normalized === 'mensal') return 'mensal';
  return null;
}

function extractFirst(pattern, text, flags = 'i') {
  const match = new RegExp(pattern, flags).exec(text);
  return match?.[1] ?? null;
}

function extractInputValue(html, name) {
  return decodeHtml(
    extractFirst(`<input[^>]*name=["']${escapeRegExp(name)}["'][^>]*value=["']([^"']*)["']`, html, 'is') ?? '',
  );
}

function extractSelectValue(html, name) {
  const selectHtml = extractFirst(`<select[^>]*name=["']${escapeRegExp(name)}["'][^>]*>([\\s\\S]*?)</select>`, html, 'is');
  if (!selectHtml) return null;
  const selected = extractFirst('<option[^>]*value=["\']([^"\']*)["\'][^>]*selected[^>]*>', selectHtml, 'is');
  if (selected !== null) return decodeHtml(selected);
  return decodeHtml(extractFirst('<option[^>]*value=["\']([^"\']*)["\'][^>]*>', selectHtml, 'is') ?? '');
}

function extractSelectedOptionText(html, name) {
  const selectHtml = extractFirst(`<select[^>]*name=["']${escapeRegExp(name)}["'][^>]*>([\\s\\S]*?)</select>`, html, 'is');
  if (!selectHtml) return null;
  const selected = extractFirst('<option[^>]*selected[^>]*>([\\s\\S]*?)</option>', selectHtml, 'is');
  if (selected !== null) return normalizeWhitespace(stripTags(selected));
  return normalizeWhitespace(stripTags(extractFirst('<option[^>]*>([\\s\\S]*?)</option>', selectHtml, 'is') ?? '')) || null;
}

function firstNonEmpty(...values) {
  for (const value of values) {
    const normalized = normalizeWhitespace(value);
    if (normalized) return normalized;
  }
  return null;
}

function extractTextareaAfterLabel(html, label) {
  const section = extractFirst(
    `<span[^>]*class=["']input-group-addon["'][^>]*>\\s*${escapeRegExp(label)}\\s*</span>[\\s\\S]*?<textarea[^>]*>([\\s\\S]*?)</textarea>`,
    html,
    'is',
  );
  return section ? stripTags(section) : '';
}

function extractCheckedCheckboxLabels(html, inputName) {
  const regex = new RegExp(
    `<input[^>]*name=["']${escapeRegExp(inputName)}["'][^>]*checked[^>]*>\\s*([^<]+?)\\s*<br`,
    'gis',
  );
  return [...html.matchAll(regex)].map((match) => normalizeWhitespace(match[1]));
}

function extractSection(html, startMarker, endMarker) {
  const start = html.indexOf(startMarker);
  if (start < 0) return '';
  const fromStart = html.slice(start);
  if (!endMarker) return fromStart;
  const end = fromStart.indexOf(endMarker);
  return end < 0 ? fromStart : fromStart.slice(0, end);
}

function extractTableRows(sectionHtml) {
  return [...String(sectionHtml || '').matchAll(/<tr>([\s\S]*?)<\/tr>/gi)]
    .map((match) => match[1])
    .filter((row) => row.includes('<td'));
}

function extractCells(rowHtml) {
  const sanitized = String(rowHtml || '').replace(/<!--[\s\S]*?-->/g, '');
  return [...sanitized.matchAll(/<t[dh][^>]*>([\s\S]*?)<\/t[dh]>/gi)].map((match) => match[1].trim());
}

function parseLegacyClientCell(cellHtml) {
  const text = stripTags(cellHtml);
  const cnpj = normalizeDocumento(extractFirst('cnpj:\\s*([0-9./-]+)', text, 'i'));
  const cpf = normalizeDocumento(extractFirst('cpf:\\s*([0-9./-]+)', text, 'i'));
  const tipoPessoa = cnpj ? 'pj' : cpf ? 'pf' : null;
  const documento = cnpj || cpf;
  const name = normalizeWhitespace(
    text
      .replace(/cnpj:\s*[0-9./-]+/i, '')
      .replace(/cpf:\s*[0-9./-]+/i, ''),
  );
  return { name, tipoPessoa, documento };
}

function collectLegacyClientAdditionalContacts(html) {
  const contacts = [];
  for (const suffix of ['3', '4', '5', '6']) {
    const contato = normalizeWhitespace(extractInputValue(html, `contato${suffix}`));
    const departamento = normalizeWhitespace(extractInputValue(html, `dpto${suffix}`));
    const email = normalizeEmail(extractInputValue(html, `email${suffix}`));
    if (!contato && !departamento && !email) continue;
    contacts.push({ contato, departamento, email });
  }
  return contacts;
}

function buildLegacyClientObservacoes(detail) {
  const notes = [];

  if (detail.loginEmail && detail.loginEmail !== detail.email) {
    notes.push(`Login legado: ${detail.loginEmail}`);
  }

  if (detail.administradorNome || detail.administradorCpf || detail.administradorRg) {
    const adminParts = [
      detail.administradorNome && `Administrador: ${detail.administradorNome}`,
      detail.administradorCpf && `CPF: ${detail.administradorCpf}`,
      detail.administradorRg && `RG: ${detail.administradorRg}`,
    ].filter(Boolean);
    if (adminParts.length) notes.push(adminParts.join(' | '));
  }

  if (detail.emailGestor) {
    notes.push(`E-mail gestor: ${detail.emailGestor}`);
  }

  for (const item of detail.additionalContacts || []) {
    const parts = [
      item.contato && `Contato: ${item.contato}`,
      item.departamento && `Departamento: ${item.departamento}`,
      item.email && `E-mail: ${item.email}`,
    ].filter(Boolean);
    if (parts.length) notes.push(parts.join(' | '));
  }

  return notes.join('\n').trim() || null;
}

function parseLegacyClientDetail(html, baseClient = {}) {
  const cnpj = normalizeDocumento(extractInputValue(html, 'cnpj'));
  const cpf = normalizeDocumento(extractInputValue(html, 'cpf'));
  const tipoPessoa = baseClient.tipoPessoa || (cnpj ? 'pj' : cpf ? 'pf' : null);
  const documento = baseClient.documento || cnpj || cpf || null;
  const emailContato = normalizeEmail(firstNonEmpty(
    extractInputValue(html, 'email2_m'),
    extractInputValue(html, 'email2'),
    extractInputValue(html, 'email_gestor_cm'),
    extractInputValue(html, 'email_gestor_cf'),
    extractInputValue(html, 'email3'),
    extractInputValue(html, 'email4'),
    extractInputValue(html, 'email5'),
    extractInputValue(html, 'email6'),
  ));
  const additionalContacts = collectLegacyClientAdditionalContacts(html);
  const detail = {
    legacyId: baseClient.legacyId || normalizeWhitespace(extractInputValue(html, 'id_cliente')) || null,
    name: normalizeWhitespace(extractInputValue(html, 'nome')) || baseClient.name || '',
    tipoPessoa,
    documento,
    active: baseClient.active ?? true,
    status: baseClient.status || null,
    nomeFantasia: normalizeWhitespace(extractInputValue(html, 'nome_fan')) || null,
    ramoAtividade: normalizeWhitespace(extractInputValue(html, 'ramo_atividade')) || null,
    inscricaoEstadual: normalizeWhitespace(extractInputValue(html, 'ie')) || null,
    cep: normalizeDocumento(extractInputValue(html, 'cep')),
    endereco: normalizeWhitespace(extractInputValue(html, 'endereco')) || null,
    numero: normalizeWhitespace(extractInputValue(html, 'end_nro')) || null,
    complemento: normalizeWhitespace(extractInputValue(html, 'end_comp')) || null,
    bairro: normalizeWhitespace(extractInputValue(html, 'bairro')) || null,
    cidade: normalizeWhitespace(extractSelectedOptionText(html, 'cod_mun')) || null,
    uf: normalizeWhitespace(extractSelectedOptionText(html, 'cod_uf')) || null,
    telefone: firstNonEmpty(extractInputValue(html, 'fone'), extractInputValue(html, 'telefone2_m')),
    celular: firstNonEmpty(extractInputValue(html, 'celular_adm_m')),
    contato: firstNonEmpty(extractInputValue(html, 'administrador_m'), extractInputValue(html, 'contato3'), extractInputValue(html, 'contato4'), extractInputValue(html, 'contato5'), extractInputValue(html, 'contato6')),
    email: emailContato,
    loginEmail: normalizeEmail(extractInputValue(html, 'email2')),
    emailGestor: normalizeEmail(firstNonEmpty(extractInputValue(html, 'email_gestor_cm'), extractInputValue(html, 'email_gestor_cf'))),
    administradorNome: normalizeWhitespace(extractInputValue(html, 'administrador_m')) || null,
    administradorCpf: normalizeDocumento(extractInputValue(html, 'cpf_adm_m')),
    administradorRg: normalizeWhitespace(extractInputValue(html, 'rg_adm_m')) || null,
    additionalContacts,
  };

  return {
    legacyId: detail.legacyId,
    name: detail.name,
    tipoPessoa: detail.tipoPessoa,
    documento: detail.documento,
    active: detail.active,
    status: detail.status,
    nomeFantasia: detail.nomeFantasia,
    ramoAtividade: detail.ramoAtividade,
    inscricaoEstadual: detail.inscricaoEstadual,
    cep: detail.cep,
    endereco: detail.endereco,
    numero: detail.numero,
    complemento: detail.complemento,
    bairro: detail.bairro,
    cidade: detail.cidade,
    uf: detail.uf,
    telefone: detail.telefone,
    celular: detail.celular,
    contato: detail.contato,
    email: detail.email,
    observacoesCadastro: buildLegacyClientObservacoes(detail),
  };
}

function parseLegacyHistory(text) {
  const lines = String(text || '')
    .split(/\r?\n/)
    .map((line) => normalizeWhitespace(line))
    .filter(Boolean);
  return lines.map((line) => {
    const dateTime = parseBrDateTime(line);
    const dateOnly = !dateTime ? parseBrDate(line) : null;
    const createdAt = dateTime || (dateOnly ? `${dateOnly}T00:00:00` : null);
    const withoutPrefix = dateTime ? line.replace(/^\d{2}\/\d{2}\/\d{4}\s+\d{2}:\d{2}(?::\d{2})?\s*-\s*/, '') : line;
    const userName = extractFirst('por:\\s*\\(\\d+\\)\\s*-\\s*([^\\-]+?)(?:\\s*-|$)', withoutPrefix, 'i');
    return {
      raw: line,
      createdAt,
      userName: normalizeWhitespace(userName || ''),
      description: withoutPrefix,
    };
  });
}

function newestDate(...values) {
  return values.filter(Boolean).sort().at(-1) ?? null;
}

function parsePaginationMax(html) {
  const pages = [...String(html || '').matchAll(/page=(\d+)/gi)].map((match) => Number.parseInt(match[1], 10));
  return pages.length ? Math.max(...pages) : 1;
}

function limitArray(items, limit) {
  if (!limit || limit < 1) return items;
  return items.slice(0, limit);
}

function chunkArray(items, size) {
  const chunkSize = Math.max(1, size || items.length || 1);
  const chunks = [];
  for (let index = 0; index < items.length; index += chunkSize) {
    chunks.push(items.slice(index, index + chunkSize));
  }
  return chunks;
}

function encodePathSegments(...segments) {
  return segments
    .flatMap((segment) => String(segment || '').split('/'))
    .filter(Boolean)
    .map((segment) => encodeURIComponent(segment))
    .join('/');
}

function sanitizeFilename(value) {
  return String(value || '')
    .replace(/[<>:"/\\|?*\u0000-\u001f]/g, '_')
    .replace(/\s+/g, ' ')
    .trim();
}

function filenameFromUrl(url) {
  try {
    return decodeURIComponent(path.posix.basename(new URL(url).pathname));
  } catch {
    return 'arquivo';
  }
}

function guessMimeType(filename) {
  const extension = path.extname(String(filename || '')).toLowerCase();
  switch (extension) {
    case '.pdf':
      return 'application/pdf';
    case '.doc':
      return 'application/msword';
    case '.docx':
      return 'application/vnd.openxmlformats-officedocument.wordprocessingml.document';
    case '.xls':
      return 'application/vnd.ms-excel';
    case '.xlsx':
      return 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';
    case '.csv':
      return 'text/csv';
    case '.txt':
      return 'text/plain';
    case '.jpg':
    case '.jpeg':
      return 'image/jpeg';
    case '.png':
      return 'image/png';
    case '.gif':
      return 'image/gif';
    case '.zip':
      return 'application/zip';
    case '.rar':
      return 'application/vnd.rar';
    default:
      return 'application/octet-stream';
  }
}

function escapeRegExp(value) {
  return String(value).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function pickFirstValidDate(...dates) {
  for (const date of dates) {
    if (date) return date;
  }
  return null;
}

function deterministicEmail(prefix, legacyId, fallbackName) {
  const local = [prefix, legacyId || slugify(fallbackName || 'user') || 'item']
    .filter(Boolean)
    .join('-')
    .replace(/[^a-z0-9-]/g, '')
    .slice(0, 54);
  return `${local}@${LEGACY_PLACEHOLDER_DOMAIN}`;
}

function getImportedPasswordHash() {
  const value = process.env.LEGACY_IMPORTED_PASSWORD_HASH || DEFAULT_IMPORTED_PASSWORD_HASH;
  return String(value || '').trim();
}

async function ensureDir(dirPath) {
  await fsp.mkdir(dirPath, { recursive: true });
}

async function loadMap() {
  await ensureDir(CACHE_DIR);
  if (!fs.existsSync(MAP_FILE)) {
    return { users: {}, clients: {}, templates: {}, demandas: {}, anexos: {} };
  }
  const map = JSON.parse(await fsp.readFile(MAP_FILE, 'utf8'));
  return {
    users: map.users || {},
    clients: map.clients || {},
    templates: map.templates || {},
    demandas: map.demandas || {},
    anexos: map.anexos || {},
  };
}

async function saveMap(map) {
  await ensureDir(CACHE_DIR);
  await fsp.writeFile(MAP_FILE, `${JSON.stringify(map, null, 2)}\n`, 'utf8');
}

async function saveSnapshot(snapshot) {
  await ensureDir(CACHE_DIR);
  await fsp.writeFile(SNAPSHOT_FILE, `${JSON.stringify(snapshot, null, 2)}\n`, 'utf8');
}

async function mapConcurrent(items, concurrency, worker) {
  const results = new Array(items.length);
  let index = 0;
  const actualConcurrency = Math.max(1, Math.min(concurrency, items.length || 1));

  await Promise.all(
    Array.from({ length: actualConcurrency }, async () => {
      while (index < items.length) {
        const currentIndex = index++;
        results[currentIndex] = await worker(items[currentIndex], currentIndex);
      }
    }),
  );

  return results;
}

class LegacySession {
  constructor(baseUrl) {
    this.baseUrl = baseUrl.replace(/\/+$/, '');
    this.cookies = new Map();
  }

  updateCookies(response) {
    const setCookies = typeof response.headers.getSetCookie === 'function'
      ? response.headers.getSetCookie()
      : [];
    for (const value of setCookies) {
      const [cookiePair] = value.split(';');
      const separator = cookiePair.indexOf('=');
      if (separator <= 0) continue;
      const key = cookiePair.slice(0, separator).trim();
      const cookieValue = cookiePair.slice(separator + 1).trim();
      this.cookies.set(key, cookieValue);
    }
  }

  buildHeaders(extra = {}) {
    const headers = { ...extra };
    if (this.cookies.size > 0) {
      headers.Cookie = [...this.cookies.entries()].map(([key, value]) => `${key}=${value}`).join('; ');
    }
    return headers;
  }

  async request(urlPath, init = {}) {
    const url = urlPath.startsWith('http') ? urlPath : `${this.baseUrl}${urlPath.startsWith('/') ? '' : '/'}${urlPath}`;
    const response = await fetch(url, {
      redirect: init.redirect || 'follow',
      ...init,
      headers: this.buildHeaders(init.headers),
    });
    this.updateCookies(response);
    const text = await response.text();
    if (!response.ok) {
      throw new Error(`Legacy request failed ${response.status} ${url}`);
    }
    return text;
  }

  async get(urlPath) {
    return this.request(urlPath, { method: 'GET' });
  }

  async postForm(urlPath, form) {
    const body = new URLSearchParams();
    for (const [key, value] of Object.entries(form)) body.append(key, value ?? '');
    return this.request(urlPath, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body,
    });
  }

  async login(email, password) {
    const loginPage = await this.get('/login');
    const token = extractInputValue(loginPage, '_token');
    if (!token) throw new Error('Não consegui ler o CSRF token do login legado.');
    const body = new URLSearchParams({
      _token: token,
      email,
      password,
    });
    const loginResponse = await fetch(`${this.baseUrl}/login`, {
      method: 'POST',
      redirect: 'manual',
      headers: this.buildHeaders({
        'Content-Type': 'application/x-www-form-urlencoded',
      }),
      body,
    });
    this.updateCookies(loginResponse);
    const painel = await this.get('/painel');
    if (!/logout|Sair|painel/i.test(painel)) {
      throw new Error('Login no legado não confirmou sessão autenticada.');
    }
  }
}

class SupabaseRestClient {
  constructor(baseUrl, serviceRoleKey) {
    this.projectUrl = baseUrl.replace(/\/+$/, '');
    this.baseUrl = `${this.projectUrl}/rest/v1`;
    this.storageUrl = `${this.projectUrl}/storage/v1`;
    this.serviceRoleKey = serviceRoleKey;
  }

  headers(extra = {}) {
    return {
      apikey: this.serviceRoleKey,
      Authorization: `Bearer ${this.serviceRoleKey}`,
      ...extra,
    };
  }

  async request(pathname, init = {}) {
    const maxAttempts = 5;
    let lastError = null;

    for (let attempt = 1; attempt <= maxAttempts; attempt += 1) {
      try {
        const response = await fetch(`${this.baseUrl}/${pathname}`, {
          ...init,
          headers: this.headers(init.headers),
        });
        const raw = await response.text();
        const data = raw ? safeJsonParse(raw) : null;
        if (!response.ok) {
          if (attempt < maxAttempts && [429, 500, 502, 503, 504].includes(response.status)) {
            await sleep(400 * attempt);
            continue;
          }
          throw new Error(`Supabase request failed ${response.status} ${pathname}: ${raw}`);
        }
        return { response, data, raw };
      } catch (error) {
        lastError = error;
        if (attempt >= maxAttempts) break;
        await sleep(400 * attempt);
      }
    }
    throw lastError;
  }

  async select(pathname) {
    const { data } = await this.request(pathname, { method: 'GET' });
    return data;
  }

  async insert(table, payload, { select = '*', prefer = 'return=representation' } = {}) {
    const query = select ? `${table}?select=${select}` : table;
    const { data } = await this.request(query, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Prefer: prefer,
      },
      body: JSON.stringify(payload),
    });
    return data;
  }

  async patch(table, filterQuery, payload, { select = '*' } = {}) {
    const query = `${table}?${filterQuery}${select ? `${filterQuery ? '&' : ''}select=${select}` : ''}`;
    const { data } = await this.request(query, {
      method: 'PATCH',
      headers: {
        'Content-Type': 'application/json',
        Prefer: 'return=representation',
      },
      body: JSON.stringify(payload),
    });
    return data;
  }

  async storageRequest(pathname, init = {}, responseType = 'json') {
    const maxAttempts = 5;
    let lastError = null;

    for (let attempt = 1; attempt <= maxAttempts; attempt += 1) {
      try {
        const response = await fetch(`${this.storageUrl}/${pathname.replace(/^\/+/, '')}`, {
          ...init,
          headers: this.headers(init.headers),
        });
        if (!response.ok) {
          const body = await response.text();
          if (attempt < maxAttempts && [429, 500, 502, 503, 504].includes(response.status)) {
            await sleep(400 * attempt);
            continue;
          }
          throw new Error(`Supabase storage request failed ${response.status} ${pathname}: ${body}`);
        }

        if (responseType === 'none') return null;
        if (responseType === 'buffer') return Buffer.from(await response.arrayBuffer());
        if (responseType === 'text') return await response.text();
        const raw = await response.text();
        return raw ? safeJsonParse(raw) : null;
      } catch (error) {
        lastError = error;
        if (attempt >= maxAttempts) break;
        await sleep(400 * attempt);
      }
    }

    throw lastError;
  }

  async listBuckets() {
    return this.storageRequest('bucket', { method: 'GET' });
  }

  async createBucket(name, isPublic = false) {
    return this.storageRequest('bucket', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        id: name,
        name,
        public: !!isPublic,
      }),
    });
  }

  async uploadObject(bucketName, objectPath, buffer, contentType, { upsert = false } = {}) {
    await this.storageRequest(`object/${encodePathSegments(bucketName, objectPath)}`, {
      method: 'POST',
      headers: {
        'Content-Type': contentType || 'application/octet-stream',
        'x-upsert': upsert ? 'true' : 'false',
      },
      body: buffer,
    }, 'none');
  }
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function safeJsonParse(raw) {
  try {
    return JSON.parse(raw);
  } catch {
    return raw;
  }
}

function encodeEq(value) {
  return encodeURIComponent(String(value));
}

function buildBlankDemandSearchQuery(page = 1) {
  const params = new URLSearchParams({
    assunto: '',
    criador: '',
    tipo: '',
    status: '',
    resp: '',
    di_dem: '',
    df_dem: '',
    prazo_di_dem: '',
    prazo_df_dem: '',
    setor: '',
    cliente: '',
    condicao: '',
    protocolo: '',
    tarefa: '',
    obs: '',
    prioridade: '',
    resp_principal: '',
    page: String(page),
  });
  return `/painel/demandas/pesquisar?${params.toString()}`;
}

function parseLegacyUsersPage(html) {
  const rows = extractTableRows(extractSection(html, '<table class="table table-hover">', '</table>'));
  return rows
    .map((row) => {
      const cells = extractCells(row);
      const legacyId = normalizeWhitespace(stripTags(cells[0] || ''));
      const name = normalizeWhitespace(stripTags(cells[1] || ''));
      const email = normalizeEmail(stripTags(cells[2] || ''));
      const type = normalizeWhitespace(stripTags(cells[3] || ''));
      if (!legacyId || !name) return null;
      return { legacyId, name, email, type };
    })
    .filter(Boolean);
}

function parseLegacyClientsPage(html, tipoPessoa) {
  const rows = extractTableRows(extractSection(html, '<table class="table table-hover">', '</table>'));
  return rows
    .map((row) => {
      const cells = extractCells(row);
      const legacyId = normalizeWhitespace(stripTags(cells[0] || ''));
      const name = normalizeWhitespace(stripTags(cells[1] || ''));
      const rawDocumentCell = stripTags(cells[4] || '');
      const documentMatches = [...rawDocumentCell.matchAll(/\d[\d./-]{7,}/g)].map((match) => match[0]);
      const documento = normalizeDocumento(documentMatches.at(-1) || rawDocumentCell);
      const status = normalizeWhitespace(stripTags(cells[6] || ''));
      const detailPath = extractFirst('href=["\'](https?:\\/\\/[^"\']*\\/painel\\/clientes\\/associados\\/editar\\/[^"\']+|\\/painel\\/clientes\\/associados\\/editar\\/[^"\']+)["\']', row, 'i');
      if (!legacyId || !name) return null;
      return {
        legacyId,
        name,
        tipoPessoa,
        documento,
        active: normalizeText(status) !== 'cancelado' && normalizeText(status) !== 'desativado',
        status,
        detailPath: detailPath
          ? (() => {
            try {
              return new URL(detailPath, LEGACY_BASE_URL).pathname;
            } catch {
              return detailPath;
            }
          })()
          : null,
      };
    })
    .filter(Boolean);
}

function parseLegacyTemplateIds(html) {
  return [...html.matchAll(/painel\/demandas\/editar\/temp\/(\d+)/gi)]
    .map((match) => match[1])
    .filter((value, index, array) => array.indexOf(value) === index);
}

function parseLegacyDemandSearchPage(html) {
  const tableSection = extractSection(html, '<table class="table table-hover">', '<ul class="pagination">');
  const rows = extractTableRows(tableSection);
  return rows
    .map((row) => {
      const legacyId = extractFirst('painel/demandas/editar/(\\d+)', row, 'i');
      if (!legacyId) return null;
      const cells = extractCells(row);
      const protocoloCell = cells[2] || '';
      const dueCell = stripTags(cells[7] || '');
      return {
        legacyId,
        listCreatedAt: parseBrDate(stripTags(cells[1] || '')),
        protocolo: normalizeWhitespace(stripTags(protocoloCell)),
        assunto: normalizeWhitespace(stripTags(cells[4] || '')),
        status: normalizeWhitespace(stripTags(cells[8] || '')),
        prioridade: /prioridade\.png/i.test(protocoloCell),
        prazo: parseBrDate(dueCell),
        dataAbertura: parseBrDate(extractFirst('DA:\\s*(\\d{2}\\/\\d{2}\\/\\d{4})', dueCell, 'i')),
      };
    })
    .filter(Boolean);
}

function parseLegacyTemplateDetail(html, legacyId) {
  const name = extractInputValue(html, 'template');
  const assuntoTemplate = extractInputValue(html, 'assunto');
  const isRecorrenteLegacy = extractSelectValue(html, 'tipo');
  const recorrenciaTipoLegacy = extractSelectValue(html, 'tipo_recorrencia');
  const recorrenciaDataBase = extractInputValue(html, 'data_base_recorrencia');
  const recorrenciaPrazoReaberturaDias = extractInputValue(html, 'prazo_reabertura_recorrencia') || extractInputValue(html, 'prazo_recorrencia');
  const prioridade = extractSelectValue(html, 'prioridade');
  const historyText = extractTextareaAfterLabel(html, 'Histórico');
  const history = parseLegacyHistory(historyText);

  const clientsSection = extractSection(html, 'Adicionar Clientes', 'Adicionar Responsavel');
  const responsaveisSection = extractSection(html, 'Adicionar Responsavel', 'Adicionar Tarefas');
  const tarefasSection = extractSection(html, 'Adicionar Tarefas', 'Adicionar Observa');
  const observacoesSection = extractSection(html, 'Adicionar Observa', 'Prazo');

  const clientes = extractTableRows(clientsSection).map((row) => {
    const cells = extractCells(row);
    if (!cells.length) return null;
    const parsed = parseLegacyClientCell(cells[0]);
    if (!parsed.name) return null;
    return {
      ...parsed,
      createdByName: normalizeWhitespace(stripTags(cells[1] || '')),
      createdAt: parseBrDate(stripTags(cells[2] || '')),
    };
  }).filter(Boolean);

  const responsaveis = extractTableRows(responsaveisSection).map((row) => {
    const cells = extractCells(row);
    if (!cells.length) return null;
    return {
      name: normalizeWhitespace(stripTags(cells[0] || '')),
      isPrincipal: /fa-check-square/i.test(cells[0] || ''),
      createdByName: normalizeWhitespace(stripTags(cells[1] || '')),
      createdAt: parseBrDate(stripTags(cells[2] || '')),
    };
  }).filter((item) => item?.name);

  const subtarefas = extractTableRows(tarefasSection).map((row, index) => {
    const cells = extractCells(row);
    if (!cells.length) return null;
    return {
      titulo: stripTags(cells[0] || ''),
      responsavelName: normalizeWhitespace(stripTags(cells[1] || '')),
      createdAt: parseBrDate(stripTags(cells[2] || '')),
      ordem: index,
    };
  }).filter((item) => item?.titulo);

  const observacoes = extractTableRows(observacoesSection).map((row) => {
    const cells = extractCells(row);
    if (!cells.length) return null;
    return {
      texto: stripTags(cells[0] || ''),
      userName: normalizeWhitespace(stripTags(cells[1] || '')),
      createdAt: parseBrDate(stripTags(cells[2] || '')),
    };
  }).filter((item) => item?.texto);

  const setores = extractCheckedCheckboxLabels(html, 'setores_envolvidos[]');
  const status = extractSelectValue(html, 'status');
  const prazo = parseIsoDateInput(extractInputValue(html, 'prazo'));

  return {
    legacyId,
    name,
    assuntoTemplate,
    prioridadeDefault: isTruthyLegacy(prioridade),
    observacoesGeraisTemplate: observacoes.map((item) => joinObservationText(item)).join('\n\n').trim() || null,
    isRecorrenteDefault: normalizeText(isRecorrenteLegacy) === 'sim',
    recorrenciaTipoLegacy,
    recorrenciaDataBaseDefault: parseIsoDateInput(recorrenciaDataBase),
    recorrenciaPrazoReaberturaDias: parsePositiveInt(recorrenciaPrazoReaberturaDias),
    setores,
    clientes,
    responsaveis,
    subtarefas,
    observacoes,
    history,
    statusLegacy: status,
    prazoLegacy: prazo,
    createdAt: history[0]?.createdAt || null,
    updatedAt: newestDate(history.at(-1)?.createdAt, ...observacoes.map((item) => item.createdAt)) || history[0]?.createdAt || null,
  };
}

function parseLegacyDemandaDetail(html, legacyId, summary) {
  const protocol = normalizeWhitespace(
    extractFirst('Protocolo:\\s*([^<]+)', html, 'i') || summary?.protocolo || '',
  );
  const assunto = extractInputValue(html, 'assunto');
  const prioridade = extractSelectValue(html, 'prioridade');
  const statusLegacy = extractSelectValue(html, 'status');
  const prazoInput = parseIsoDateInput(extractInputValue(html, 'prazo'));
  const isRecorrenteLegacy = extractSelectValue(html, 'tipo');
  const recorrenciaTipoLegacy = extractSelectValue(html, 'tipo_recorrencia');
  const recorrenciaDataBase = parseIsoDateInput(extractInputValue(html, 'data_base_recorrencia'));
  const recorrenciaPrazo = parsePositiveInt(extractInputValue(html, 'prazo_recorrencia'));
  const historyText = extractTextareaAfterLabel(html, 'Histórico');
  const history = parseLegacyHistory(historyText);

  const clientsSection = extractSection(html, 'Adicionar Clientes', 'Adicionar Responsavel');
  const responsaveisSection = extractSection(html, 'Adicionar Responsavel', 'Adicionar Tarefas');
  const tarefasSection = extractSection(html, 'Adicionar Tarefas', 'Adicionar Observa');
  const observacoesSection = extractSection(html, 'Adicionar Observa', 'Prazo');

  const clientes = extractTableRows(clientsSection).map((row) => {
    const cells = extractCells(row);
    if (!cells.length) return null;
    const parsed = parseLegacyClientCell(cells[0]);
    if (!parsed.name) return null;
    return {
      ...parsed,
      createdByName: normalizeWhitespace(stripTags(cells[1] || '')),
      createdAt: parseBrDate(stripTags(cells[2] || '')),
    };
  }).filter(Boolean);

  const responsaveis = extractTableRows(responsaveisSection).map((row) => {
    const cells = extractCells(row);
    if (!cells.length) return null;
    return {
      name: normalizeWhitespace(stripTags(cells[0] || '')),
      isPrincipal: /fa-check-square/i.test(cells[0] || ''),
      createdByName: normalizeWhitespace(stripTags(cells[1] || '')),
      createdAt: parseBrDate(stripTags(cells[2] || '')),
    };
  }).filter((item) => item?.name);

  const subtarefas = extractTableRows(tarefasSection).map((row, index) => {
    const cells = extractCells(row);
    if (!cells.length) return null;
    return {
      titulo: stripTags(cells[0] || ''),
      concluida: /fa-check-square/i.test(cells[0] || ''),
      responsavelName: normalizeWhitespace(stripTags(cells[1] || '')),
      createdAt: parseBrDate(stripTags(cells[2] || '')),
      ordem: index,
    };
  }).filter((item) => item?.titulo);

  const observacoes = extractTableRows(observacoesSection).map((row) => {
    const cells = extractCells(row);
    if (!cells.length) return null;
    return {
      texto: stripTags(cells[0] || ''),
      userName: normalizeWhitespace(stripTags(cells[1] || '')),
      createdAt: parseBrDate(stripTags(cells[2] || '')),
    };
  }).filter((item) => item?.texto);

  const setores = extractCheckedCheckboxLabels(html, 'setores_envolvidos[]');
  const anexosPageUrl = extractFirst('href=["\'](/painel/demandas/anexos/\\d+)["\']', html, 'i');
  const createdAt = history[0]?.createdAt
    || summary?.dataAbertura
    || summary?.listCreatedAt
    || observacoes[0]?.createdAt
    || null;

  const updatedAt = newestDate(
    history.at(-1)?.createdAt,
    ...observacoes.map((item) => item.createdAt),
    ...subtarefas.map((item) => item.createdAt),
  ) || createdAt;

  return {
    legacyId,
    protocolo: protocol,
    assunto,
    prioridade: isTruthyLegacy(prioridade),
    prazo: pickFirstValidDate(prazoInput, summary?.prazo),
    statusLegacy,
    observacoesGerais: null,
    isRecorrente: normalizeText(isRecorrenteLegacy) === 'sim',
    recorrenciaTipoLegacy,
    recorrenciaDataBase,
    recorrenciaPrazoReaberturaDias: recorrenciaPrazo,
    setores,
    clientes,
    responsaveis,
    subtarefas,
    observacoes,
    history,
    createdAt,
    updatedAt,
    dataAbertura: summary?.dataAbertura || null,
    anexosPageUrl: anexosPageUrl ? `${LEGACY_BASE_URL}${anexosPageUrl}` : null,
    origemSummary: summary || null,
  };
}

function parseLegacyDemandaAnexosPage(html, legacyDemandaId) {
  if (/Whoops, looks like something went wrong\./i.test(html)) {
    return {
      anexos: [],
      error: `demanda ${legacyDemandaId}: página de anexos do legado retornou erro interno.`,
    };
  }

  const tableSection = extractSection(html, '<table class="table table-hover">', '</table>');
  const rows = extractTableRows(tableSection);
  const anexos = rows
    .map((row) => {
      const cells = extractCells(row);
      if (!cells.length) return null;
      if (/n[aã]o existem registros cadastrados/i.test(stripTags(cells[0] || ''))) return null;

      const hrefs = [...row.matchAll(/href=["']([^"']+)["']/gi)].map((match) => match[1]);
      const downloadUrl = hrefs
        .map((href) => {
          try {
            return new URL(href, LEGACY_BASE_URL).toString();
          } catch {
            return null;
          }
        })
        .find((href) => href && /\/assets\/uploads\/imgs\/demandas\/|\/download|arquivo/i.test(href));

      if (!downloadUrl) return null;

      const filename = normalizeWhitespace(stripTags(cells[3] || '')) || filenameFromUrl(downloadUrl);
      return {
        legacyId: normalizeWhitespace(stripTags(cells[0] || '')),
        descricao: normalizeWhitespace(stripTags(cells[1] || '')),
        userName: normalizeWhitespace(stripTags(cells[2] || '')),
        filename,
        createdAt: parseBrDate(stripTags(cells[4] || '')),
        downloadUrl,
      };
    })
    .filter(Boolean);

  return { anexos, error: null };
}

function parseIsoDateInput(value) {
  const normalized = normalizeWhitespace(value);
  if (!normalized || normalized === '0000-00-00') return null;
  const match = normalized.match(/^(\d{4})-(\d{2})-(\d{2})$/);
  if (!match) return null;
  const numericYear = Number.parseInt(match[1], 10);
  if (numericYear < 1900) return null;
  return normalized;
}

function parsePositiveInt(value) {
  const parsed = Number.parseInt(String(value || '').replace(/\D/g, ''), 10);
  if (!Number.isFinite(parsed) || parsed <= 0) return null;
  return parsed;
}

function joinObservationText(observation) {
  const prefixParts = [observation.createdAt, observation.userName].filter(Boolean);
  const prefix = prefixParts.length ? `[${prefixParts.join(' | ')}] ` : '';
  return `${prefix}${observation.texto}`.trim();
}

async function collectLegacyUsers(session, options) {
  const firstPage = await session.get('/painel/usuarios/movel?email=%25&nome=%25');
  const totalPages = parsePaginationMax(firstPage);
  const pagesToFetch = limitArray(
    Array.from({ length: totalPages }, (_, index) => index + 1),
    options.limitUsers ? Math.ceil(options.limitUsers / 10) : null,
  );
  const pageHtmls = await mapConcurrent(pagesToFetch, options.concurrency, async (page) =>
    page === 1
      ? firstPage
      : session.get(`/painel/usuarios/movel?email=%25&nome=%25&page=${page}`));
  const users = pageHtmls.flatMap(parseLegacyUsersPage);
  return dedupeBy(users, (item) => item.legacyId).slice(0, options.limitUsers ?? undefined);
}

async function collectLegacyClients(session, options) {
  const [firstPj, firstPf] = await Promise.all([
    session.get('/painel/clientes/movel'),
    session.get('/painel/clientes/movel/fisica'),
  ]);
  const pjPages = limitArray(
    Array.from({ length: parsePaginationMax(firstPj) }, (_, index) => index + 1),
    options.limitClients ? Math.ceil(options.limitClients / 100) : null,
  );
  const pfPages = limitArray(
    Array.from({ length: parsePaginationMax(firstPf) }, (_, index) => index + 1),
    options.limitClients ? Math.ceil(options.limitClients / 100) : null,
  );

  const [pjHtmls, pfHtmls] = await Promise.all([
    mapConcurrent(pjPages, options.concurrency, async (page) =>
      page === 1 ? firstPj : session.get(`/painel/clientes/movel?cli=&cidade=&parc=&fan=&ent=&end=&status=&aud=&page=${page}`)),
    mapConcurrent(pfPages, options.concurrency, async (page) =>
      page === 1 ? firstPf : session.get(`/painel/clientes/movel/fisica?cli=&cidade=&parc=&fan=&ent=&end=&status=&aud=&page=${page}`)),
  ]);

  const clients = [
    ...pjHtmls.flatMap((html) => parseLegacyClientsPage(html, 'pj')),
    ...pfHtmls.flatMap((html) => parseLegacyClientsPage(html, 'pf')),
  ];

  const dedupedClients = dedupeBy(clients, (item) => item.legacyId).slice(0, options.limitClients ?? undefined);
  return mapConcurrent(dedupedClients, options.concurrency, async (client, index) => {
    console.log(`cliente ${index + 1}/${dedupedClients.length}: ${client.legacyId}`);
    if (!client.detailPath) return client;
    try {
      const html = await session.get(client.detailPath);
      return parseLegacyClientDetail(html, client);
    } catch (error) {
      console.warn(`cliente ${client.legacyId}: falha ao carregar detalhe (${error instanceof Error ? error.message : String(error)})`);
      return client;
    }
  });
}

async function collectLegacyTemplates(session, options) {
  const listing = await session.get('/painel/demandas/temp');
  const ids = limitArray(parseLegacyTemplateIds(listing), options.limitTemplates);
  const details = await mapConcurrent(ids, options.concurrency, async (legacyId, index) => {
    console.log(`template ${index + 1}/${ids.length}: ${legacyId}`);
    const html = await session.get(`/painel/demandas/editar/temp/${legacyId}`);
    return parseLegacyTemplateDetail(html, legacyId);
  });
  return details;
}

async function collectLegacyDemandSummaries(session, options) {
  const demandIndex = await session.get('/painel/demandas');
  const csrf = extractInputValue(demandIndex, '_token');
  const firstSearch = await session.postForm('/painel/demandas/pesquisar', {
    _token: csrf,
    cliente: '',
    assunto: '',
    status: '',
    tipo: '',
    protocolo: '',
    prioridade: '',
    criador: '',
    resp: '',
    resp_principal: '',
    setor: '',
    condicao: '',
    tarefa: '',
    obs: '',
    di_dem: '',
    df_dem: '',
    prazo_di_dem: '',
    prazo_df_dem: '',
  });
  const totalPages = parsePaginationMax(firstSearch);
  const pages = limitArray(Array.from({ length: totalPages }, (_, index) => index + 1), options.limitDemandPages);
  const htmlPages = await mapConcurrent(pages, options.concurrency, async (page) => {
    if (page === 1) return firstSearch;
    return session.get(buildBlankDemandSearchQuery(page));
  });
  return dedupeBy(htmlPages.flatMap(parseLegacyDemandSearchPage), (item) => item.legacyId);
}

async function collectLegacyDemandDetails(session, summaries, options, progress = {}) {
  const limitedSummaries = limitArray(summaries, options.limitDemands);
  const offset = progress.offset || 0;
  const total = progress.total || limitedSummaries.length;
  return mapConcurrent(limitedSummaries, options.concurrency, async (summary, index) => {
    console.log(`demanda ${offset + index + 1}/${total}: ${summary.legacyId}`);
    const html = await session.get(`/painel/demandas/editar/${summary.legacyId}`);
    return parseLegacyDemandaDetail(html, summary.legacyId, summary);
  });
}

function dedupeBy(items, getKey) {
  const map = new Map();
  for (const item of items) {
    const key = getKey(item);
    if (!key || map.has(key)) continue;
    map.set(key, item);
  }
  return [...map.values()];
}

function buildUserDirectory(users) {
  const byLegacyId = new Map();
  const byExactName = new Map();
  for (const user of users) {
    byLegacyId.set(user.legacyId, user);
    const key = normalizeText(user.name);
    const list = byExactName.get(key) || [];
    list.push(user);
    byExactName.set(key, list);
  }
  return { byLegacyId, byExactName };
}

function collectReferencedUserNames(snapshot) {
  const names = new Set();
  const capture = (value) => {
    const normalized = normalizeWhitespace(value);
    if (normalized) names.add(normalized);
  };

  for (const template of snapshot.templates || []) {
    template.responsaveis?.forEach((item) => capture(item.name));
    template.subtarefas?.forEach((item) => capture(item.responsavelName));
    template.observacoes?.forEach((item) => capture(item.userName));
    template.history?.forEach((item) => capture(item.userName));
  }

  for (const demanda of snapshot.demandas || []) {
    demanda.responsaveis?.forEach((item) => capture(item.name));
    demanda.subtarefas?.forEach((item) => capture(item.responsavelName));
    demanda.observacoes?.forEach((item) => capture(item.userName));
    demanda.history?.forEach((item) => capture(item.userName));
  }

  return names;
}

function resolveLegacyUserByName(userDirectory, name) {
  if (!name) return null;
  const candidates = userDirectory.byExactName.get(normalizeText(name)) || [];
  if (candidates.length === 0) return null;
  if (candidates.length === 1) return candidates[0];

  const preferred = candidates.find((candidate) => normalizeText(candidate.type) !== 'cliente')
    || candidates.find((candidate) => normalizeEmail(candidate.email)?.endsWith('@luxustelefonia.com.br'))
    || candidates[0];
  return preferred;
}

async function loadReferenceData(supabase) {
  const [setores, roles] = await Promise.all([
    supabase.select('Setor?select=id,name,slug'),
    supabase.select('Role?select=id,slug'),
  ]);

  const setoresByName = new Map();
  for (const setor of setores) setoresByName.set(normalizeText(setor.name), setor);

  const rolesBySlug = new Map();
  for (const role of roles) rolesBySlug.set(role.slug, role);

  return { setores, setoresByName, rolesBySlug };
}

async function queryOne(supabase, pathname) {
  const rows = await supabase.select(pathname);
  return Array.isArray(rows) ? rows[0] ?? null : null;
}

async function ensureMigrationUser(context) {
  const existing = await queryOne(
    context.supabase,
    `User?select=id,email,name&email=eq.${encodeEq(LEGACY_MIGRATION_EMAIL)}&limit=1`,
  );
  if (existing) return existing.id;

  const createdRows = await context.supabase.insert('User', {
    email: LEGACY_MIGRATION_EMAIL,
    password_hash: getImportedPasswordHash(),
    name: 'Legacy Migration',
    active: true,
  });
  const user = Array.isArray(createdRows) ? createdRows[0] : createdRows;
  const roleId = context.reference.rolesBySlug.get('admin')?.id || context.reference.rolesBySlug.get('colaborador')?.id;
  if (roleId) {
    await ensureUserRole(context.supabase, user.id, roleId);
  }
  return user.id;
}

async function ensureUserRole(supabase, userId, roleId) {
  const existing = await queryOne(
    supabase,
    `user_role?select=user_id&user_id=eq.${encodeEq(userId)}&role_id=eq.${encodeEq(roleId)}&limit=1`,
  );
  if (existing) return;
  await supabase.insert('user_role', {
    user_id: userId,
    role_id: roleId,
  }, { select: '*' });
}

async function ensureImportedUser(context, descriptor) {
  const { map } = context;
  const legacyUser = descriptor.legacyUser || null;
  const legacyKey = legacyUser?.legacyId || `name:${normalizeText(descriptor.name)}`;
  const mappedId = map.users[legacyKey];
  if (mappedId) return mappedId;

  const normalizedName = normalizeWhitespace(descriptor.name);
  if (!normalizedName) return context.migrationUserId;

  const email = normalizeEmail(descriptor.email)
    || normalizeEmail(legacyUser?.email)
    || deterministicEmail('legacy-user', legacyUser?.legacyId || slugify(normalizedName), normalizedName);

  const existing = await queryOne(
    context.supabase,
    `User?select=id,email,name&email=eq.${encodeEq(email)}&limit=1`,
  );

  const desiredRoleSlug = mapLegacyRoleSlug(descriptor.type || legacyUser?.type);
  const roleId = context.reference.rolesBySlug.get(desiredRoleSlug)?.id
    || context.reference.rolesBySlug.get('colaborador')?.id;

  let userId = existing?.id;
  if (!userId) {
    const createdRows = await context.supabase.insert('User', {
      email,
      password_hash: getImportedPasswordHash(),
      name: normalizedName,
      active: true,
    });
    userId = (Array.isArray(createdRows) ? createdRows[0] : createdRows).id;
  }

  if (roleId) await ensureUserRole(context.supabase, userId, roleId);
  map.users[legacyKey] = userId;
  await saveMap(map);
  return userId;
}

function mapLegacyRoleSlug(type) {
  const normalized = normalizeText(type);
  if (normalized === 'administrador') return 'admin';
  if (normalized === 'gestor') return 'gestor';
  if (normalized === 'cliente') return 'cliente';
  return 'colaborador';
}

function buildClientMutationPayload(client) {
  const payload = {
    name: normalizeWhitespace(client.name),
    tipo_pessoa: client.tipoPessoa || null,
    documento: normalizeDocumento(client.documento),
    active: typeof client.active === 'boolean' ? client.active : undefined,
    legacy_id: client.legacyId || null,
    nome_fantasia: normalizeWhitespace(client.nomeFantasia) || null,
    ramo_atividade: normalizeWhitespace(client.ramoAtividade) || null,
    inscricao_estadual: normalizeWhitespace(client.inscricaoEstadual) || null,
    cep: normalizeDocumento(client.cep),
    endereco: normalizeWhitespace(client.endereco) || null,
    numero: normalizeWhitespace(client.numero) || null,
    complemento: normalizeWhitespace(client.complemento) || null,
    bairro: normalizeWhitespace(client.bairro) || null,
    cidade: normalizeWhitespace(client.cidade) || null,
    uf: normalizeWhitespace(client.uf)?.toUpperCase() || null,
    telefone: normalizeWhitespace(client.telefone) || null,
    celular: normalizeWhitespace(client.celular) || null,
    contato: normalizeWhitespace(client.contato) || null,
    email: normalizeEmail(client.email),
    observacoes_cadastro: normalizeWhitespace(client.observacoesCadastro) || null,
  };

  return Object.fromEntries(
    Object.entries(payload).filter(([, value]) => value !== null && value !== undefined && value !== ''),
  );
}

async function syncImportedClient(context, clientId, client) {
  const payload = buildClientMutationPayload(client);
  if (!Object.keys(payload).length) return;
  await context.supabase.patch('Cliente', `id=eq.${encodeEq(clientId)}`, payload, { select: 'id' });
}

async function findOrCreateClient(context, client) {
  const legacyKey = client.legacyId || `${client.tipoPessoa}:${client.documento || normalizeText(client.name)}`;
  const mappedId = context.map.clients[legacyKey];
  if (mappedId) {
    await syncImportedClient(context, mappedId, client);
    return mappedId;
  }

  let existing = null;
  if (client.legacyId) {
    existing = await queryOne(
      context.supabase,
      `Cliente?select=id,name,legacy_id&legacy_id=eq.${encodeEq(client.legacyId)}&limit=1`,
    );
  }
  if (!existing && client.documento) {
    existing = await queryOne(
      context.supabase,
      `Cliente?select=id,name,documento&documento=eq.${encodeEq(client.documento)}&limit=1`,
    );
  }
  if (!existing) {
    const nameEq = encodeEq(client.name);
    const tipoEq = client.tipoPessoa ? `&tipo_pessoa=eq.${encodeEq(client.tipoPessoa)}` : '';
    existing = await queryOne(
      context.supabase,
      `Cliente?select=id,name,tipo_pessoa&name=eq.${nameEq}${tipoEq}&limit=1`,
    );
  }

  let clientId = existing?.id;
  if (!clientId) {
    const createdRows = await context.supabase.insert('Cliente', {
      ...buildClientMutationPayload(client),
      active: client.active ?? true,
    });
    clientId = (Array.isArray(createdRows) ? createdRows[0] : createdRows).id;
  } else {
    await syncImportedClient(context, clientId, client);
  }

  context.map.clients[legacyKey] = clientId;
  await saveMap(context.map);
  return clientId;
}

async function importUsers(context, users) {
  for (const user of users) {
    console.log(`importando usuário ${user.legacyId} - ${user.name}`);
    await ensureImportedUser(context, {
      name: user.name,
      email: user.email,
      legacyUser: user,
      type: user.type,
    });
  }
}

function resolveSetorIds(reference, setorNames, warnings, entityLabel) {
  const resolved = [];
  for (const setorName of setorNames || []) {
    const setor = reference.setoresByName.get(normalizeText(setorName));
    if (!setor) {
      warnings.push(`${entityLabel}: setor não encontrado no novo sistema: ${setorName}`);
      continue;
    }
    resolved.push(setor.id);
  }
  return [...new Set(resolved)];
}

async function buildEventRows(context, type, ownerId, history, fallbackCreatedAt = null) {
  const rows = [];
  for (const item of history || []) {
    let userId = null;
    if (item.userName) {
      const legacyUser = resolveLegacyUserByName(context.userDirectory, item.userName);
      userId = await ensureImportedUser(context, {
        name: item.userName,
        legacyUser,
        type: legacyUser?.type,
      });
    }
    rows.push({
      [`${type}_id`]: ownerId,
      user_id: userId,
      tipo: 'legado',
      descricao: item.raw,
      metadata: { imported: true },
      created_at: (item.createdAt || fallbackCreatedAt) ? (item.createdAt || fallbackCreatedAt).replace('T', ' ') : undefined,
    });
  }
  return rows;
}

function normalizeRowsForInsert(rows) {
  if (!rows.length) return rows;

  const keys = new Set();
  for (const row of rows) {
    for (const key of Object.keys(row)) keys.add(key);
  }

  return rows.map((row) => {
    const normalized = {};
    for (const key of keys) {
      if (Object.prototype.hasOwnProperty.call(row, key)) {
        normalized[key] = row[key] === undefined ? null : row[key];
      } else {
        normalized[key] = null;
      }
    }
    return normalized;
  });
}

async function replaceCollection(supabase, table, filterQuery, rows) {
  await supabase.request(`${table}?${filterQuery}`, { method: 'DELETE' });
  if (!rows.length) return;
  await supabase.insert(table, normalizeRowsForInsert(rows), { select: '*' });
}

async function tryPatchDemandaLegacyId(supabase, demandaId, legacyId, warnings) {
  if (!demandaId || !legacyId) return;
  try {
    await supabase.patch('Demanda', `id=eq.${encodeEq(demandaId)}`, {
      legacy_id: legacyId,
    }, { select: 'id' });
  } catch (error) {
    warnings?.push(`demanda ${legacyId}: não consegui gravar legacy_id em Demanda; aplique a migration 20260504_add_legacy_attachment_links.sql para habilitar upload direto no legado.`);
  }
}

function getAnexosBucketName() {
  return (process.env.SUPABASE_STORAGE_BUCKET || 'demandas-anexos').trim() || 'demandas-anexos';
}

function buildSupabaseStoragePath(bucket, objectPath) {
  return `supabase://${bucket}/${objectPath}`;
}

function buildLegacyStoragePath(legacyDemandaId, legacyAnexoId, downloadUrl) {
  return `legacy://demandas/${encodeURIComponent(legacyDemandaId || '')}/anexos/${encodeURIComponent(legacyAnexoId || '')}?url=${encodeURIComponent(downloadUrl || '')}`;
}

async function ensureAnexosBucket(context) {
  if (context.anexosBucketReady) return context.anexosBucket;

  const bucket = getAnexosBucketName();
  const buckets = await context.supabase.listBuckets();
  const exists = Array.isArray(buckets)
    && buckets.some((item) => String(item?.name || item?.id || '').trim() === bucket);

  if (!exists) {
    await context.supabase.createBucket(bucket, false);
  }

  context.anexosBucket = bucket;
  context.anexosBucketReady = true;
  return bucket;
}

async function downloadLegacyAttachment(downloadUrl) {
  const maxAttempts = 5;
  let lastError = null;

  for (let attempt = 1; attempt <= maxAttempts; attempt += 1) {
    try {
      const response = await fetch(downloadUrl, { redirect: 'follow' });
      if (!response.ok) {
        const body = await response.text();
        throw new Error(`download falhou ${response.status}: ${body.slice(0, 200)}`);
      }

      return {
        buffer: Buffer.from(await response.arrayBuffer()),
        contentType: response.headers.get('content-type') || null,
      };
    } catch (error) {
      lastError = error;
      if (attempt >= maxAttempts) break;
      await sleep(500 * attempt);
    }
  }

  throw lastError;
}

async function syncDemandaAnexos(context, legacy, demanda, demandaId) {
  const mapKey = demanda.legacyId;
  if (!demandaId || !mapKey) return;
  if (context.map.anexos[mapKey] && !context.options.forceAnexos) return;

  const attachmentPagePath = `/painel/demandas/anexos/${mapKey}`;
  let parsed;
  try {
    const html = await legacy.get(attachmentPagePath);
    parsed = parseLegacyDemandaAnexosPage(html, mapKey);
  } catch (error) {
    context.warnings.push(`demanda ${mapKey}: falha ao consultar anexos no legado (${error instanceof Error ? error.message : String(error)}).`);
    return;
  }

  if (parsed.error) {
    context.warnings.push(parsed.error);
    return;
  }

  const anexoRows = [];

  for (const anexo of parsed.anexos) {
    try {
      const originalFilename = sanitizeFilename(anexo.filename || filenameFromUrl(anexo.downloadUrl)) || 'arquivo';
      const mimeType = guessMimeType(originalFilename);

      if (context.options.linkAnexos) {
        anexoRows.push({
          demanda_id: demandaId,
          filename: originalFilename,
          mime_type: mimeType,
          size: 0,
          storage_path: buildLegacyStoragePath(mapKey, anexo.legacyId || '', anexo.downloadUrl),
        });
        continue;
      }

      const bucket = await ensureAnexosBucket(context);
      const download = await downloadLegacyAttachment(anexo.downloadUrl);
      const objectPath = `demandas/${demandaId}/legacy-${sanitizeFilename(anexo.legacyId || 'sem-id')}-${originalFilename}`;
      await context.supabase.uploadObject(bucket, objectPath, download.buffer, mimeType, { upsert: false });
      anexoRows.push({
        demanda_id: demandaId,
        filename: originalFilename,
        mime_type: download.contentType || mimeType,
        size: download.buffer.length,
        storage_path: buildSupabaseStoragePath(bucket, objectPath),
      });
    } catch (error) {
      context.warnings.push(`demanda ${mapKey}: falha ao copiar anexo "${anexo.filename}" (${error instanceof Error ? error.message : String(error)}).`);
    }
  }

  await replaceCollection(
    context.supabase,
    'anexo',
    `demanda_id=eq.${encodeEq(demandaId)}`,
    anexoRows,
  );

  await tryPatchDemandaLegacyId(context.supabase, demandaId, mapKey, context.warnings);

  const currentDemanda = await queryOne(
    context.supabase,
    `Demanda?select=id,observacoes_gerais&id=eq.${encodeEq(demandaId)}&limit=1`,
  );
  const observacoesGerais = currentDemanda?.observacoes_gerais;
  if (typeof observacoesGerais === 'string' && observacoesGerais.startsWith('Anexos originais no legado: ')) {
    await context.supabase.patch('Demanda', `id=eq.${encodeEq(demandaId)}`, {
      observacoes_gerais: null,
    }, { select: 'id' });
  }

  context.map.anexos[mapKey] = demandaId;
  await saveMap(context.map);
}

async function backfillMappedDemandAttachments(context, legacy, options) {
  const pendingEntries = Object.entries(context.map.demandas)
    .filter(([legacyId, demandaId]) => legacyId && demandaId && (options.forceAnexos || !context.map.anexos[legacyId]))
    .filter(([legacyId]) => !options.demandIds?.length || options.demandIds.includes(legacyId));
  const limitedEntries = limitArray(pendingEntries, options.limitDemands);

  console.log(`demandas com anexos pendentes: ${limitedEntries.length}`);
  let index = 0;
  await mapConcurrent(limitedEntries, options.concurrency, async ([legacyId, demandaId]) => {
    index += 1;
    console.log(`anexos ${index}/${limitedEntries.length}: demanda ${legacyId}`);
    await syncDemandaAnexos(context, legacy, { legacyId }, demandaId);
  });
}

async function importTemplates(context, templates) {
  for (const template of templates) {
    console.log(`importando template ${template.legacyId} - ${template.name}`);
    const mapKey = template.legacyId;
    if (context.map.templates[mapKey]) continue;

    const existing = await queryOne(
      context.supabase,
      `Template?select=id,name,assunto_template&name=eq.${encodeEq(template.name)}&assunto_template=eq.${encodeEq(template.assuntoTemplate || '')}&limit=1`,
    );
    let templateId = existing?.id;

    const creatorName = template.history[0]?.userName || '';
    const creatorLegacy = resolveLegacyUserByName(context.userDirectory, creatorName);
    const creatorId = await ensureImportedUser(context, {
      name: creatorName || 'Legacy Migration',
      legacyUser: creatorLegacy,
      type: creatorLegacy?.type,
    });

    const warnings = [];
    const recurrenceType = mapLegacyRecorrenciaTipo(template.recorrenciaTipoLegacy);
    const isRecorrenteDefault = template.isRecorrenteDefault && !!recurrenceType;
    if (template.isRecorrenteDefault && !recurrenceType) {
      warnings.push(`template ${template.legacyId}: recorrência "${template.recorrenciaTipoLegacy}" não suportada no novo sistema.`);
    }

    if (!templateId) {
      const createdRows = await context.supabase.insert('Template', {
        name: template.name,
        descricao: `Migrado do legado (template ${template.legacyId})`,
        assunto_template: template.assuntoTemplate || null,
        prioridade_default: !!template.prioridadeDefault,
        observacoes_gerais_template: template.observacoesGeraisTemplate,
        is_recorrente_default: isRecorrenteDefault,
        recorrencia_tipo: recurrenceType,
        recorrencia_data_base_default: isRecorrenteDefault ? template.recorrenciaDataBaseDefault : null,
        recorrencia_prazo_reabertura_dias: isRecorrenteDefault ? template.recorrenciaPrazoReaberturaDias : null,
        criador_id: creatorId || context.migrationUserId,
        created_at: template.createdAt ? template.createdAt.replace('T', ' ') : undefined,
        updated_at: template.updatedAt ? template.updatedAt.replace('T', ' ') : undefined,
      });
      templateId = (Array.isArray(createdRows) ? createdRows[0] : createdRows).id;
    }

    const setorIds = resolveSetorIds(context.reference, template.setores, warnings, `template ${template.legacyId}`);
    const clienteIds = [];
    for (const cliente of template.clientes) clienteIds.push(await findOrCreateClient(context, cliente));

    const responsavelRows = [];
    for (const responsavel of template.responsaveis) {
      const legacyUser = resolveLegacyUserByName(context.userDirectory, responsavel.name);
      const userId = await ensureImportedUser(context, {
        name: responsavel.name,
        legacyUser,
        type: legacyUser?.type,
      });
      responsavelRows.push({ userId, isPrincipal: !!responsavel.isPrincipal });
    }

    const subtarefaRows = [];
    for (const subtarefa of template.subtarefas) {
      let responsavelUserId = null;
      if (subtarefa.responsavelName) {
        const legacyUser = resolveLegacyUserByName(context.userDirectory, subtarefa.responsavelName);
        responsavelUserId = await ensureImportedUser(context, {
          name: subtarefa.responsavelName,
          legacyUser,
          type: legacyUser?.type,
        });
      }
      subtarefaRows.push({
        titulo: subtarefa.titulo,
        ordem: subtarefa.ordem,
        responsavel_user_id: responsavelUserId,
      });
    }

    await replaceCollection(context.supabase, 'template_setor', `template_id=eq.${encodeEq(templateId)}`, setorIds.map((setorId) => ({
      template_id: templateId,
      setor_id: setorId,
    })));
    await replaceCollection(context.supabase, 'template_cliente', `template_id=eq.${encodeEq(templateId)}`, clienteIds.map((clienteId) => ({
      template_id: templateId,
      cliente_id: clienteId,
    })));
    await replaceCollection(context.supabase, 'template_responsavel', `template_id=eq.${encodeEq(templateId)}`, responsavelRows.map((item) => ({
      template_id: templateId,
      user_id: item.userId,
      is_principal: item.isPrincipal,
    })));
    await replaceCollection(context.supabase, 'template_subtarefa', `template_id=eq.${encodeEq(templateId)}`, subtarefaRows.map((item) => ({
      template_id: templateId,
      titulo: item.titulo,
      ordem: item.ordem,
      responsavel_user_id: item.responsavel_user_id,
    })));
    await replaceCollection(
      context.supabase,
      'template_evento',
      `template_id=eq.${encodeEq(templateId)}`,
      await buildEventRows(context, 'template', templateId, template.history, template.createdAt),
    );

    context.map.templates[mapKey] = templateId;
    await saveMap(context.map);
    if (warnings.length) context.warnings.push(...warnings);
  }
}

function isFinalStatus(status) {
  return status === 'concluido' || status === 'cancelado';
}

async function importDemandas(context, demandas, options = DEFAULT_OPTIONS) {
  for (const demanda of demandas) {
    console.log(`importando demanda ${demanda.legacyId} - ${demanda.protocolo}`);
    const mapKey = demanda.legacyId;
    if (context.map.demandas[mapKey]) continue;

    const existing = await queryOne(
      context.supabase,
      `Demanda?select=id,protocolo&protocolo=eq.${encodeEq(demanda.protocolo)}&limit=1`,
    );

    const creatorName = demanda.history[0]?.userName || '';
    const creatorLegacy = resolveLegacyUserByName(context.userDirectory, creatorName);
    const creatorId = await ensureImportedUser(context, {
      name: creatorName || 'Legacy Migration',
      legacyUser: creatorLegacy,
      type: creatorLegacy?.type,
    });

    const warnings = [];
    const recurrenceType = mapLegacyRecorrenciaTipo(demanda.recorrenciaTipoLegacy);
    const isRecorrente = demanda.isRecorrente && !!recurrenceType;
    if (demanda.isRecorrente && !recurrenceType) {
      warnings.push(`demanda ${demanda.legacyId}: recorrência "${demanda.recorrenciaTipoLegacy}" não suportada no novo sistema.`);
    }

    let demandaId = existing?.id;
    if (!demandaId) {
      const latestObservation = newestDate(...demanda.observacoes.map((item) => item.createdAt));
      const createdRows = await context.supabase.insert('Demanda', {
        protocolo: demanda.protocolo,
        assunto: demanda.assunto,
        prioridade: !!demanda.prioridade,
        prazo: demanda.prazo,
        status: mapLegacyStatus(demanda.statusLegacy),
        criador_id: creatorId || context.migrationUserId,
        observacoes_gerais: demanda.observacoesGerais || null,
        is_recorrente: isRecorrente,
        resolvido_em: isFinalStatus(mapLegacyStatus(demanda.statusLegacy)) ? (demanda.updatedAt ? demanda.updatedAt.replace('T', ' ') : null) : null,
        ultima_observacao_em: latestObservation ? latestObservation.replace('T', ' ') : null,
        created_at: demanda.createdAt ? demanda.createdAt.replace('T', ' ') : undefined,
        updated_at: demanda.updatedAt ? demanda.updatedAt.replace('T', ' ') : undefined,
      });
      demandaId = (Array.isArray(createdRows) ? createdRows[0] : createdRows).id;
      await tryPatchDemandaLegacyId(context.supabase, demandaId, demanda.legacyId, warnings);
    }
    else if (demanda.legacyId) {
      await tryPatchDemandaLegacyId(context.supabase, demandaId, demanda.legacyId, warnings);
    }

    const setorIds = resolveSetorIds(context.reference, demanda.setores, warnings, `demanda ${demanda.legacyId}`);
    const clienteIds = [];
    for (const cliente of demanda.clientes) clienteIds.push(await findOrCreateClient(context, cliente));

    const responsavelRows = [];
    for (const responsavel of demanda.responsaveis) {
      const legacyUser = resolveLegacyUserByName(context.userDirectory, responsavel.name);
      const userId = await ensureImportedUser(context, {
        name: responsavel.name,
        legacyUser,
        type: legacyUser?.type,
      });
      responsavelRows.push({ userId, isPrincipal: !!responsavel.isPrincipal });
    }

    const subtarefaRows = [];
    for (const subtarefa of demanda.subtarefas) {
      let responsavelUserId = null;
      if (subtarefa.responsavelName) {
        const legacyUser = resolveLegacyUserByName(context.userDirectory, subtarefa.responsavelName);
        responsavelUserId = await ensureImportedUser(context, {
          name: subtarefa.responsavelName,
          legacyUser,
          type: legacyUser?.type,
        });
      }
      subtarefaRows.push({
        titulo: subtarefa.titulo,
        concluida: !!subtarefa.concluida,
        ordem: subtarefa.ordem,
        responsavel_user_id: responsavelUserId,
      });
    }

    const observacaoRows = [];
    for (const observacao of demanda.observacoes) {
      const legacyUser = resolveLegacyUserByName(context.userDirectory, observacao.userName);
      const userId = await ensureImportedUser(context, {
        name: observacao.userName || 'Legacy Migration',
        legacyUser,
        type: legacyUser?.type,
      });
      observacaoRows.push({
        demanda_id: demandaId,
        user_id: userId || context.migrationUserId,
        texto: observacao.texto,
        created_at: observacao.createdAt ? observacao.createdAt.replace('T', ' ') : undefined,
      });
    }

    await replaceCollection(context.supabase, 'demanda_setor', `demanda_id=eq.${encodeEq(demandaId)}`, setorIds.map((setorId) => ({
      demanda_id: demandaId,
      setor_id: setorId,
    })));
    await replaceCollection(context.supabase, 'demanda_cliente', `demanda_id=eq.${encodeEq(demandaId)}`, clienteIds.map((clienteId) => ({
      demanda_id: demandaId,
      cliente_id: clienteId,
    })));
    await replaceCollection(context.supabase, 'demanda_responsavel', `demanda_id=eq.${encodeEq(demandaId)}`, responsavelRows.map((item) => ({
      demanda_id: demandaId,
      user_id: item.userId,
      is_principal: item.isPrincipal,
    })));
    await replaceCollection(context.supabase, 'subtarefa', `demanda_id=eq.${encodeEq(demandaId)}`, subtarefaRows.map((item) => ({
      demanda_id: demandaId,
      titulo: item.titulo,
      concluida: item.concluida,
      ordem: item.ordem,
      responsavel_user_id: item.responsavel_user_id,
    })));
    await replaceCollection(context.supabase, 'observacao', `demanda_id=eq.${encodeEq(demandaId)}`, observacaoRows);
    await replaceCollection(
      context.supabase,
      'demanda_evento',
      `demanda_id=eq.${encodeEq(demandaId)}`,
      await buildEventRows(context, 'demanda', demandaId, demanda.history, demanda.createdAt),
    );

    if (isRecorrente && demanda.recorrenciaDataBase) {
      await replaceCollection(context.supabase, 'recorrencia_config', `demanda_id=eq.${encodeEq(demandaId)}`, [{
        demanda_id: demandaId,
        data_base: demanda.recorrenciaDataBase,
        tipo: recurrenceType,
        prazo_reabertura_dias: demanda.recorrenciaPrazoReaberturaDias || 1,
      }]);
    }

    if (!options.skipAnexos) {
      await syncDemandaAnexos(context, context.legacy, demanda, demandaId);
    }

    context.map.demandas[mapKey] = demandaId;
    await saveMap(context.map);
    if (warnings.length) context.warnings.push(...warnings);
  }
}

async function importDemandasInBatches(legacy, context, pendingSummaries, options) {
  const limitedSummaries = limitArray(pendingSummaries, options.limitDemands);
  const batches = chunkArray(limitedSummaries, options.demandBatchSize);
  const total = limitedSummaries.length;

  for (let batchIndex = 0; batchIndex < batches.length; batchIndex += 1) {
    const batch = batches[batchIndex];
    const offset = batchIndex * options.demandBatchSize;
    console.log('');
    console.log(`lote de demandas ${batchIndex + 1}/${batches.length} (${batch.length} itens)`);
    const details = await collectLegacyDemandDetails(legacy, batch, options, { offset, total });
    await importDemandas(context, details, options);
  }
}

function summarizeSnapshot(snapshot) {
  return {
    users: snapshot.users.length,
    clients: snapshot.clients.length,
    templates: snapshot.templates.length,
    demandas: snapshot.demandas.length,
    referencedUserNames: collectReferencedUserNames(snapshot).size,
    demandProtocolsSample: snapshot.demandas.slice(0, 5).map((item) => item.protocolo),
    templateNamesSample: snapshot.templates.slice(0, 5).map((item) => item.name),
  };
}

async function main() {
  loadDotEnv(path.join(process.cwd(), '.env'));
  const options = parseArgs(process.argv.slice(2));
  const existingMap = await loadMap();

  const legacyEmail = process.env.LEGACY_EMAIL;
  const legacyPassword = process.env.LEGACY_PASSWORD;
  if (!legacyEmail || !legacyPassword) {
    throw new Error('Defina LEGACY_EMAIL e LEGACY_PASSWORD antes de rodar o migrador.');
  }

  const snapshot = { users: [], clients: [], templates: [], demandas: [] };
  let pendingDemandSummaries = [];
  const legacy = new LegacySession(LEGACY_BASE_URL);
  await legacy.login(legacyEmail, legacyPassword);

  if (options.phases.has('users') || options.phases.has('templates') || options.phases.has('demandas')) {
    console.log('coletando usuários do legado...');
    snapshot.users = await collectLegacyUsers(legacy, options);
  }

  if (options.phases.has('clients')) {
    console.log('coletando clientes do legado...');
    snapshot.clients = await collectLegacyClients(legacy, options);
  }

  if (options.phases.has('templates')) {
    console.log('coletando templates do legado...');
    snapshot.templates = await collectLegacyTemplates(legacy, options);
  }

  if (options.phases.has('demandas')) {
    console.log('coletando listagem de demandas do legado...');
    const summaries = await collectLegacyDemandSummaries(legacy, options);
    pendingDemandSummaries = summaries.filter((item) => !existingMap.demandas[item.legacyId]);
    console.log(`resumos de demandas coletados: ${summaries.length}`);
    console.log(`demandas pendentes para importar neste lote: ${pendingDemandSummaries.length}`);
    if (!options.apply) {
      console.log('coletando detalhes das demandas...');
      snapshot.demandas = await collectLegacyDemandDetails(legacy, pendingDemandSummaries, options);
    }
  }

  if (options.writeSnapshot) await saveSnapshot(snapshot);

  console.log('');
  console.log('Resumo do dry-run:');
  console.log(JSON.stringify(summarizeSnapshot(snapshot), null, 2));

  if (!options.apply) {
    console.log('');
    console.log(`Dry-run concluído. Snapshot salvo em ${SNAPSHOT_FILE}`);
    console.log(`Mapa de importação será mantido em ${MAP_FILE} quando você rodar com --apply.`);
    return;
  }

  if (!process.env.SUPABASE_URL || !process.env.SUPABASE_SERVICE_ROLE_KEY) {
    throw new Error('SUPABASE_URL e SUPABASE_SERVICE_ROLE_KEY são obrigatórios para --apply.');
  }

  const supabase = new SupabaseRestClient(process.env.SUPABASE_URL, process.env.SUPABASE_SERVICE_ROLE_KEY);
  const reference = await loadReferenceData(supabase);
  const map = existingMap;
  const userDirectory = buildUserDirectory(snapshot.users);
  const warnings = [];
  const context = {
    supabase,
    reference,
    map,
    userDirectory,
    warnings,
    legacy,
    options,
    anexosBucket: null,
    anexosBucketReady: false,
  };
  context.migrationUserId = await ensureMigrationUser(context);

  if (options.phases.has('users')) {
    await importUsers(context, snapshot.users);
  }

  if (options.phases.has('clients')) {
    for (const client of snapshot.clients) {
      await findOrCreateClient(context, client);
    }
  }

  if (options.phases.has('templates')) {
    await importTemplates(context, snapshot.templates);
  }

  if (options.phases.has('demandas')) {
    await importDemandasInBatches(legacy, context, pendingDemandSummaries, options);
  }

  if (options.phases.has('anexos')) {
    await backfillMappedDemandAttachments(context, legacy, options);
  }

  await saveMap(map);

  console.log('');
  console.log('Importação concluída.');
  if (warnings.length) {
    console.log('');
    console.log('Avisos:');
    for (const warning of warnings) console.log(`- ${warning}`);
  }
}

main().catch((error) => {
  console.error('');
  console.error(`[legacy-migrate] falhou: ${error instanceof Error ? error.message : String(error)}`);
  process.exit(1);
});
