#!/usr/bin/env node

import fs from 'node:fs';
import fsp from 'node:fs/promises';
import path from 'node:path';
import { createClient } from '@supabase/supabase-js';

const CACHE_DIR = path.join(process.cwd(), 'scripts', '.legacy-cache');
const DEFAULT_SNAPSHOT_FILE = path.join(CACHE_DIR, 'snapshot.json');
const DEFAULT_MAP_FILE = path.join(CACHE_DIR, 'map.json');
const REPORT_FILE = path.join(CACHE_DIR, 'template-clientes-sync-report.json');
const PAGE_SIZE = 1000;

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
    apply: false,
    snapshotFile: DEFAULT_SNAPSHOT_FILE,
    mapFile: DEFAULT_MAP_FILE,
  };
  for (const arg of argv) {
    if (arg === '--apply') options.apply = true;
    if (arg === '--dry-run') options.apply = false;
    if (arg.startsWith('--snapshot=')) options.snapshotFile = path.resolve(arg.slice('--snapshot='.length));
    if (arg.startsWith('--map=')) options.mapFile = path.resolve(arg.slice('--map='.length));
  }
  return options;
}

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, 'utf8'));
}

function normalizeText(value) {
  return String(value || '')
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase()
    .replace(/\s+/g, ' ')
    .trim();
}

function normalizeDocument(value) {
  const digits = String(value || '').replace(/\D/g, '');
  return digits || null;
}

function addToMapList(map, key, item) {
  if (!key) return;
  const list = map.get(key) ?? [];
  list.push(item);
  map.set(key, list);
}

function uniqueById(items) {
  return [...new Map(items.map((item) => [item.id, item])).values()];
}

function chooseUnique(candidates, legacyClient) {
  const unique = uniqueById(candidates);
  if (unique.length === 1) return unique[0];

  const legacyName = normalizeText(legacyClient.name);
  const exactName = unique.filter((item) => normalizeText(item.name) === legacyName);
  if (exactName.length === 1) return exactName[0];

  const exactFantasia = unique.filter((item) => normalizeText(item.nome_fantasia) === legacyName);
  if (exactFantasia.length === 1) return exactFantasia[0];

  const active = unique.filter((item) => item.active !== false);
  if (active.length === 1) return active[0];

  return null;
}

async function fetchAll(supabase, table, select, configure = (query) => query) {
  const rows = [];
  for (let from = 0; ; from += PAGE_SIZE) {
    const query = configure(supabase.from(table).select(select)).range(from, from + PAGE_SIZE - 1);
    const { data, error } = await query;
    if (error) throw new Error(`${table}: ${error.message}`);
    rows.push(...(data ?? []));
    if (!data || data.length < PAGE_SIZE) break;
  }
  return rows;
}

async function insertInBatches(supabase, rows) {
  let inserted = 0;
  for (let i = 0; i < rows.length; i += 500) {
    const batch = rows.slice(i, i + 500);
    const { error } = await supabase.from('template_cliente').insert(batch);
    if (error) throw new Error(`template_cliente insert: ${error.message}`);
    inserted += batch.length;
  }
  return inserted;
}

async function main() {
  loadDotEnv(path.join(process.cwd(), '.env'));
  const options = parseArgs(process.argv.slice(2));

  if (!process.env.SUPABASE_URL || !process.env.SUPABASE_SERVICE_ROLE_KEY) {
    throw new Error('SUPABASE_URL e SUPABASE_SERVICE_ROLE_KEY sao obrigatorios.');
  }

  const snapshot = readJson(options.snapshotFile);
  const map = readJson(options.mapFile);
  const legacyTemplates = snapshot.templates ?? [];
  const supabase = createClient(process.env.SUPABASE_URL, process.env.SUPABASE_SERVICE_ROLE_KEY, {
    auth: { persistSession: false },
  });

  const [templates, clientes, existingLinks] = await Promise.all([
    fetchAll(supabase, 'Template', 'id,name,assunto_template'),
    fetchAll(supabase, 'Cliente', 'id,name,active,documento,nome_fantasia,legacy_id,tipo_pessoa'),
    fetchAll(supabase, 'template_cliente', 'template_id,cliente_id'),
  ]);

  const templateById = new Map(templates.map((item) => [item.id, item]));
  const templatesByNameAndSubject = new Map();
  for (const template of templates) {
    addToMapList(
      templatesByNameAndSubject,
      `${normalizeText(template.name)}|${normalizeText(template.assunto_template)}`,
      template,
    );
  }

  const clientesByDocument = new Map();
  const clientesByName = new Map();
  const clientesByFantasia = new Map();
  for (const cliente of clientes) {
    addToMapList(clientesByDocument, normalizeDocument(cliente.documento), cliente);
    addToMapList(clientesByName, normalizeText(cliente.name), cliente);
    addToMapList(clientesByFantasia, normalizeText(cliente.nome_fantasia), cliente);
  }

  function resolveTemplate(legacyTemplate) {
    const mappedId = map.templates?.[String(legacyTemplate.legacyId)];
    if (mappedId && templateById.has(mappedId)) return templateById.get(mappedId);

    const matches = templatesByNameAndSubject.get(
      `${normalizeText(legacyTemplate.name)}|${normalizeText(legacyTemplate.assuntoTemplate)}`,
    ) ?? [];
    return matches.length === 1 ? matches[0] : null;
  }

  function resolveClient(legacyClient) {
    const byDocument = clientesByDocument.get(normalizeDocument(legacyClient.documento)) ?? [];
    const documentMatch = chooseUnique(byDocument, legacyClient);
    if (documentMatch) return { cliente: documentMatch, by: 'documento' };

    const byName = clientesByName.get(normalizeText(legacyClient.name)) ?? [];
    const nameMatch = chooseUnique(byName, legacyClient);
    if (nameMatch) return { cliente: nameMatch, by: 'name' };

    const byFantasia = clientesByFantasia.get(normalizeText(legacyClient.name)) ?? [];
    const fantasiaMatch = chooseUnique(byFantasia, legacyClient);
    if (fantasiaMatch) return { cliente: fantasiaMatch, by: 'nome_fantasia' };

    return null;
  }

  const desired = new Map();
  const unresolvedTemplates = [];
  const unresolvedClients = [];
  const matchedBy = { documento: 0, name: 0, nome_fantasia: 0 };
  let templatesWithLegacyClients = 0;

  for (const legacyTemplate of legacyTemplates) {
    const legacyClients = legacyTemplate.clientes ?? [];
    if (!legacyClients.length) continue;
    templatesWithLegacyClients += 1;

    const template = resolveTemplate(legacyTemplate);
    if (!template) {
      unresolvedTemplates.push({
        legacyId: legacyTemplate.legacyId,
        name: legacyTemplate.name,
        assuntoTemplate: legacyTemplate.assuntoTemplate ?? null,
      });
      continue;
    }

    for (const legacyClient of legacyClients) {
      const resolved = resolveClient(legacyClient);
      if (!resolved) {
        unresolvedClients.push({
          templateLegacyId: legacyTemplate.legacyId,
          templateName: legacyTemplate.name,
          clientName: legacyClient.name,
          documento: normalizeDocument(legacyClient.documento),
        });
        continue;
      }
      matchedBy[resolved.by] += 1;
      const key = `${template.id}|${resolved.cliente.id}`;
      desired.set(key, { template_id: template.id, cliente_id: resolved.cliente.id });
    }
  }

  const existingSet = new Set(existingLinks.map((item) => `${item.template_id}|${item.cliente_id}`));
  const toInsert = [...desired.values()].filter((item) => !existingSet.has(`${item.template_id}|${item.cliente_id}`));
  const inserted = options.apply ? await insertInBatches(supabase, toInsert) : 0;

  const report = {
    apply: options.apply,
    templatesRead: legacyTemplates.length,
    templatesWithLegacyClients,
    desiredLinks: desired.size,
    existingDesiredLinks: desired.size - toInsert.length,
    missingLinks: toInsert.length,
    inserted,
    matchedBy,
    unresolvedTemplatesCount: unresolvedTemplates.length,
    unresolvedClientsCount: unresolvedClients.length,
    unresolvedTemplates: unresolvedTemplates.slice(0, 30),
    unresolvedClients: unresolvedClients.slice(0, 50),
  };

  await fsp.mkdir(CACHE_DIR, { recursive: true });
  await fsp.writeFile(REPORT_FILE, `${JSON.stringify(report, null, 2)}\n`, 'utf8');
  console.log(JSON.stringify(report, null, 2));
  console.log(`report: ${REPORT_FILE}`);
}

main().catch((error) => {
  console.error(`[sync-template-clientes] falhou: ${error instanceof Error ? error.message : String(error)}`);
  process.exit(1);
});
