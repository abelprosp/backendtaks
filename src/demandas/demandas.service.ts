import { Injectable, BadRequestException, ForbiddenException, NotFoundException, ServiceUnavailableException } from '@nestjs/common';
import OpenAI from 'openai';
import { SupabaseService } from '../supabase/supabase.service';
import * as fs from 'fs';
import * as path from 'path';
import { v4 as uuidv4 } from 'uuid';
import { DemandaVisibilityService } from './demanda-visibility.service';
import { RecorrenciaService } from './recorrencia.service';
import { TemplatesService } from '../templates/templates.service';
import { MemoryTtlCache } from '../common/memory-ttl-cache';
import { CreateDemandaDto } from './dto/create-demanda.dto';
import { UpdateDemandaDto } from './dto/update-demanda.dto';
import { ListDemandasFiltersDto } from './dto/list-demandas-filters.dto';
import { CreateDemandaFromTemplateDto } from '../templates/dto/create-demanda-from-template.dto';
import type { DemandaStatus } from '../types/enums';
import { IaContextService } from '../ia-context/ia-context.service';

function computeTempoHoras(from: string | Date | null, to: string | Date | null): number | null {
  if (!from || !to) return null;
  const a = new Date(from).getTime();
  const b = new Date(to).getTime();
  return Math.round((b - a) / (1000 * 60 * 60) * 10) / 10;
}

/** Garante que datas sejam sempre ISO string ou null para o frontend não quebrar. */
function toDateISO(value: unknown): string | null {
  if (value == null) return null;
  if (typeof value === 'string') {
    const d = new Date(value);
    return Number.isNaN(d.getTime()) ? null : d.toISOString();
  }
  if (value instanceof Date) return Number.isNaN(value.getTime()) ? null : value.toISOString();
  return null;
}

function getTodayInSaoPaulo(): string {
  const formatter = new Intl.DateTimeFormat('en-CA', {
    timeZone: 'America/Sao_Paulo',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  });

  const parts = formatter.formatToParts(new Date());
  const year = parts.find((part) => part.type === 'year')?.value ?? '';
  const month = parts.find((part) => part.type === 'month')?.value ?? '';
  const day = parts.find((part) => part.type === 'day')?.value ?? '';
  return year && month && day ? `${year}-${month}-${day}` : new Date().toISOString().slice(0, 10);
}

function mapDemandaList(row: any, criador?: any, responsaveis?: any[], setores?: any[], clientes?: any[]) {
  if (!row) return null;
  const resolvidoEm = toDateISO(row.resolvido_em);
  const ultimaObservacaoEm = toDateISO(row.ultima_observacao_em);
  const createdAt = toDateISO(row.created_at) ?? undefined;
  const now = new Date().toISOString();
  return {
    id: row.id,
    protocolo: row.protocolo,
    assunto: row.assunto,
    prioridade: row.prioridade,
    prazo: row.prazo != null && row.prazo !== '' ? (toDateISO(row.prazo) ?? (typeof row.prazo === 'string' ? row.prazo : null)) : null,
    status: row.status,
    criadorId: row.criador_id,
    observacoesGerais: row.observacoes_gerais,
    isRecorrente: row.is_recorrente,
    demandaOrigemId: row.demanda_origem_id,
    createdAt: createdAt ?? null,
    updatedAt: toDateISO(row.updated_at) ?? row.updated_at,
    resolvidoEm: resolvidoEm ?? undefined,
    ultimaObservacaoEm: ultimaObservacaoEm ?? undefined,
    tempoResolucaoHoras: resolvidoEm && createdAt ? computeTempoHoras(createdAt, resolvidoEm) : null,
    tempoDesdeUltimaObservacaoHoras: ultimaObservacaoEm ? computeTempoHoras(ultimaObservacaoEm, now) : null,
    criador: criador ? { id: criador.id, name: criador.name, email: criador.email } : undefined,
    responsaveis: responsaveis ?? [],
    setores: setores ?? [],
    clientes: clientes ?? [],
  };
}

type DemandaRelationsBatch = {
  criadorByDemanda: Map<string, any | null>;
  responsaveisByDemanda: Map<string, any[]>;
  setoresByDemanda: Map<string, any[]>;
  clientesByDemanda: Map<string, any[]>;
  subtarefasByDemanda: Map<string, any[]>;
  observacoesByDemanda: Map<string, any[]>;
  anexosByDemanda: Map<string, any[]>;
  recorrenciaByDemanda: Map<string, any | null>;
};

type PesquisaGeralMode = 'all' | 'observacoes_gerais_only' | 'status_only';

type EvidenciaCampoChave =
  | 'protocolo'
  | 'assunto'
  | 'status'
  | 'prioridade'
  | 'prazo'
  | 'dataCriacao'
  | 'resolvidoEm'
  | 'ultimaObservacaoEm'
  | 'observacoesGerais'
  | 'criador'
  | 'responsaveis'
  | 'setores'
  | 'clientes'
  | 'subtarefas'
  | 'observacoes'
  | 'recorrencia';

type EvidenciaCampo = {
  key: EvidenciaCampoChave;
  label: string;
  value: string;
};

type EvidenciaMatch = {
  key: EvidenciaCampoChave;
  label: string;
  snippet: string;
};

type EvidenciaDemanda = {
  demandaId: string;
  protocolo: string;
  assunto: string;
  matchedFields: EvidenciaMatch[];
};

type PesquisaGeralResult = {
  matchedIds: string[];
  evidenciasByDemanda: Map<string, EvidenciaDemanda>;
  contagemCampos: Map<EvidenciaCampoChave, { label: string; count: number }>;
  scoreByDemanda: Map<string, number>;
};

type SistemaModulo = 'demandas' | 'setores' | 'clientes' | 'templates' | 'usuarios' | 'paginas';

type GlobalModuleCount = {
  module: SistemaModulo;
  label: string;
  count: number;
};

type GlobalMatchItem = {
  module: SistemaModulo;
  moduleLabel: string;
  title: string;
  snippet: string;
  route: string;
};

type GlobalEvidenceResult = {
  moduleCounts: GlobalModuleCount[];
  globalMatches: GlobalMatchItem[];
};

type IaGlobalCatalogRow = {
  module: Exclude<SistemaModulo, 'demandas' | 'paginas'>;
  title: string;
  snippet: string;
  searchable: string;
  route: string;
};

type IaDemandaDatasetRow = {
  demandaId: string;
  protocolo: string;
  assunto: string;
  status: string;
  prioridade: boolean;
  observacoesGerais: string;
  prazo: string | null;
  createdAt: string | null;
  resolvidoEm: string | null;
  ultimaObservacaoEm: string | null;
  isRecorrente: boolean;
  criador: string;
  responsaveis: string;
  setores: string;
  clientes: string;
  subtarefas: string;
  observacoes: string;
  recorrencia: string;
};

type IaSearchScope =
  | 'all'
  | 'demandas'
  | 'setores'
  | 'clientes'
  | 'templates'
  | 'usuarios'
  | 'paginas'
  | 'observacoes_gerais'
  | 'status';

type IaSearchContext = {
  previousQuery?: string;
  previousScope?: string;
  previousSearchTerm?: string;
  previousFilters?: Record<string, unknown>;
};

@Injectable()
export class DemandasService {
  private readonly iaReferenceDataCache = new MemoryTtlCache<
    string,
    { setores: any[]; clientes: any[]; users: any[] }
  >(60_000);
  private anexosBucketReady = false;

  constructor(
    private supabase: SupabaseService,
    private visibility: DemandaVisibilityService,
    private recorrencia: RecorrenciaService,
    private templatesService: TemplatesService,
    private iaContextService: IaContextService,
  ) {}

  private async gerarProtocolo(): Promise<string> {
    const sb = this.supabase.getClient();
    const year = new Date().getFullYear();
    const start = `${year}-01-01`;
    const end = `${year + 1}-01-01`;
    const { count } = await sb.from('Demanda').select('*', { count: 'exact', head: true }).gte('created_at', start).lt('created_at', end);
    return `LUX-${year}-${String((count ?? 0) + 1).padStart(5, '0')}`;
  }

  private getAnexosBucket(): string {
    return process.env.SUPABASE_STORAGE_BUCKET?.trim() || 'demandas-anexos';
  }

  private buildSupabaseStoragePath(bucket: string, objectPath: string): string {
    return `supabase://${bucket}/${objectPath}`;
  }

  private parseAnexoStoragePath(storagePath: string | null | undefined):
    | { mode: 'supabase'; bucket: string; objectPath: string }
    | { mode: 'local'; objectPath: string } {
    const raw = String(storagePath ?? '').trim();
    if (raw.startsWith('supabase://')) {
      const withoutProtocol = raw.slice('supabase://'.length);
      const slashIndex = withoutProtocol.indexOf('/');
      if (slashIndex > 0) {
        return {
          mode: 'supabase',
          bucket: withoutProtocol.slice(0, slashIndex),
          objectPath: withoutProtocol.slice(slashIndex + 1),
        };
      }
    }
    return { mode: 'local', objectPath: raw };
  }

  private async ensureAnexosBucket(): Promise<string> {
    const bucket = this.getAnexosBucket();
    if (this.anexosBucketReady) return bucket;
    const storage = this.supabase.getClient().storage;
    const { data, error } = await storage.listBuckets();
    if (error) {
      throw new ServiceUnavailableException('Erro ao validar o armazenamento de anexos.');
    }
    const bucketExists = (data ?? []).some((item) => item.name === bucket);
    if (!bucketExists) {
      const { error: createError } = await storage.createBucket(bucket, { public: false });
      if (createError && !/already exists/i.test(createError.message)) {
        throw new ServiceUnavailableException('Erro ao preparar o bucket de anexos.');
      }
    }
    this.anexosBucketReady = true;
    return bucket;
  }

  private async loadDemandaRelations(demandaId: string, detail = false) {
    const sb = this.supabase.getClient();
    const { data: demanda } = await sb.from('Demanda').select('id, criador_id').eq('id', demandaId).single();
    if (!demanda) {
      return {
        criador: null,
        responsaveis: [],
        setores: [],
        clientes: [],
        subtarefas: [],
        observacoes: [],
        anexos: [],
        recorrenciaConfig: null,
      };
    }
    const rel = await this.loadDemandaRelationsBatch([demanda], detail);
    return {
      criador: rel.criadorByDemanda.get(demandaId) ?? null,
      responsaveis: rel.responsaveisByDemanda.get(demandaId) ?? [],
      setores: rel.setoresByDemanda.get(demandaId) ?? [],
      clientes: rel.clientesByDemanda.get(demandaId) ?? [],
      subtarefas: detail ? rel.subtarefasByDemanda.get(demandaId) ?? [] : [],
      observacoes: detail ? rel.observacoesByDemanda.get(demandaId) ?? [] : [],
      anexos: detail ? rel.anexosByDemanda.get(demandaId) ?? [] : [],
      recorrenciaConfig: detail ? rel.recorrenciaByDemanda.get(demandaId) ?? null : null,
    };
  }

  private async loadDemandaRelationsBatch(rows: any[], detail = false): Promise<DemandaRelationsBatch> {
    const demandaIds = [...new Set((rows ?? []).map((row: any) => String(row?.id ?? '')).filter(Boolean))];
    const empty: DemandaRelationsBatch = {
      criadorByDemanda: new Map(),
      responsaveisByDemanda: new Map(),
      setoresByDemanda: new Map(),
      clientesByDemanda: new Map(),
      subtarefasByDemanda: new Map(),
      observacoesByDemanda: new Map(),
      anexosByDemanda: new Map(),
      recorrenciaByDemanda: new Map(),
    };
    if (!demandaIds.length) return empty;

    const rpcBatch = detail
      ? await this.loadDemandaRelationsBatchFromDetailRpc(demandaIds)
      : await this.loadDemandaRelationsBatchFromListRpc(demandaIds);
    if (rpcBatch) return rpcBatch;

    const sb = this.supabase.getClient();
    const [responsaveisRes, setoresRes, clientesRes, subtarefasRes, observacoesRes, anexosRes, recorrenciaRes] = await Promise.all([
      sb.from('demanda_responsavel').select('demanda_id, user_id, is_principal').in('demanda_id', demandaIds),
      sb.from('demanda_setor').select('demanda_id, setor_id').in('demanda_id', demandaIds),
      sb.from('demanda_cliente').select('demanda_id, cliente_id').in('demanda_id', demandaIds),
      detail
        ? sb
            .from('subtarefa')
            .select('id, demanda_id, titulo, concluida, ordem, responsavel_user_id')
            .in('demanda_id', demandaIds)
            .order('ordem', { ascending: true })
            .order('id', { ascending: true })
        : Promise.resolve({ data: [] as any[] }),
      detail
        ? sb
            .from('observacao')
            .select('id, demanda_id, user_id, texto, created_at')
            .in('demanda_id', demandaIds)
            .order('created_at', { ascending: false })
        : Promise.resolve({ data: [] as any[] }),
      detail ? sb.from('anexo').select('*').in('demanda_id', demandaIds) : Promise.resolve({ data: [] as any[] }),
      detail ? sb.from('recorrencia_config').select('*').in('demanda_id', demandaIds) : Promise.resolve({ data: [] as any[] }),
    ]);

    const creatorIds = [...new Set(rows.map((row: any) => row?.criador_id).filter(Boolean))];
    const responsavelIds = [...new Set((responsaveisRes.data ?? []).map((item: any) => item?.user_id).filter(Boolean))];
    const subtarefaRespIds = detail
      ? [...new Set((subtarefasRes.data ?? []).map((item: any) => item?.responsavel_user_id).filter(Boolean))]
      : [];
    const observacaoUserIds = detail ? [...new Set((observacoesRes.data ?? []).map((item: any) => item?.user_id).filter(Boolean))] : [];
    const setorIds = [...new Set((setoresRes.data ?? []).map((item: any) => item?.setor_id).filter(Boolean))];
    const clienteIds = [...new Set((clientesRes.data ?? []).map((item: any) => item?.cliente_id).filter(Boolean))];

    const [usersRes, setoresDetalhesRes, clientesDetalhesRes] = await Promise.all([
      [...new Set([...creatorIds, ...responsavelIds, ...subtarefaRespIds, ...observacaoUserIds])].length
        ? sb
            .from('User')
            .select('id, name, email')
            .in('id', [...new Set([...creatorIds, ...responsavelIds, ...subtarefaRespIds, ...observacaoUserIds])])
        : Promise.resolve({ data: [] as any[] }),
      setorIds.length ? sb.from('Setor').select('id, name, slug').in('id', setorIds) : Promise.resolve({ data: [] as any[] }),
      clienteIds.length ? sb.from('Cliente').select('id, name').in('id', clienteIds) : Promise.resolve({ data: [] as any[] }),
    ]);

    const userMap = new Map((usersRes.data ?? []).map((user: any) => [String(user?.id ?? ''), user]));
    const setorMap = new Map((setoresDetalhesRes.data ?? []).map((setor: any) => [String(setor?.id ?? ''), setor]));
    const clienteMap = new Map((clientesDetalhesRes.data ?? []).map((cliente: any) => [String(cliente?.id ?? ''), cliente]));

    for (const row of rows) {
      const demandaId = String(row?.id ?? '');
      empty.criadorByDemanda.set(demandaId, row?.criador_id ? userMap.get(String(row.criador_id)) ?? null : null);
    }

    for (const item of responsaveisRes.data ?? []) {
      const demandaId = String(item?.demanda_id ?? '');
      if (!demandaId) continue;
      const list = empty.responsaveisByDemanda.get(demandaId) ?? [];
      list.push({
        userId: item?.user_id,
        isPrincipal: !!item?.is_principal,
        user: item?.user_id ? userMap.get(String(item.user_id)) ?? null : null,
      });
      empty.responsaveisByDemanda.set(demandaId, list);
    }

    for (const item of setoresRes.data ?? []) {
      const demandaId = String(item?.demanda_id ?? '');
      const setorId = String(item?.setor_id ?? '');
      if (!demandaId || !setorId) continue;
      const setor = setorMap.get(setorId);
      if (!setor) continue;
      const list = empty.setoresByDemanda.get(demandaId) ?? [];
      list.push({ setor });
      empty.setoresByDemanda.set(demandaId, list);
    }

    for (const item of clientesRes.data ?? []) {
      const demandaId = String(item?.demanda_id ?? '');
      const clienteId = String(item?.cliente_id ?? '');
      if (!demandaId || !clienteId) continue;
      const cliente = clienteMap.get(clienteId);
      if (!cliente) continue;
      const list = empty.clientesByDemanda.get(demandaId) ?? [];
      list.push({ cliente });
      empty.clientesByDemanda.set(demandaId, list);
    }

    if (detail) {
      for (const item of subtarefasRes.data ?? []) {
        const demandaId = String(item?.demanda_id ?? '');
        if (!demandaId) continue;
        const list = empty.subtarefasByDemanda.get(demandaId) ?? [];
        list.push({
          id: item?.id,
          titulo: item?.titulo,
          concluida: !!item?.concluida,
          ordem: item?.ordem ?? 0,
          responsavelUserId: item?.responsavel_user_id ?? null,
          responsavel: item?.responsavel_user_id ? userMap.get(String(item.responsavel_user_id)) ?? null : null,
        });
        empty.subtarefasByDemanda.set(demandaId, list);
      }

      for (const item of observacoesRes.data ?? []) {
        const demandaId = String(item?.demanda_id ?? '');
        if (!demandaId) continue;
        const list = empty.observacoesByDemanda.get(demandaId) ?? [];
        const user = item?.user_id ? userMap.get(String(item.user_id)) : null;
        list.push({
          id: item?.id,
          texto: item?.texto ?? '',
          createdAt: toDateISO(item?.created_at) ?? undefined,
          user: user ? { id: user.id, name: user.name } : undefined,
        });
        empty.observacoesByDemanda.set(demandaId, list);
      }

      for (const item of anexosRes.data ?? []) {
        const demandaId = String(item?.demanda_id ?? '');
        if (!demandaId) continue;
        const list = empty.anexosByDemanda.get(demandaId) ?? [];
        list.push(item);
        empty.anexosByDemanda.set(demandaId, list);
      }

      for (const item of recorrenciaRes.data ?? []) {
        const demandaId = String(item?.demanda_id ?? '');
        if (!demandaId) continue;
        empty.recorrenciaByDemanda.set(demandaId, item ?? null);
      }
    }

    return empty;
  }

  private parseRpcJsonArray(value: unknown): any[] {
    return Array.isArray(value) ? value : [];
  }

  private async loadDemandaRelationsBatchFromListRpc(demandaIds: string[]): Promise<DemandaRelationsBatch | null> {
    const { data, error } = await this.supabase.getClient().rpc('rpc_hydrate_demandas_list', { p_ids: demandaIds });
    if (error || !Array.isArray(data)) return null;

    const batch: DemandaRelationsBatch = {
      criadorByDemanda: new Map(),
      responsaveisByDemanda: new Map(),
      setoresByDemanda: new Map(),
      clientesByDemanda: new Map(),
      subtarefasByDemanda: new Map(),
      observacoesByDemanda: new Map(),
      anexosByDemanda: new Map(),
      recorrenciaByDemanda: new Map(),
    };

    for (const row of data as any[]) {
      const demandaId = String(row?.demanda_id ?? '');
      if (!demandaId) continue;
      batch.criadorByDemanda.set(demandaId, row?.criador ?? null);
      batch.responsaveisByDemanda.set(demandaId, this.parseRpcJsonArray(row?.responsaveis));
      batch.setoresByDemanda.set(demandaId, this.parseRpcJsonArray(row?.setores));
      batch.clientesByDemanda.set(demandaId, this.parseRpcJsonArray(row?.clientes));
    }

    return batch;
  }

  private async loadDemandaRelationsBatchFromDetailRpc(demandaIds: string[]): Promise<DemandaRelationsBatch | null> {
    if (demandaIds.length !== 1) return null;
    const demandaId = demandaIds[0];
    const { data, error } = await this.supabase.getClient().rpc('rpc_demanda_detail', { p_demanda_id: demandaId });
    if (error || !Array.isArray(data) || !data.length) return null;

    const row = data[0] as any;
    const batch: DemandaRelationsBatch = {
      criadorByDemanda: new Map([[demandaId, row?.criador ?? null]]),
      responsaveisByDemanda: new Map([[demandaId, this.parseRpcJsonArray(row?.responsaveis)]]),
      setoresByDemanda: new Map([[demandaId, this.parseRpcJsonArray(row?.setores)]]),
      clientesByDemanda: new Map([[demandaId, this.parseRpcJsonArray(row?.clientes)]]),
      subtarefasByDemanda: new Map([[demandaId, this.parseRpcJsonArray(row?.subtarefas)]]),
      observacoesByDemanda: new Map([[demandaId, this.parseRpcJsonArray(row?.observacoes)]]),
      anexosByDemanda: new Map([[demandaId, this.parseRpcJsonArray(row?.anexos)]]),
      recorrenciaByDemanda: new Map([[demandaId, row?.recorrencia_config ?? null]]),
    };

    return batch;
  }

  private parseRpcStringArray(value: unknown): string[] {
    return Array.isArray(value)
      ? value.map((item) => String(item ?? '')).filter(Boolean)
      : [];
  }

  private async loadIaReferenceData(): Promise<{ setores: any[]; clientes: any[]; users: any[] }> {
    return this.iaReferenceDataCache.getOrLoad('default', async () => {
      const sb = this.supabase.getClient();
      const [setoresRes, clientesRes, usersRes] = await Promise.all([
        sb.from('Setor').select('id, name').order('name'),
        sb.from('Cliente').select('id, name').eq('active', true).order('name'),
        sb.from('User').select('id, name').eq('active', true).order('name'),
      ]);
      return {
        setores: setoresRes.data ?? [],
        clientes: clientesRes.data ?? [],
        users: usersRes.data ?? [],
      };
    });
  }

  private async loadGlobalCatalogItemsViaRpc(): Promise<IaGlobalCatalogRow[] | null> {
    const { data, error } = await this.supabase.getClient().rpc('rpc_ia_global_catalog');
    if (error || !Array.isArray(data)) return null;
    return (data as any[])
      .map((row) => {
        const module = String(row?.module ?? '') as IaGlobalCatalogRow['module'];
        if (!['setores', 'clientes', 'templates', 'usuarios'].includes(module)) return null;
        return {
          module,
          title: String(row?.title ?? '').trim(),
          snippet: String(row?.snippet ?? '').trim(),
          searchable: String(row?.searchable ?? '').trim(),
          route: String(row?.route ?? '').trim() || '/demandas',
        } as IaGlobalCatalogRow;
      })
      .filter((row): row is IaGlobalCatalogRow => !!row && !!row.title);
  }

  private async loadGlobalCatalogItemsFallback(
    shouldProcessModule: (module: SistemaModulo) => boolean,
  ): Promise<IaGlobalCatalogRow[]> {
    const sb = this.supabase.getClient();
    const [setoresRes, clientesRes, templatesRes, usersRes, rolesRes, userRoleRes] = await Promise.all([
      shouldProcessModule('setores')
        ? sb.from('Setor').select('id, name, slug').order('name').limit(80)
        : Promise.resolve({ data: [] as any[] }),
      shouldProcessModule('clientes')
        ? sb.from('Cliente').select('id, name, active').order('name').limit(80)
        : Promise.resolve({ data: [] as any[] }),
      shouldProcessModule('templates')
        ? sb
            .from('Template')
            .select('id, name, descricao, assunto_template, observacoes_gerais_template, is_recorrente_default, recorrencia_tipo')
            .order('updated_at', { ascending: false })
            .limit(80)
        : Promise.resolve({ data: [] as any[] }),
      shouldProcessModule('usuarios')
        ? sb.from('User').select('id, name, email, active').order('name').limit(120)
        : Promise.resolve({ data: [] as any[] }),
      shouldProcessModule('usuarios')
        ? sb.from('Role').select('id, name, slug')
        : Promise.resolve({ data: [] as any[] }),
      shouldProcessModule('usuarios')
        ? sb.from('user_role').select('user_id, role_id')
        : Promise.resolve({ data: [] as any[] }),
    ]);

    const items: IaGlobalCatalogRow[] = [];
    for (const row of setoresRes.data ?? []) {
      items.push({
        module: 'setores',
        title: String(row?.name ?? 'Setor'),
        snippet: `Slug: ${String(row?.slug ?? '—')}`,
        searchable: `${String(row?.name ?? '')} ${String(row?.slug ?? '')}`.trim(),
        route: '/cadastros',
      });
    }
    for (const row of clientesRes.data ?? []) {
      items.push({
        module: 'clientes',
        title: String(row?.name ?? 'Cliente'),
        snippet: row?.active ? 'Cliente ativo' : 'Cliente inativo',
        searchable: `${String(row?.name ?? '')} ${row?.active ? 'ativo cliente' : 'inativo cliente'}`.trim(),
        route: '/cadastros',
      });
    }
    for (const row of templatesRes.data ?? []) {
      items.push({
        module: 'templates',
        title: String(row?.name ?? 'Template'),
        snippet: [
          row?.descricao ? String(row.descricao) : '',
          row?.assunto_template ? `Assunto: ${String(row.assunto_template)}` : '',
          row?.is_recorrente_default ? `Recorrente: ${String(row?.recorrencia_tipo ?? 'sim')}` : '',
        ]
          .filter(Boolean)
          .join(' | '),
        searchable: [
          String(row?.name ?? ''),
          String(row?.descricao ?? ''),
          String(row?.assunto_template ?? ''),
          String(row?.observacoes_gerais_template ?? ''),
          row?.is_recorrente_default ? `recorrente ${String(row?.recorrencia_tipo ?? '')}` : '',
        ]
          .filter(Boolean)
          .join(' '),
        route: '/templates',
      });
    }

    const roleById = new Map(
      (rolesRes.data ?? []).map((r: any) => [String(r?.id ?? ''), String(r?.name ?? r?.slug ?? '')]),
    );
    const rolesByUser = new Map<string, string[]>();
    for (const link of userRoleRes.data ?? []) {
      const userId = String((link as any)?.user_id ?? '');
      const roleName = roleById.get(String((link as any)?.role_id ?? ''));
      if (!userId || !roleName) continue;
      const prev = rolesByUser.get(userId) ?? [];
      prev.push(roleName);
      rolesByUser.set(userId, prev);
    }
    for (const row of usersRes.data ?? []) {
      const uid = String(row?.id ?? '');
      const roles = rolesByUser.get(uid) ?? [];
      items.push({
        module: 'usuarios',
        title: String(row?.name ?? row?.email ?? 'Usuário'),
        snippet: `${String(row?.email ?? '')}${roles.length ? ` | Perfis: ${roles.join(', ')}` : ''} | ${
          row?.active ? 'Ativo' : 'Inativo'
        }`,
        searchable: `${String(row?.name ?? '')} ${String(row?.email ?? '')} ${
          row?.active ? 'ativo' : 'inativo'
        } ${roles.join(' ')}`.trim(),
        route: '/cadastros',
      });
    }

    return items;
  }

  private async loadIaDemandaDatasetViaRpc(ids: string[]): Promise<IaDemandaDatasetRow[] | null> {
    const { data, error } = await this.supabase.getClient().rpc('rpc_ia_demanda_dataset', { p_ids: ids });
    if (error || !Array.isArray(data)) return null;
    return (data as any[]).map((row) => ({
      demandaId: String(row?.demanda_id ?? ''),
      protocolo: String(row?.protocolo ?? ''),
      assunto: String(row?.assunto ?? ''),
      status: String(row?.status ?? ''),
      prioridade: !!row?.prioridade,
      observacoesGerais: String(row?.observacoes_gerais ?? ''),
      prazo: row?.prazo ? String(row.prazo) : null,
      createdAt: row?.created_at ? String(row.created_at) : null,
      resolvidoEm: row?.resolvido_em ? String(row.resolvido_em) : null,
      ultimaObservacaoEm: row?.ultima_observacao_em ? String(row.ultima_observacao_em) : null,
      isRecorrente: !!row?.is_recorrente,
      criador: String(row?.criador ?? ''),
      responsaveis: String(row?.responsaveis ?? ''),
      setores: String(row?.setores ?? ''),
      clientes: String(row?.clientes ?? ''),
      subtarefas: String(row?.subtarefas ?? ''),
      observacoes: String(row?.observacoes ?? ''),
      recorrencia: String(row?.recorrencia ?? ''),
    }));
  }

  private async loadIaSystemContextViaRpc(userId: string): Promise<{
    totalDemandasVisiveis: number;
    porStatus: Record<string, number>;
    demandasRecentes: { protocolo: string; assunto: string; status: string }[];
    setores: string[];
    clientesAtivos: string[];
    templates: string[];
  } | null> {
    const { data, error } = await this.supabase.getClient().rpc('rpc_ia_system_context', {
      p_user_id: userId,
    });
    if (error || !Array.isArray(data) || !data.length) return null;

    const row = (data as any[])[0] ?? {};
    const rawPorStatus =
      row?.por_status && typeof row.por_status === 'object' && !Array.isArray(row.por_status)
        ? row.por_status
        : {};
    const porStatus = Object.fromEntries(
      Object.entries(rawPorStatus).map(([key, value]) => [key, Number(value ?? 0) || 0]),
    );

    return {
      totalDemandasVisiveis: Number(row?.total_demandas_visiveis ?? 0) || 0,
      porStatus,
      demandasRecentes: this.parseRpcJsonArray(row?.demandas_recentes)
        .map((item: any) => ({
          protocolo: String(item?.protocolo ?? '—'),
          assunto: String(item?.assunto ?? '—'),
          status: String(item?.status ?? '—'),
        }))
        .filter((item) => item.protocolo || item.assunto),
      setores: this.parseRpcStringArray(row?.setores),
      clientesAtivos: this.parseRpcStringArray(row?.clientes_ativos),
      templates: this.parseRpcStringArray(row?.templates),
    };
  }

  private async loadDashboardKpisViaRpc(): Promise<{
    totalDemandas: number;
    concluidas: number;
    emAberto: number;
    tempoMedioResolucaoHoras: number | null;
    demandasSemObservacaoRecente: number;
    tempoMedioDesdeUltimaObservacaoHoras: number | null;
    porStatus: Record<string, number>;
  } | null> {
    const { data, error } = await this.supabase.getClient().rpc('rpc_dashboard_kpis');
    if (error || !Array.isArray(data) || !data.length) return null;

    const row = (data as any[])[0] ?? {};
    const rawPorStatus =
      row?.por_status && typeof row.por_status === 'object' && !Array.isArray(row.por_status)
        ? row.por_status
        : {};
    const porStatus = Object.fromEntries(
      Object.entries(rawPorStatus).map(([key, value]) => [key, Number(value ?? 0) || 0]),
    );
    const parseNullableNumber = (value: unknown): number | null => {
      if (value == null || value === '') return null;
      const parsed = Number(value);
      return Number.isFinite(parsed) ? parsed : null;
    };

    return {
      totalDemandas: Number(row?.total_demandas ?? 0) || 0,
      concluidas: Number(row?.concluidas ?? 0) || 0,
      emAberto: Number(row?.em_aberto ?? 0) || 0,
      tempoMedioResolucaoHoras: parseNullableNumber(row?.tempo_medio_resolucao_horas),
      demandasSemObservacaoRecente: Number(row?.demandas_sem_observacao_recente ?? 0) || 0,
      tempoMedioDesdeUltimaObservacaoHoras: parseNullableNumber(
        row?.tempo_medio_desde_ultima_observacao_horas,
      ),
      porStatus,
    };
  }

  private getPagination(filters: ListDemandasFiltersDto): { page: number; pageSize: number; offset: number } {
    const page = Math.max(Number(filters.page ?? 1) || 1, 1);
    const requestedPageSize = Math.max(Number(filters.pageSize ?? 100) || 100, 1);
    const pageSize = Math.min(requestedPageSize, 10_000);
    return {
      page,
      pageSize,
      offset: (page - 1) * pageSize,
    };
  }

  private async listDemandasViaRpc(
    userId: string,
    filters: ListDemandasFiltersDto,
    ids?: string[] | null,
  ): Promise<{ data: any[]; total: number } | null> {
    const { pageSize, offset } = this.getPagination(filters);
    const { data, error } = await this.supabase.getClient().rpc('rpc_list_demandas_page', {
      p_user_id: userId,
      p_limit: pageSize,
      p_offset: offset,
      p_ids: ids?.length ? ids : null,
      p_cliente_id: filters.clienteId ?? null,
      p_assunto: filters.assunto ?? null,
      p_status: filters.status ?? null,
      p_tipo_recorrencia: filters.tipoRecorrencia ?? null,
      p_protocolo: filters.protocolo ?? null,
      p_prioridade: filters.prioridade ?? null,
      p_criador_id: filters.criadorId ?? null,
      p_responsavel_principal_id: filters.responsavelApenasPrincipal ? filters.responsavelPrincipalId ?? null : null,
      p_setor_ids: filters.setorIds?.length ? filters.setorIds : null,
      p_condicao_prazo: filters.condicaoPrazo ?? null,
      p_pesquisa_tarefa_ou_observacao: filters.pesquisarTarefaOuObservacao ?? null,
      p_pesquisa_geral: ids?.length ? null : filters.pesquisaGeral ?? null,
      p_data_criacao_de: filters.dataCriacaoDe ?? null,
      p_data_criacao_ate: filters.dataCriacaoAte ?? null,
      p_prazo_de: filters.prazoDe ?? null,
      p_prazo_ate: filters.prazoAte ?? null,
    });
    if (error || !Array.isArray(data)) return null;

    const rows = data as any[];
    return {
      total: Number(rows[0]?.total_count ?? 0) || 0,
      data: rows.map((row) =>
        mapDemandaList(
          row,
          row?.criador ?? undefined,
          this.parseRpcJsonArray(row?.responsaveis),
          this.parseRpcJsonArray(row?.setores),
          this.parseRpcJsonArray(row?.clientes),
        ),
      ),
    };
  }

  private async findOneViaProtectedRpc(userId: string, id: string): Promise<any | undefined | null> {
    const { data, error } = await this.supabase.getClient().rpc('rpc_demanda_detail_for_user', {
      p_user_id: userId,
      p_demanda_id: id,
    });
    if (error || !Array.isArray(data)) return null;
    if (!data.length) return undefined;

    const row = (data as any[])[0] ?? {};
    const rec = row?.recorrencia_config ?? null;
    return {
      ...mapDemandaList(
        row,
        row?.criador ?? undefined,
        this.parseRpcJsonArray(row?.responsaveis),
        this.parseRpcJsonArray(row?.setores),
        this.parseRpcJsonArray(row?.clientes),
      ),
      subtarefas: this.parseRpcJsonArray(row?.subtarefas),
      observacoes: this.parseRpcJsonArray(row?.observacoes),
      anexos: this.parseRpcJsonArray(row?.anexos),
      recorrenciaConfig: rec
        ? {
            dataBase:
              toDateISO(rec.data_base) ??
              (typeof rec.data_base === 'string' ? rec.data_base : null),
            tipo: rec.tipo ?? '',
            prazoReaberturaDias: rec.prazo_reabertura_dias ?? 0,
          }
        : null,
    };
  }

  private buildPesquisaGeralResultFromDataset(
    rows: IaDemandaDatasetRow[],
    termoRaw: string,
    mode: PesquisaGeralMode,
  ): PesquisaGeralResult {
    const formatDateBr = (value: unknown): string => {
      if (value == null || value === '') return '';
      const d = new Date(String(value));
      if (Number.isNaN(d.getTime())) return '';
      const dd = String(d.getDate()).padStart(2, '0');
      const mm = String(d.getMonth() + 1).padStart(2, '0');
      const yyyy = d.getFullYear();
      return `${dd}/${mm}/${yyyy}`;
    };

    const normalizedPhrase = this.normalizeIaText(termoRaw);
    const quotedTerms = [...termoRaw.matchAll(/"([^"]{2,})"/g)].map((m) => this.normalizeIaText(m[1]));
    const tokens = this.tokenizePesquisaGeral(termoRaw);
    const allowed = this.allowedEvidenceFieldKeys(mode);
    const contagemCampos = new Map<EvidenciaCampoChave, { label: string; count: number }>();
    const evidenciasByDemanda = new Map<string, EvidenciaDemanda>();
    const fieldWeights: Record<EvidenciaCampoChave, number> = {
      protocolo: 5,
      assunto: 4.5,
      status: 4,
      prioridade: 1.2,
      prazo: 1.5,
      dataCriacao: 1.4,
      resolvidoEm: 1.2,
      ultimaObservacaoEm: 1.2,
      observacoesGerais: 4.2,
      criador: 2.2,
      responsaveis: 2.2,
      setores: 3.2,
      clientes: 3.2,
      subtarefas: 2.6,
      observacoes: 3.6,
      recorrencia: 2.1,
    };
    const matchedRows: { id: string; score: number }[] = [];
    const scoreByDemanda = new Map<string, number>();

    for (const row of rows) {
      const id = row.demandaId;
      if (!id) continue;

      const prazoBr = formatDateBr(row.prazo);
      const criacaoBr = formatDateBr(row.createdAt);
      const resolvidoBr = formatDateBr(row.resolvidoEm);
      const ultimaObsBr = formatDateBr(row.ultimaObservacaoEm);

      const allFields: EvidenciaCampo[] = [
        { key: 'protocolo', label: 'Protocolo', value: row.protocolo },
        { key: 'assunto', label: 'Assunto', value: row.assunto },
        { key: 'status', label: 'Status', value: `${row.status} ${this.statusLabelPt(row.status)}`.trim() },
        { key: 'prioridade', label: 'Prioridade', value: row.prioridade ? 'prioridade alta sim' : 'prioridade nao' },
        { key: 'prazo', label: 'Prazo', value: `${row.prazo ?? ''} ${prazoBr}`.trim() },
        { key: 'dataCriacao', label: 'Data de criação', value: `${row.createdAt ?? ''} ${criacaoBr}`.trim() },
        { key: 'resolvidoEm', label: 'Resolução', value: `${row.resolvidoEm ?? ''} ${resolvidoBr}`.trim() },
        {
          key: 'ultimaObservacaoEm',
          label: 'Última atualização',
          value: `${row.ultimaObservacaoEm ?? ''} ${ultimaObsBr}`.trim(),
        },
        { key: 'observacoesGerais', label: 'Observações gerais', value: row.observacoesGerais },
        { key: 'criador', label: 'Criador', value: row.criador },
        { key: 'responsaveis', label: 'Responsáveis', value: row.responsaveis },
        { key: 'setores', label: 'Setores', value: row.setores },
        { key: 'clientes', label: 'Clientes', value: row.clientes },
        { key: 'subtarefas', label: 'Subtarefas', value: row.subtarefas },
        { key: 'observacoes', label: 'Observações', value: row.observacoes },
        { key: 'recorrencia', label: 'Recorrência', value: row.recorrencia },
      ];

      let score = 0;
      const matchedFields: EvidenciaMatch[] = [];
      for (const field of allFields) {
        if (allowed && !allowed.has(field.key)) continue;
        const evaluated = this.evaluateSearchableText(field.value, normalizedPhrase, tokens, quotedTerms);
        if (!evaluated.matched) continue;
        score += evaluated.score * (fieldWeights[field.key] ?? 1);
        const prev = contagemCampos.get(field.key) ?? { label: field.label, count: 0 };
        prev.count += 1;
        contagemCampos.set(field.key, prev);
        matchedFields.push({
          key: field.key,
          label: field.label,
          snippet: this.sanitizeEvidenceSnippet(field.value || `${field.label.toLowerCase()} encontrado`),
        });
      }

      if (!matchedFields.length) continue;
      matchedRows.push({ id, score });
      scoreByDemanda.set(id, score);
      evidenciasByDemanda.set(id, {
        demandaId: id,
        protocolo: row.protocolo || '—',
        assunto: row.assunto || '—',
        matchedFields: matchedFields.sort((a, b) => (fieldWeights[b.key] ?? 1) - (fieldWeights[a.key] ?? 1)),
      });
    }

    matchedRows.sort((a, b) => b.score - a.score);
    return {
      matchedIds: matchedRows.map((item) => item.id),
      evidenciasByDemanda,
      contagemCampos,
      scoreByDemanda,
    };
  }

  async create(userId: string, dto: CreateDemandaDto) {
    const sb = this.supabase.getClient();
    const protocolo = await this.gerarProtocolo();
    const status = (dto.status as DemandaStatus) || 'em_aberto';
    const { data: demanda, error } = await sb
      .from('Demanda')
      .insert({
        protocolo,
        assunto: dto.assunto,
        prioridade: dto.prioridade ?? false,
        prazo: dto.prazo ?? null,
        status,
        criador_id: userId,
        observacoes_gerais: dto.observacoesGerais ?? null,
        is_privada: dto.isPrivada ?? false,
        private_owner_user_id: dto.isPrivada ? userId : null,
        is_recorrente: dto.isRecorrente ?? false,
      })
      .select()
      .single();
    if (error) throw new Error(error.message);
    if (dto.setores?.length) await sb.from('demanda_setor').insert(dto.setores.map((setorId) => ({ demanda_id: demanda.id, setor_id: setorId })));
    if (dto.clienteIds?.length) await sb.from('demanda_cliente').insert(dto.clienteIds.map((clienteId) => ({ demanda_id: demanda.id, cliente_id: clienteId })));
    if (dto.responsaveis?.length) await sb.from('demanda_responsavel').insert(dto.responsaveis.map((r) => ({ demanda_id: demanda.id, user_id: r.userId, is_principal: r.isPrincipal ?? false })));
    if (dto.privateViewerIds?.length) {
      await sb.from('demanda_private_viewer').insert([...new Set(dto.privateViewerIds)].map((viewerId) => ({ demanda_id: demanda.id, user_id: viewerId })));
    }
    if (dto.subtarefas?.length) {
      await sb.from('subtarefa').insert(
        dto.subtarefas.map((t, i) => ({
          demanda_id: demanda.id,
          titulo: t.titulo,
          ordem: (t as any).ordem ?? i,
          responsavel_user_id: (t as any).responsavelUserId ?? null,
        })),
      );
    }
    if (dto.isRecorrente && dto.recorrencia) {
      await sb.from('recorrencia_config').insert({
        demanda_id: demanda.id,
        data_base: dto.recorrencia.dataBase,
        tipo: dto.recorrencia.tipo,
        prazo_reabertura_dias: dto.recorrencia.prazoReaberturaDias ?? 0,
      });
    }
    this.visibility.clearVisibleDemandaIdsCache();
    const rel = await this.loadDemandaRelations(demanda.id, false);
    return mapDemandaList(demanda, rel.criador, rel.responsaveis, rel.setores, rel.clientes);
  }

  async createFromTemplate(userId: string, templateId: string, dto: CreateDemandaFromTemplateDto) {
    const template = await this.templatesService.getForDemanda(templateId) as any;
    const protocolo = await this.gerarProtocolo();
    const prioridade = dto.prioridade ?? template.prioridadeDefault;
    const observacoesGerais = dto.observacoesGerais ?? template.observacoesGeraisTemplate ?? undefined;
    const recorrenciaDataBase = dto.recorrenciaDataBase ?? template.recorrenciaDataBaseDefault ?? undefined;
    const isRecorrente = !!recorrenciaDataBase && !!template.isRecorrenteDefault && !!template.recorrenciaTipo;
    const setorIds = (dto.setorIds?.length ? dto.setorIds : template.setores?.map((s: any) => s.setor?.id ?? s.setorId)?.filter(Boolean)) ?? [];
    const responsaveisDto = (dto.responsaveis?.length ? dto.responsaveis : template.responsaveis?.map((r: any) => ({ userId: r.userId ?? r.user?.id, isPrincipal: r.isPrincipal ?? false }))) ?? [];
    const subtarefasTemplate = (
      dto.subtarefas?.length
        ? dto.subtarefas
        : template.subtarefas?.map((t: any) => ({
            titulo: t.titulo ?? '',
            responsavelUserId: t.responsavelUserId ?? t.responsavel?.id ?? null,
          }))
    ) ?? [];

    const sb = this.supabase.getClient();
    const { data: demanda, error } = await sb
      .from('Demanda')
      .insert({
        protocolo,
        assunto: dto.assunto,
        prioridade,
        prazo: dto.prazo ?? null,
        status: 'em_aberto',
        criador_id: userId,
        observacoes_gerais: observacoesGerais ?? null,
        is_privada: dto.isPrivada ?? false,
        private_owner_user_id: dto.isPrivada ? userId : null,
        is_recorrente: isRecorrente,
      })
      .select()
      .single();
    if (error) throw new Error(error.message);

    if (setorIds.length) await sb.from('demanda_setor').insert(setorIds.map((setorId: string) => ({ demanda_id: demanda.id, setor_id: setorId })));
    if (dto.clienteIds?.length) await sb.from('demanda_cliente').insert(dto.clienteIds.map((clienteId) => ({ demanda_id: demanda.id, cliente_id: clienteId })));
    if (responsaveisDto.length) await sb.from('demanda_responsavel').insert(responsaveisDto.map((r: any) => ({ demanda_id: demanda.id, user_id: r.userId, is_principal: r.isPrincipal ?? false })));
    if (dto.privateViewerIds?.length) {
      await sb.from('demanda_private_viewer').insert([...new Set(dto.privateViewerIds)].map((viewerId) => ({ demanda_id: demanda.id, user_id: viewerId })));
    }
    if (subtarefasTemplate.length) {
      await sb.from('subtarefa').insert(
        subtarefasTemplate.map((t: any, i: number) => ({
          demanda_id: demanda.id,
          titulo: t.titulo,
          ordem: i,
          responsavel_user_id: t.responsavelUserId ?? null,
        })),
      );
    }
    if (isRecorrente && recorrenciaDataBase && template.recorrenciaTipo) {
      await sb.from('recorrencia_config').insert({
        demanda_id: demanda.id,
        data_base: recorrenciaDataBase,
        tipo: template.recorrenciaTipo,
        prazo_reabertura_dias: template.recorrenciaPrazoReaberturaDias ?? 0,
      });
    }
    this.visibility.clearVisibleDemandaIdsCache();
    const rel = await this.loadDemandaRelations(demanda.id, false);
    return mapDemandaList(demanda, rel.criador, rel.responsaveis, rel.setores, rel.clientes);
  }

  private statusLabelPt(status: string): string {
    const labels: Record<string, string> = {
      em_aberto: 'em aberto',
      em_andamento: 'em andamento',
      concluido: 'concluído',
      standby: 'standby',
      cancelado: 'cancelado',
    };
    return labels[status] ?? status;
  }

  private tokenizePesquisaGeral(value: string): string[] {
    const stopwords = new Set([
      'a', 'o', 'as', 'os', 'um', 'uma', 'de', 'do', 'da', 'dos', 'das',
      'no', 'na', 'nos', 'nas', 'em', 'por', 'para', 'com', 'sem',
      'que', 'quais', 'qual', 'onde', 'como', 'tem', 'tenho', 'quero',
      'me', 'minha', 'meu', 'minhas', 'meus', 'sobre', 'sistema',
      'campo', 'campos', 'dados', 'basicos', 'basico', 'procure', 'buscar',
      'demanda', 'demandas', 'apenas', 'somente', 'so', 'status',
      'observacao', 'observacoes', 'obs', 'geral', 'gerais', 'usuario', 'user',
      'qualquer', 'todo', 'todos', 'todas',
    ]);
    return this
      .normalizeIaText(value)
      .split(/[^a-z0-9]+/)
      .map((t) => t.trim())
      .filter((t) => t.length >= 2 && !stopwords.has(t));
  }

  private hasPesquisaTokenMatch(text: string, token: string): boolean {
    if (!token) return false;
    if (token.length <= 2) {
      return new RegExp(`\\b${this.escapeRegExp(token)}\\b`).test(text);
    }
    return text.includes(token);
  }

  private isGenericPesquisaGeralPhrase(value: string): boolean {
    const normalized = this.normalizeIaText(value);
    if (!normalized) return true;
    return (
      normalized === 'demanda' ||
      normalized === 'demandas' ||
      normalized === 'dados basicos' ||
      normalized === 'dados basico' ||
      normalized === 'campos basicos' ||
      normalized === 'campo basico' ||
      normalized === 'observacao' ||
      normalized === 'observacoes' ||
      normalized === 'observacoes gerais' ||
      normalized === 'obs'
    );
  }

  private extractDateTokens(query: string): string[] {
    return [...new Set(query.match(/\b\d{2}\/\d{2}\/\d{4}\b|\b\d{4}-\d{2}-\d{2}\b/g) ?? [])];
  }

  private normalizePesquisaGeralFromQuery(query: string, current?: string): string | undefined {
    const rawQuery = query.trim();
    const normalizedQuery = this.normalizeIaText(rawQuery);
    const quoted = rawQuery.match(/"([^"]{2,})"/)?.[1]?.trim();
    if (quoted) return quoted;

    const dateTokens = this.extractDateTokens(rawQuery);
    if (dateTokens.length) {
      if (!current?.trim()) return dateTokens[0];
      const currentTrimmed = current.trim();
      const currentNormalized = this.normalizeIaText(currentTrimmed);
      const hasDateInCurrent = dateTokens.some((d) => currentNormalized.includes(this.normalizeIaText(d)));
      const hasManyTokens = this.tokenizePesquisaGeral(currentTrimmed).length >= 3;
      if (hasDateInCurrent && hasManyTokens) return dateTokens[0];
    }

    if (current?.trim()) return current.trim();

    const mentionsFullTextFields =
      normalizedQuery.includes('observacao') ||
      normalizedQuery.includes('obs') ||
      normalizedQuery.includes('dados basicos') ||
      normalizedQuery.includes('campos basicos') ||
      normalizedQuery.includes('recorrente') ||
      normalizedQuery.includes('data base');

    if (mentionsFullTextFields) {
      if (dateTokens.length) return dateTokens[0];
      return rawQuery;
    }

    const freeTextTokens = this.tokenizePesquisaGeral(rawQuery);
    if (freeTextTokens.length) {
      if (freeTextTokens.length === 1) return freeTextTokens[0];
      return freeTextTokens.slice(0, 4).join(' ');
    }
    return undefined;
  }

  private queryMentionsPrioridade(query: string): boolean {
    const normalized = this.normalizeIaText(query);
    return (
      normalized.includes('prioridade') ||
      normalized.includes('urgencia') ||
      normalized.includes('urgente') ||
      normalized.includes('sem urgencia')
    );
  }

  private queryMentionsStatus(query: string): boolean {
    const normalized = this.normalizeIaText(query);
    return (
      normalized.includes('status') ||
      normalized.includes('andamento') ||
      normalized.includes('em aberto') ||
      normalized.includes('abert') ||
      normalized.includes('concluid') ||
      normalized.includes('finalizad') ||
      normalized.includes('standby') ||
      normalized.includes('stand by') ||
      normalized.includes('cancelad')
    );
  }

  private normalizeIaScope(scope?: string | null): IaSearchScope {
    const normalized = this.normalizeIaText(String(scope ?? 'all')).replace(/\s+/g, '_');
    if (normalized === 'demandas' || normalized === 'demanda') return 'demandas';
    if (normalized === 'setores' || normalized === 'setor') return 'setores';
    if (normalized === 'clientes' || normalized === 'cliente') return 'clientes';
    if (normalized === 'templates' || normalized === 'template') return 'templates';
    if (normalized === 'usuarios' || normalized === 'usuario' || normalized === 'users' || normalized === 'user') return 'usuarios';
    if (normalized === 'paginas' || normalized === 'pagina') return 'paginas';
    if (
      normalized === 'observacoes_gerais' ||
      normalized === 'observacoes_geral' ||
      normalized === 'observacao_geral' ||
      normalized === 'obs_gerais'
    ) return 'observacoes_gerais';
    if (normalized === 'status') return 'status';
    return 'all';
  }

  private parsePesquisaGeralMode(query: string, scope: IaSearchScope = 'all'): PesquisaGeralMode {
    if (scope === 'observacoes_gerais') return 'observacoes_gerais_only';
    if (scope === 'status') return 'status_only';
    const normalized = this.normalizeIaText(query);
    if (
      normalized.includes('qualquer campo') ||
      normalized.includes('todos os campos') ||
      normalized.includes('em qualquer campo')
    ) {
      return 'all';
    }
    if (/\b(apenas|somente|so)\b.*\bstatus\b/.test(normalized)) {
      return 'status_only';
    }
    if (
      /\b(apenas|somente|so)\b.*\b(obs|observacao|observacoes)\b.*\bgerais?\b/.test(normalized) ||
      normalized.includes('apenas observacao geral') ||
      normalized.includes('somente observacao geral')
    ) {
      return 'observacoes_gerais_only';
    }
    return 'all';
  }

  private pesquisaGeralModeLabel(mode: PesquisaGeralMode): string {
    if (mode === 'observacoes_gerais_only') return 'Apenas observações gerais';
    if (mode === 'status_only') return 'Somente status';
    return 'Qualquer campo';
  }

  private allowedEvidenceFieldKeys(mode: PesquisaGeralMode): Set<EvidenciaCampoChave> | null {
    if (mode === 'observacoes_gerais_only') return new Set<EvidenciaCampoChave>(['observacoesGerais']);
    if (mode === 'status_only') return new Set<EvidenciaCampoChave>(['status']);
    return null;
  }

  private globalModuleLabel(module: SistemaModulo): string {
    switch (module) {
      case 'demandas':
        return 'Demandas';
      case 'setores':
        return 'Setores';
      case 'clientes':
        return 'Clientes';
      case 'templates':
        return 'Templates';
      case 'usuarios':
        return 'Usuários';
      case 'paginas':
        return 'Páginas';
      default:
        return module;
    }
  }

  private parseGlobalModuleIntents(query: string, scope: IaSearchScope = 'all'): Set<SistemaModulo> {
    if (scope === 'demandas' || scope === 'observacoes_gerais' || scope === 'status') return new Set(['demandas']);
    if (scope === 'setores') return new Set(['setores']);
    if (scope === 'clientes') return new Set(['clientes']);
    if (scope === 'templates') return new Set(['templates']);
    if (scope === 'usuarios') return new Set(['usuarios']);
    if (scope === 'paginas') return new Set(['paginas']);

    const normalized = this.normalizeIaText(query);
    const out = new Set<SistemaModulo>();
    if (normalized.includes('demanda') || normalized.includes('protocolo') || normalized.includes('chamado')) out.add('demandas');
    if (normalized.includes('setor')) out.add('setores');
    if (normalized.includes('cliente')) out.add('clientes');
    if (normalized.includes('template') || normalized.includes('modelo')) out.add('templates');
    if (
      normalized.includes('usuario') ||
      normalized.includes('usuarios') ||
      normalized.includes('colaborador') ||
      normalized.includes('responsavel') ||
      normalized.includes('responsável') ||
      normalized.includes('master')
    ) out.add('usuarios');
    if (
      normalized.includes('pagina') ||
      normalized.includes('paginas') ||
      normalized.includes('rota') ||
      normalized.includes('menu') ||
      normalized.includes('onde')
    ) out.add('paginas');
    return out;
  }

  private isListingStyleQuery(query: string): boolean {
    const normalized = this.normalizeIaText(query);
    return (
      normalized.includes('quais') ||
      normalized.includes('listar') ||
      normalized.includes('lista') ||
      normalized.includes('mostra') ||
      normalized.includes('mostrar') ||
      normalized.includes('existem') ||
      normalized.includes('todos') ||
      normalized.includes('todas')
    );
  }

  private isFollowUpIaQuery(query: string): boolean {
    const normalized = this.normalizeIaText(query);
    return (
      normalized.startsWith('agora') ||
      normalized.startsWith('e ') ||
      normalized.includes('dessas') ||
      normalized.includes('desses') ||
      normalized.includes('dessas demandas') ||
      normalized.includes('desses resultados') ||
      normalized.includes('refina') ||
      normalized.includes('filtra') ||
      normalized.includes('mantem') ||
      normalized.includes('mantém')
    );
  }

  private shouldClearIaContext(query: string): boolean {
    const normalized = this.normalizeIaText(query);
    return (
      normalized.includes('do zero') ||
      normalized.includes('nova busca') ||
      normalized.includes('sem contexto') ||
      normalized.includes('ignora anterior') ||
      normalized.includes('resetar filtro')
    );
  }

  private sanitizeIaContextFilters(raw?: Record<string, unknown>): ListDemandasFiltersDto {
    const out: ListDemandasFiltersDto = {};
    if (!raw || typeof raw !== 'object') return out;

    const pickString = (key: keyof ListDemandasFiltersDto) => {
      const v = (raw as Record<string, unknown>)[String(key)];
      if (typeof v === 'string' && v.trim()) (out as any)[key] = v.trim();
    };

    pickString('clienteId');
    pickString('assunto');
    pickString('status');
    pickString('tipoRecorrencia');
    pickString('protocolo');
    pickString('criadorId');
    pickString('responsavelPrincipalId');
    pickString('condicaoPrazo');
    pickString('pesquisarTarefaOuObservacao');
    pickString('pesquisaGeral');
    pickString('dataCriacaoDe');
    pickString('dataCriacaoAte');
    pickString('prazoDe');
    pickString('prazoAte');

    const prioridade = (raw as Record<string, unknown>).prioridade;
    if (prioridade === true || prioridade === false) out.prioridade = prioridade;

    const setorIds = (raw as Record<string, unknown>).setorIds;
    if (Array.isArray(setorIds)) {
      const list = setorIds
        .map((x) => String(x || '').trim())
        .filter(Boolean)
        .slice(0, 30);
      if (list.length) out.setorIds = list;
    }

    return out;
  }

  private applyIaContextToFilters(
    query: string,
    current: ListDemandasFiltersDto,
    context?: IaSearchContext,
  ): ListDemandasFiltersDto {
    if (!context?.previousFilters || !this.isFollowUpIaQuery(query) || this.shouldClearIaContext(query)) {
      return current;
    }

    const previous = this.sanitizeIaContextFilters(context.previousFilters);
    if (!Object.keys(previous).length) return current;

    const merged: ListDemandasFiltersDto = { ...previous, ...current };
    if (current.setorIds) merged.setorIds = current.setorIds;
    if (current.pesquisaGeral) merged.pesquisaGeral = current.pesquisaGeral;
    if (current.pesquisarTarefaOuObservacao) merged.pesquisarTarefaOuObservacao = current.pesquisarTarefaOuObservacao;
    if (current.dataCriacaoDe || current.dataCriacaoAte) {
      if (current.dataCriacaoDe) merged.dataCriacaoDe = current.dataCriacaoDe;
      if (current.dataCriacaoAte) merged.dataCriacaoAte = current.dataCriacaoAte;
    }
    if (current.prazoDe || current.prazoAte) {
      if (current.prazoDe) merged.prazoDe = current.prazoDe;
      if (current.prazoAte) merged.prazoAte = current.prazoAte;
    }
    return merged;
  }

  private evaluateSearchableText(
    text: string,
    normalizedPhrase: string,
    tokens: string[],
    quotedTerms: string[],
  ): { matched: boolean; score: number } {
    const textNorm = this.normalizeIaText(text);
    if (!textNorm) return { matched: false, score: 0 };

    if (quotedTerms.length) {
      const hit = quotedTerms.some((q) => {
        if (!q) return false;
        if (q.includes(' ')) return textNorm.includes(q);
        return this.hasPesquisaTokenMatch(textNorm, q);
      });
      return { matched: hit, score: hit ? 10 : 0 };
    }

    if (tokens.length) {
      const hits = tokens.filter((t) => this.hasPesquisaTokenMatch(textNorm, t)).length;
      const minHits = tokens.length <= 2 ? 1 : Math.ceil(tokens.length * 0.5);
      return { matched: hits >= minHits, score: hits };
    }

    if (normalizedPhrase.length >= 2 && !this.isGenericPesquisaGeralPhrase(normalizedPhrase)) {
      const hit = textNorm.includes(normalizedPhrase);
      return { matched: hit, score: hit ? 1 : 0 };
    }
    return { matched: false, score: 0 };
  }

  private async buildGlobalSystemEvidence(
    query: string,
    mode: PesquisaGeralMode,
    scope: IaSearchScope,
    searchTerm: string | null,
    demandaCount: number,
    demandaGlobalMatches: GlobalMatchItem[],
  ): Promise<GlobalEvidenceResult> {
    const moduleCountsMap = new Map<SistemaModulo, GlobalModuleCount>();
    const globalMatches: GlobalMatchItem[] = [];
    const seen = new Set<string>();
    const addGlobal = (item: GlobalMatchItem) => {
      const key = `${item.module}:${item.title}:${item.route}`;
      if (seen.has(key)) return;
      seen.add(key);
      globalMatches.push(item);
    };

    if (demandaCount > 0) {
      moduleCountsMap.set('demandas', {
        module: 'demandas',
        label: this.globalModuleLabel('demandas'),
        count: demandaCount,
      });
    }
    demandaGlobalMatches.forEach((m) => addGlobal(m));

    if (mode !== 'all') {
      return {
        moduleCounts: [...moduleCountsMap.values()],
        globalMatches: globalMatches.slice(0, 8),
      };
    }

    const intents = this.parseGlobalModuleIntents(query, scope);
    const listingQuery = this.isListingStyleQuery(query);
    const effectiveTerm = searchTerm?.trim() || this.normalizePesquisaGeralFromQuery(query, undefined) || query.trim();
    const normalizedPhrase = this.normalizeIaText(effectiveTerm);
    const tokens = this.tokenizePesquisaGeral(effectiveTerm);
    const quotedTerms = [...effectiveTerm.matchAll(/"([^"]{2,})"/g)].map((m) => this.normalizeIaText(m[1]));
    const hasSearchTerm =
      quotedTerms.length > 0 ||
      tokens.length > 0 ||
      (normalizedPhrase.length >= 2 && !this.isGenericPesquisaGeralPhrase(normalizedPhrase));

    const shouldProcessModule = (module: SistemaModulo): boolean => intents.size === 0 || intents.has(module);
    const collectModule = (module: Exclude<SistemaModulo, 'demandas' | 'paginas'>, rows: IaGlobalCatalogRow[]) => {
      if (!rows.length) return;
      const candidates = rows.map((row) => {
        const evaluated = this.evaluateSearchableText(row.searchable, normalizedPhrase, tokens, quotedTerms);
        return {
          title: row.title,
          snippet: row.snippet,
          route: row.route,
          score: evaluated.score,
          matched: evaluated.matched,
        };
      });

      let selected = candidates.filter((c) => c.matched);
      if (!selected.length && shouldProcessModule(module) && listingQuery) {
        selected = candidates.slice(0, 5).map((c) => ({ ...c, matched: true, score: 0 }));
      } else if (!hasSearchTerm && shouldProcessModule(module) && (intents.has(module) || intents.size === 0)) {
        selected = candidates.slice(0, 5).map((c) => ({ ...c, matched: true, score: 0 }));
      }

      if (!selected.length) return;

      selected.sort((a, b) => b.score - a.score);
      const capped = selected.slice(0, 5);
      moduleCountsMap.set(module, {
        module,
        label: this.globalModuleLabel(module),
        count: selected.length,
      });
      capped.forEach((item) =>
        addGlobal({
          module,
          moduleLabel: this.globalModuleLabel(module),
          title: item.title,
          snippet: this.sanitizeEvidenceSnippet(item.snippet),
          route: item.route,
        }),
      );
    };

    const catalogRows =
      (await this.loadGlobalCatalogItemsViaRpc()) ?? (await this.loadGlobalCatalogItemsFallback(shouldProcessModule));

    collectModule('setores', catalogRows.filter((row) => row.module === 'setores'));
    collectModule('clientes', catalogRows.filter((row) => row.module === 'clientes'));
    collectModule('templates', catalogRows.filter((row) => row.module === 'templates'));
    collectModule('usuarios', catalogRows.filter((row) => row.module === 'usuarios'));

    const paginas = shouldProcessModule('paginas') ? this.getPagesContextForIa() : [];
    if (paginas.length) {
      const pagesRows = paginas.map((p) => ({
        title: p.label,
        snippet: `${p.descricao} (${p.url})`,
        searchable: `${p.label} ${p.descricao} ${p.url}`,
      }));
      const matches = pagesRows
        .map((p) => ({ ...p, eval: this.evaluateSearchableText(p.searchable, normalizedPhrase, tokens, quotedTerms) }))
        .filter((p) => p.eval.matched);
      const selected =
        matches.length > 0
          ? matches.sort((a, b) => b.eval.score - a.eval.score).slice(0, 5)
          : listingQuery || !hasSearchTerm || intents.has('paginas')
          ? pagesRows.slice(0, 5).map((p) => ({ ...p, eval: { matched: true, score: 0 } }))
          : [];

      if (selected.length) {
        moduleCountsMap.set('paginas', {
          module: 'paginas',
          label: this.globalModuleLabel('paginas'),
          count: selected.length,
        });
        selected.forEach((p) =>
          addGlobal({
            module: 'paginas',
            moduleLabel: this.globalModuleLabel('paginas'),
            title: p.title,
            snippet: this.sanitizeEvidenceSnippet(p.snippet),
            route: paginas.find((x) => x.label === p.title)?.url ?? '/demandas',
          }),
        );
      }
    }

    const moduleCounts = [...moduleCountsMap.values()].sort((a, b) => b.count - a.count);
    return { moduleCounts, globalMatches: globalMatches.slice(0, 8) };
  }

  private sanitizeEvidenceSnippet(value: string): string {
    const compact = value.replace(/\s+/g, ' ').trim();
    if (!compact) return '';
    if (compact.length <= 140) return compact;
    return `${compact.slice(0, 137)}...`;
  }

  private async pesquisarEvidenciasPorCampo(
    ids: string[],
    pesquisaGeral: string,
    mode: PesquisaGeralMode = 'all',
  ): Promise<PesquisaGeralResult> {
    const termoRaw = pesquisaGeral?.trim();
    if (!termoRaw || ids.length === 0) {
      return {
        matchedIds: ids,
        evidenciasByDemanda: new Map(),
        contagemCampos: new Map(),
        scoreByDemanda: new Map(),
      };
    }

    const rpcRows = await this.loadIaDemandaDatasetViaRpc(ids);
    if (rpcRows) {
      return this.buildPesquisaGeralResultFromDataset(rpcRows, termoRaw, mode);
    }

    const sb = this.supabase.getClient();
    const [demRes, relRespRes, relSetorRes, relClienteRes, subRes, obsRes, recRes] = await Promise.all([
      sb
        .from('Demanda')
        .select('id, protocolo, assunto, status, prioridade, observacoes_gerais, prazo, created_at, resolvido_em, ultima_observacao_em, is_recorrente, criador_id')
        .in('id', ids),
      sb.from('demanda_responsavel').select('demanda_id, user_id').in('demanda_id', ids),
      sb.from('demanda_setor').select('demanda_id, setor_id').in('demanda_id', ids),
      sb.from('demanda_cliente').select('demanda_id, cliente_id').in('demanda_id', ids),
      sb.from('subtarefa').select('demanda_id, titulo, responsavel_user_id').in('demanda_id', ids),
      sb.from('observacao').select('demanda_id, texto').in('demanda_id', ids),
      sb.from('recorrencia_config').select('demanda_id, tipo, data_base, prazo_reabertura_dias').in('demanda_id', ids),
    ]);

    const rows = demRes.data ?? [];
    if (!rows.length) {
      return {
        matchedIds: [],
        evidenciasByDemanda: new Map(),
        contagemCampos: new Map(),
        scoreByDemanda: new Map(),
      };
    }

    const creatorIds = [...new Set(rows.map((r: any) => r?.criador_id).filter(Boolean))];
    const relResp = relRespRes.data ?? [];
    const relSetor = relSetorRes.data ?? [];
    const relCliente = relClienteRes.data ?? [];

    const respUserIds = [...new Set(relResp.map((r: any) => r?.user_id).filter(Boolean))];
    const setorIds = [...new Set(relSetor.map((r: any) => r?.setor_id).filter(Boolean))];
    const clienteIds = [...new Set(relCliente.map((r: any) => r?.cliente_id).filter(Boolean))];

    const subRespIds = [...new Set((subRes.data ?? []).map((s: any) => s?.responsavel_user_id).filter(Boolean))];
    const [usersRes, setoresRes, clientesRes] = await Promise.all([
      sb.from('User').select('id, name').in('id', [...new Set([...creatorIds, ...respUserIds, ...subRespIds])]),
      sb.from('Setor').select('id, name').in('id', setorIds.length ? setorIds : ['00000000-0000-0000-0000-000000000000']),
      sb.from('Cliente').select('id, name').in('id', clienteIds.length ? clienteIds : ['00000000-0000-0000-0000-000000000000']),
    ]);

    const userById = new Map((usersRes.data ?? []).map((u: any) => [u.id, String(u?.name ?? '')]));
    const setorById = new Map((setoresRes.data ?? []).map((s: any) => [s.id, String(s?.name ?? '')]));
    const clienteById = new Map((clientesRes.data ?? []).map((c: any) => [c.id, String(c?.name ?? '')]));

    const responsaveisByDemanda = new Map<string, string[]>();
    const setoresByDemanda = new Map<string, string[]>();
    const clientesByDemanda = new Map<string, string[]>();
    const subtarefasByDemanda = new Map<string, string[]>();
    const observacoesByDemanda = new Map<string, string[]>();
    const recorrenciaByDemanda = new Map<string, { tipo?: string; data_base?: string; prazo_reabertura_dias?: number }>();

    const pushMap = (map: Map<string, string[]>, key: string, value: string) => {
      if (!key || !value) return;
      const prev = map.get(key) ?? [];
      prev.push(value);
      map.set(key, prev);
    };

    for (const r of relResp) {
      const nome = userById.get(r?.user_id);
      if (nome) pushMap(responsaveisByDemanda, String(r?.demanda_id ?? ''), nome);
    }
    for (const r of relSetor) {
      const nome = setorById.get(r?.setor_id);
      if (nome) pushMap(setoresByDemanda, String(r?.demanda_id ?? ''), nome);
    }
    for (const r of relCliente) {
      const nome = clienteById.get(r?.cliente_id);
      if (nome) pushMap(clientesByDemanda, String(r?.demanda_id ?? ''), nome);
    }
    for (const s of subRes.data ?? []) {
      const titulo = String((s as any)?.titulo ?? '');
      const respName = userById.get((s as any)?.responsavel_user_id ?? '') ?? '';
      pushMap(
        subtarefasByDemanda,
        String((s as any)?.demanda_id ?? ''),
        respName ? `${titulo} responsavel ${respName}` : titulo,
      );
    }
    for (const o of obsRes.data ?? []) {
      pushMap(observacoesByDemanda, String((o as any)?.demanda_id ?? ''), String((o as any)?.texto ?? ''));
    }
    for (const rec of recRes.data ?? []) {
      const demandaId = String((rec as any)?.demanda_id ?? '');
      if (!demandaId) continue;
      recorrenciaByDemanda.set(demandaId, {
        tipo: (rec as any)?.tipo ?? '',
        data_base: (rec as any)?.data_base ? String((rec as any).data_base) : '',
        prazo_reabertura_dias: Number((rec as any)?.prazo_reabertura_dias ?? 0),
      });
    }

    const formatDateBr = (value: unknown): string => {
      if (value == null || value === '') return '';
      const d = new Date(String(value));
      if (Number.isNaN(d.getTime())) return '';
      const dd = String(d.getDate()).padStart(2, '0');
      const mm = String(d.getMonth() + 1).padStart(2, '0');
      const yyyy = d.getFullYear();
      return `${dd}/${mm}/${yyyy}`;
    };

    const normalizedPhrase = this.normalizeIaText(termoRaw);
    const quotedTerms = [...termoRaw.matchAll(/"([^"]{2,})"/g)].map((m) => this.normalizeIaText(m[1]));
    const tokens = this.tokenizePesquisaGeral(termoRaw);
    const allowed = this.allowedEvidenceFieldKeys(mode);
    const contagemCampos = new Map<EvidenciaCampoChave, { label: string; count: number }>();
    const evidenciasByDemanda = new Map<string, EvidenciaDemanda>();
    const fieldWeights: Record<EvidenciaCampoChave, number> = {
      protocolo: 5,
      assunto: 4.5,
      status: 4,
      prioridade: 1.2,
      prazo: 1.5,
      dataCriacao: 1.4,
      resolvidoEm: 1.2,
      ultimaObservacaoEm: 1.2,
      observacoesGerais: 4.2,
      criador: 2.2,
      responsaveis: 2.2,
      setores: 3.2,
      clientes: 3.2,
      subtarefas: 2.6,
      observacoes: 3.6,
      recorrencia: 2.1,
    };
    const matchedRows: { id: string; score: number }[] = [];
    const scoreByDemanda = new Map<string, number>();

    for (const row of rows) {
      const id = String((row as any)?.id ?? '');
      if (!id) continue;

      const criador = userById.get((row as any)?.criador_id) ?? '';
      const responsaveis = (responsaveisByDemanda.get(id) ?? []).join(' | ');
      const setores = (setoresByDemanda.get(id) ?? []).join(' | ');
      const clientes = (clientesByDemanda.get(id) ?? []).join(' | ');
      const subtarefas = (subtarefasByDemanda.get(id) ?? []).slice(0, 25).join(' | ');
      const observacoes = (observacoesByDemanda.get(id) ?? []).slice(0, 25).join(' | ');
      const rec = recorrenciaByDemanda.get(id);
      const prazoIso = (row as any)?.prazo ? String((row as any).prazo) : '';
      const prazoBr = formatDateBr((row as any)?.prazo);
      const criacaoIso = (row as any)?.created_at ? String((row as any).created_at) : '';
      const criacaoBr = formatDateBr((row as any)?.created_at);
      const resolvidoIso = (row as any)?.resolvido_em ? String((row as any).resolvido_em) : '';
      const resolvidoBr = formatDateBr((row as any)?.resolvido_em);
      const ultimaObsIso = (row as any)?.ultima_observacao_em ? String((row as any).ultima_observacao_em) : '';
      const ultimaObsBr = formatDateBr((row as any)?.ultima_observacao_em);
      const recDataIso = rec?.data_base ? String(rec.data_base) : '';
      const recDataBr = formatDateBr(rec?.data_base);

      const allFields: EvidenciaCampo[] = [
        { key: 'protocolo', label: 'Protocolo', value: String((row as any)?.protocolo ?? '') },
        { key: 'assunto', label: 'Assunto', value: String((row as any)?.assunto ?? '') },
        {
          key: 'status',
          label: 'Status',
          value: `${String((row as any)?.status ?? '')} ${this.statusLabelPt(String((row as any)?.status ?? ''))}`,
        },
        { key: 'prioridade', label: 'Prioridade', value: (row as any)?.prioridade ? 'prioridade alta sim' : 'prioridade nao' },
        { key: 'prazo', label: 'Prazo', value: `${prazoIso} ${prazoBr}`.trim() },
        { key: 'dataCriacao', label: 'Data de criação', value: `${criacaoIso} ${criacaoBr}`.trim() },
        { key: 'resolvidoEm', label: 'Resolução', value: `${resolvidoIso} ${resolvidoBr}`.trim() },
        { key: 'ultimaObservacaoEm', label: 'Última atualização', value: `${ultimaObsIso} ${ultimaObsBr}`.trim() },
        { key: 'observacoesGerais', label: 'Observações gerais', value: String((row as any)?.observacoes_gerais ?? '') },
        { key: 'criador', label: 'Criador', value: criador },
        { key: 'responsaveis', label: 'Responsáveis', value: responsaveis },
        { key: 'setores', label: 'Setores', value: setores },
        { key: 'clientes', label: 'Clientes', value: clientes },
        { key: 'subtarefas', label: 'Subtarefas', value: subtarefas },
        { key: 'observacoes', label: 'Observações', value: observacoes },
        {
          key: 'recorrencia',
          label: 'Recorrência',
          value: rec
            ? `recorrente ${rec.tipo ?? ''} data base ${recDataIso} ${recDataBr} prazo reabertura ${rec.prazo_reabertura_dias ?? 0}`
            : (row as any)?.is_recorrente
            ? 'recorrente'
            : 'nao recorrente',
        },
      ];

      const fields = allowed ? allFields.filter((f) => allowed.has(f.key)) : allFields;
      if (!fields.length) continue;

      const matchedFields = new Map<EvidenciaCampoChave, EvidenciaMatch>();
      const tokenHitsUnion = new Set<string>();
      let quotedMatched = false;
      let phraseMatched = false;
      let rowScore = 0;

      for (const field of fields) {
        const fieldText = String(field.value ?? '').trim();
        if (!fieldText) continue;
        const fieldNorm = this.normalizeIaText(fieldText);
        if (!fieldNorm) continue;

        const tokenHits = tokens.filter((t) => this.hasPesquisaTokenMatch(fieldNorm, t));
        tokenHits.forEach((t) => tokenHitsUnion.add(t));

        const hasQuotedHit =
          quotedTerms.length > 0 &&
          quotedTerms.some((q) => {
            if (!q) return false;
            if (q.includes(' ')) return fieldNorm.includes(q);
            return this.hasPesquisaTokenMatch(fieldNorm, q);
          });

        const hasPhraseHit =
          !quotedTerms.length &&
          !tokens.length &&
          normalizedPhrase.length >= 2 &&
          fieldNorm.includes(normalizedPhrase);

        const includeAsEvidence =
          hasQuotedHit ||
          tokenHits.length > 0 ||
          hasPhraseHit;

        if (hasQuotedHit) quotedMatched = true;
        if (hasPhraseHit) phraseMatched = true;
        if (hasQuotedHit) rowScore += (fieldWeights[field.key] ?? 1) * 3;
        else if (tokenHits.length > 0) rowScore += tokenHits.length * (fieldWeights[field.key] ?? 1);
        else if (hasPhraseHit) rowScore += (fieldWeights[field.key] ?? 1.2) * 1.8;

        if (includeAsEvidence && !matchedFields.has(field.key)) {
          matchedFields.set(field.key, {
            key: field.key,
            label: field.label,
            snippet: this.sanitizeEvidenceSnippet(fieldText),
          });
        }
      }

      let rowMatched = false;
      if (quotedTerms.length) {
        rowMatched = quotedMatched;
      } else if (!tokens.length) {
        if (this.isGenericPesquisaGeralPhrase(normalizedPhrase)) {
          rowMatched = fields.some((f) => String(f.value ?? '').trim().length > 0);
        } else {
          rowMatched = phraseMatched;
        }
      } else {
        const minHits = tokens.length <= 2 ? 1 : Math.ceil(tokens.length * 0.5);
        rowMatched = tokenHitsUnion.size >= minHits;
      }

      if (!rowMatched) continue;

      if (rowScore <= 0) rowScore = 0.8;
      matchedRows.push({ id, score: rowScore });
      scoreByDemanda.set(id, rowScore);
      const matchedFieldsList = [...matchedFields.values()];
      evidenciasByDemanda.set(id, {
        demandaId: id,
        protocolo: String((row as any)?.protocolo ?? '—'),
        assunto: String((row as any)?.assunto ?? '—'),
        matchedFields: matchedFieldsList,
      });

      const uniqueFieldKeys = new Set(matchedFieldsList.map((m) => m.key));
      uniqueFieldKeys.forEach((key) => {
        const found = contagemCampos.get(key);
        const label = matchedFields.get(key)?.label ?? key;
        contagemCampos.set(key, { label, count: (found?.count ?? 0) + 1 });
      });
    }

    matchedRows.sort((a, b) => b.score - a.score);
    const matchedIds = matchedRows.map((m) => m.id);
    return { matchedIds, evidenciasByDemanda, contagemCampos, scoreByDemanda };
  }

  private async resolveFilteredIds(
    userId: string,
    filters: ListDemandasFiltersDto,
    pesquisaMode: PesquisaGeralMode = 'all',
  ): Promise<{ ids: string[]; pesquisaEvidence?: PesquisaGeralResult }> {
    let ids = await this.visibility.visibleDemandaIds(userId);
    const sb = this.supabase.getClient();

    if (filters.clienteId) {
      const { data } = await sb.from('demanda_cliente').select('demanda_id').eq('cliente_id', filters.clienteId);
      const clienteIds = new Set((data ?? []).map((d: any) => d.demanda_id));
      ids = ids.filter((id: string) => clienteIds.has(id));
    }
    if (filters.ocultarStandby && filters.status !== 'standby') {
      const { data } = await sb.from('Demanda').select('id').neq('status', 'standby');
      const set = new Set((data ?? []).map((d: any) => d.id));
      ids = ids.filter((id: string) => set.has(id));
    }
    if (filters.responsavelPrincipalId) {
      let query = sb
        .from('demanda_responsavel')
        .select('demanda_id')
        .eq('user_id', filters.responsavelPrincipalId);
      if (filters.responsavelApenasPrincipal) {
        query = query.eq('is_principal', true);
      }
      const { data } = await query;
      const set = new Set((data ?? []).map((d: any) => d.demanda_id));
      ids = ids.filter((id: string) => set.has(id));
    }
    if (filters.setorIds?.length) {
      const { data } = await sb.from('demanda_setor').select('demanda_id').in('setor_id', filters.setorIds);
      const set = new Set((data ?? []).map((d: any) => d.demanda_id));
      ids = ids.filter((id: string) => set.has(id));
    }
    if (filters.tipoRecorrencia) {
      const { data: recIds } = await sb.from('recorrencia_config').select('demanda_id').eq('tipo', filters.tipoRecorrencia);
      const recSet = new Set((recIds ?? []).map((d: any) => d.demanda_id));
      ids = ids.filter((id: string) => recSet.has(id));
    }
    if (filters.pesquisarTarefaOuObservacao) {
      const term = `%${filters.pesquisarTarefaOuObservacao}%`;
      const [sub, obs] = await Promise.all([
        sb.from('subtarefa').select('demanda_id').ilike('titulo', term),
        sb.from('observacao').select('demanda_id').ilike('texto', term),
      ]);
      const set = new Set([...(sub.data ?? []).map((d: any) => d.demanda_id), ...(obs.data ?? []).map((d: any) => d.demanda_id)]);
      ids = ids.filter((id: string) => set.has(id));
    }
    if (filters.pesquisaGeral?.trim()) {
      const evidence = await this.pesquisarEvidenciasPorCampo(ids, filters.pesquisaGeral.trim(), pesquisaMode);
      const set = new Set(evidence.matchedIds);
      ids = ids.filter((id: string) => set.has(id));
      return { ids, pesquisaEvidence: evidence };
    }

    return { ids };
  }

  private async listByIds(
    ids: string[],
    filters: ListDemandasFiltersDto,
    scoreByDemanda?: Map<string, number>,
  ) {
    if (ids.length === 0) return { data: [], total: 0 };
    const sb = this.supabase.getClient();
    const { pageSize, offset } = this.getPagination(filters);

    let q = sb
      .from('Demanda')
      .select('*', { count: 'exact' })
      .in('id', ids)
      .order('created_at', { ascending: false })
      .range(offset, offset + pageSize - 1);
    if (filters.assunto) q = q.ilike('assunto', `%${filters.assunto}%`);
    if (filters.status) q = q.eq('status', filters.status);
    if (filters.protocolo) q = q.ilike('protocolo', `%${filters.protocolo}%`);
    if (filters.prioridade !== undefined) q = q.eq('prioridade', filters.prioridade);
    if (filters.criadorId) q = q.eq('criador_id', filters.criadorId);
    if (filters.prazoDe) q = q.gte('prazo', filters.prazoDe);
    if (filters.prazoAte) q = q.lte('prazo', filters.prazoAte);
    const todayInSaoPaulo = getTodayInSaoPaulo();
    if (filters.condicaoPrazo === 'vencido') {
      q = q.neq('status', 'concluido').neq('status', 'cancelado').lt('prazo', todayInSaoPaulo);
    }
    if (filters.condicaoPrazo === 'no_prazo') {
      q = q.neq('status', 'concluido').neq('status', 'cancelado').gte('prazo', todayInSaoPaulo);
    }
    if (filters.condicaoPrazo === 'finalizada') q = q.in('status', ['concluido', 'cancelado']);
    if (filters.dataCriacaoDe) q = q.gte('created_at', filters.dataCriacaoDe);
    if (filters.dataCriacaoAte) q = q.lte('created_at', filters.dataCriacaoAte);

    const { data: rows, count: total } = await q;
    const relationMaps = await this.loadDemandaRelationsBatch(rows ?? [], false);
    const data = [];
    for (const row of rows ?? []) {
      const demandaId = String(row?.id ?? '');
      data.push(
        mapDemandaList(
          row,
          relationMaps.criadorByDemanda.get(demandaId) ?? undefined,
          relationMaps.responsaveisByDemanda.get(demandaId) ?? [],
          relationMaps.setoresByDemanda.get(demandaId) ?? [],
          relationMaps.clientesByDemanda.get(demandaId) ?? [],
        ),
      );
    }
    if (filters.pesquisaGeral?.trim() && scoreByDemanda?.size) {
      data.sort((a: any, b: any) => (scoreByDemanda.get(String(b?.id ?? '')) ?? 0) - (scoreByDemanda.get(String(a?.id ?? '')) ?? 0));
    }
    return { data, total: total ?? 0 };
  }

  async list(userId: string, filters: ListDemandasFiltersDto) {
    if (filters.responsavelPrincipalId || (filters.ocultarStandby && filters.status !== 'standby')) {
      const { ids, pesquisaEvidence } = await this.resolveFilteredIds(userId, filters, 'all');
      if (!ids.length) {
        return { data: [], total: 0 };
      }
      const filtersWithoutResponsavel = {
        ...filters,
        responsavelPrincipalId: undefined,
        responsavelApenasPrincipal: undefined,
        ocultarStandby: undefined,
      };
      const rpcResult = await this.listDemandasViaRpc(userId, filtersWithoutResponsavel, ids);
      if (rpcResult) return rpcResult;
      return this.listByIds(ids, filtersWithoutResponsavel, pesquisaEvidence?.scoreByDemanda);
    }

    if (filters.pesquisaGeral?.trim()) {
      const { ids, pesquisaEvidence } = await this.resolveFilteredIds(userId, filters, 'all');
      if (!ids.length) {
        return { data: [], total: 0 };
      }
      const rpcResult = await this.listDemandasViaRpc(userId, filters, ids);
      if (rpcResult) {
        if (pesquisaEvidence?.scoreByDemanda?.size) {
          rpcResult.data.sort(
            (a: any, b: any) =>
              (pesquisaEvidence.scoreByDemanda.get(String(b?.id ?? '')) ?? 0) -
              (pesquisaEvidence.scoreByDemanda.get(String(a?.id ?? '')) ?? 0),
          );
        }
        return rpcResult;
      }
      return this.listByIds(ids, filters, pesquisaEvidence?.scoreByDemanda);
    }

    const rpcResult = await this.listDemandasViaRpc(userId, filters);
    if (rpcResult) return rpcResult;
    const { ids, pesquisaEvidence } = await this.resolveFilteredIds(userId, filters, 'all');
    return this.listByIds(ids, filters, pesquisaEvidence?.scoreByDemanda);
  }

  async findOne(userId: string, id: string) {
    const rpcRow = await this.findOneViaProtectedRpc(userId, id);
    if (rpcRow) return rpcRow;

    const sb = this.supabase.getClient();
    const { data: row } = await sb.from('Demanda').select('*').eq('id', id).single();
    if (!row) throw new NotFoundException('Demanda não encontrada');
    const can = await this.visibility.canViewDemanda(userId, id, row.criador_id ?? null);
    if (!can) throw new ForbiddenException('Sem permissão para ver esta demanda');
    const rel = await this.loadDemandaRelationsBatch([row], true);
    const rec = (rel.recorrenciaByDemanda.get(id) ?? null) as { data_base?: unknown; tipo?: string; prazo_reabertura_dias?: number } | null;
    return {
      ...mapDemandaList(
        row,
        rel.criadorByDemanda.get(id) ?? undefined,
        rel.responsaveisByDemanda.get(id) ?? [],
        rel.setoresByDemanda.get(id) ?? [],
        rel.clientesByDemanda.get(id) ?? [],
      ),
      subtarefas: rel.subtarefasByDemanda.get(id) ?? [],
      observacoes: rel.observacoesByDemanda.get(id) ?? [],
      anexos: rel.anexosByDemanda.get(id) ?? [],
      recorrenciaConfig: rec
        ? { dataBase: toDateISO(rec.data_base) ?? (typeof rec.data_base === 'string' ? rec.data_base : null), tipo: rec.tipo ?? '', prazoReaberturaDias: rec.prazo_reabertura_dias ?? 0 }
        : null,
    };
  }

  async update(userId: string, id: string, dto: UpdateDemandaDto) {
    await this.findOne(userId, id);
    const isResponsavel = await this.isResponsavelPrincipal(userId, id);
    let newStatus = dto.status as DemandaStatus | undefined;
    if (newStatus && !isResponsavel && newStatus !== 'standby') newStatus = 'standby';

    const sb = this.supabase.getClient();
    const upd: any = {};
    if (dto.assunto != null) upd.assunto = dto.assunto;
    if (dto.prioridade !== undefined) upd.prioridade = dto.prioridade;
    if (dto.prazo != null) upd.prazo = dto.prazo;
    if (newStatus) {
      upd.status = newStatus;
      if (newStatus === 'concluido') upd.resolvido_em = new Date().toISOString();
      else upd.resolvido_em = null;
    }
    if (dto.observacoesGerais !== undefined) upd.observacoes_gerais = dto.observacoesGerais;
    if (dto.isPrivada !== undefined) {
      upd.is_privada = dto.isPrivada;
      upd.private_owner_user_id = dto.isPrivada ? userId : null;
    }
    if (dto.isRecorrente !== undefined) upd.is_recorrente = dto.isRecorrente;
    if (Object.keys(upd).length) await sb.from('Demanda').update(upd).eq('id', id);

    if (dto.setores) {
      await sb.from('demanda_setor').delete().eq('demanda_id', id);
      if (dto.setores.length) await sb.from('demanda_setor').insert(dto.setores.map((setorId) => ({ demanda_id: id, setor_id: setorId })));
    }
    if (dto.clienteIds) {
      await sb.from('demanda_cliente').delete().eq('demanda_id', id);
      if (dto.clienteIds.length) await sb.from('demanda_cliente').insert(dto.clienteIds.map((clienteId) => ({ demanda_id: id, cliente_id: clienteId })));
    }
    if (dto.responsaveis) {
      await sb.from('demanda_responsavel').delete().eq('demanda_id', id);
      if (dto.responsaveis.length) await sb.from('demanda_responsavel').insert(dto.responsaveis.map((r) => ({ demanda_id: id, user_id: r.userId, is_principal: r.isPrincipal ?? false })));
    }
    if (dto.privateViewerIds) {
      await sb.from('demanda_private_viewer').delete().eq('demanda_id', id);
      if (dto.privateViewerIds.length) await sb.from('demanda_private_viewer').insert([...new Set(dto.privateViewerIds)].map((viewerId) => ({ demanda_id: id, user_id: viewerId })));
    }
    if (dto.subtarefas) {
      await sb.from('subtarefa').delete().eq('demanda_id', id);
      if (dto.subtarefas.length) {
        await sb.from('subtarefa').insert(
          dto.subtarefas.map((t, i) => ({
            demanda_id: id,
            titulo: t.titulo,
            concluida: t.concluida ?? false,
            ordem: (t as any).ordem ?? i,
            responsavel_user_id: (t as any).responsavelUserId ?? null,
          })),
        );
      }
    }
    if (dto.recorrencia) {
      const dataBase = dto.recorrencia.dataBase;
      const tipo = dto.recorrencia.tipo;
      const prazoReaberturaDias = Number(dto.recorrencia.prazoReaberturaDias ?? 0) || 0;
      const { data: existing } = await sb.from('recorrencia_config').select('id').eq('demanda_id', id).single();
      if (existing?.id) {
        await sb.from('recorrencia_config').update({
          data_base: dataBase,
          tipo,
          prazo_reabertura_dias: prazoReaberturaDias,
        }).eq('demanda_id', id);
      } else {
        await sb.from('recorrencia_config').insert({
          demanda_id: id,
          data_base: dataBase,
          tipo,
          prazo_reabertura_dias: prazoReaberturaDias,
        });
      }
      await sb.from('Demanda').update({ is_recorrente: true }).eq('id', id);
    } else if (dto.isRecorrente === false) {
      await sb.from('recorrencia_config').delete().eq('demanda_id', id);
    }
    this.visibility.clearVisibleDemandaIdsCache();
    return this.findOne(userId, id);
  }

  /** Exclui a demanda (apenas admin). Relacionamentos são removidos em cascata. */
  async remove(userId: string, id: string) {
    await this.findOne(userId, id);
    const sb = this.supabase.getClient();
    const { error } = await sb.from('Demanda').delete().eq('id', id);
    if (error) throw new Error(error.message);
    this.visibility.clearVisibleDemandaIdsCache();
    return { id };
  }

  async addObservacao(userId: string, demandaId: string, texto: string) {
    await this.findOne(userId, demandaId);
    if (!texto?.trim()) throw new BadRequestException('Informe o texto da observação.');
    const sb = this.supabase.getClient();
    const isResponsavel = await this.isResponsavelPrincipal(userId, demandaId);
    await sb.from('observacao').insert({ demanda_id: demandaId, user_id: userId, texto: texto.trim() });
    const demandaUpd: { status?: string; ultima_observacao_em: string } = { ultima_observacao_em: new Date().toISOString() };
    if (!isResponsavel) demandaUpd.status = 'standby';
    await sb.from('Demanda').update(demandaUpd).eq('id', demandaId);
    return this.findOne(userId, demandaId);
  }

  async updateObservacao(userId: string, demandaId: string, observacaoId: string, texto: string) {
    await this.findOne(userId, demandaId);
    if (!texto?.trim()) throw new BadRequestException('Informe o texto da observação.');
    const sb = this.supabase.getClient();
    const { data: observacao } = await sb
      .from('observacao')
      .select('id, user_id')
      .eq('id', observacaoId)
      .eq('demanda_id', demandaId)
      .single();
    if (!observacao?.id) throw new NotFoundException('Observação não encontrada');

    const isResponsavel = await this.isResponsavelPrincipal(userId, demandaId);
    if (String(observacao.user_id) !== userId && !isResponsavel) {
      throw new ForbiddenException('Sem permissão para editar esta observação.');
    }

    const { error } = await sb
      .from('observacao')
      .update({ texto: texto.trim() })
      .eq('id', observacaoId)
      .eq('demanda_id', demandaId);
    if (error) throw new Error(error.message);
    return this.findOne(userId, demandaId);
  }

  async addAnexo(userId: string, demandaId: string, file: Express.Multer.File) {
    await this.findOne(userId, demandaId);
    if (!file?.buffer?.length) throw new BadRequestException('Arquivo inválido.');
    const safeName = `${uuidv4()}-${(file.originalname || 'file').replace(/[^a-zA-Z0-9._-]/g, '_')}`;
    const sb = this.supabase.getClient();
    const bucket = await this.ensureAnexosBucket();
    const objectPath = ['demandas', demandaId, safeName].join('/');
    const { error: uploadError } = await sb.storage
      .from(bucket)
      .upload(objectPath, file.buffer, {
        contentType: file.mimetype || 'application/octet-stream',
        upsert: false,
      });
    if (uploadError) throw new ServiceUnavailableException(uploadError.message);
    const { data, error } = await sb
      .from('anexo')
      .insert({
        demanda_id: demandaId,
        filename: file.originalname || 'file',
        mime_type: file.mimetype || 'application/octet-stream',
        size: file.size,
        storage_path: this.buildSupabaseStoragePath(bucket, objectPath),
      })
      .select()
      .single();
    if (error) throw new Error(error.message);
    return data;
  }

  async getAnexoForDownload(userId: string, demandaId: string, anexoId: string) {
    await this.findOne(userId, demandaId);
    const sb = this.supabase.getClient();
    const { data: anexo } = await sb.from('anexo').select('*').eq('id', anexoId).eq('demanda_id', demandaId).single();
    if (!anexo) throw new NotFoundException('Anexo não encontrado');
    const storageLocation = this.parseAnexoStoragePath(anexo.storage_path);
    if (storageLocation.mode === 'supabase') {
      const { data: fileData, error } = await sb.storage
        .from(storageLocation.bucket)
        .download(storageLocation.objectPath);
      if (error || !fileData) throw new NotFoundException('Arquivo não encontrado');
      const buffer = Buffer.from(await fileData.arrayBuffer());
      return { buffer, filename: anexo.filename, mimeType: anexo.mime_type };
    }
    const uploadDir = process.env.UPLOAD_DIR || './uploads';
    const fullPath = path.resolve(path.join(uploadDir, storageLocation.objectPath));
    if (!fs.existsSync(fullPath)) throw new NotFoundException('Arquivo não encontrado');
    return { path: fullPath, filename: anexo.filename, mimeType: anexo.mime_type };
  }

  private async isResponsavelPrincipal(userId: string, demandaId: string): Promise<boolean> {
    const { data } = await this.supabase.getClient().from('demanda_responsavel').select('id').eq('demanda_id', demandaId).eq('user_id', userId).eq('is_principal', true).limit(1);
    return !!data?.length;
  }

  async exportExcel(userId: string, filters: ListDemandasFiltersDto) {
    const needsPrefilter = filters.pesquisaGeral?.trim() || filters.responsavelPrincipalId || (filters.ocultarStandby && filters.status !== 'standby');
    const prefilteredIds = needsPrefilter
      ? (await this.resolveFilteredIds(userId, filters, 'all')).ids
      : null;
    if (prefilteredIds && !prefilteredIds.length) return [];

    const rpcFilters = prefilteredIds && filters.responsavelPrincipalId
      ? { ...filters, responsavelPrincipalId: undefined, responsavelApenasPrincipal: undefined, ocultarStandby: undefined }
      : filters;

    const rpcResult = await this.listDemandasViaRpc(
      userId,
      { ...rpcFilters, page: 1, pageSize: 10000 },
      prefilteredIds,
    );
    if (rpcResult) return rpcResult.data;

    let ids = prefilteredIds ?? await this.visibility.visibleDemandaIds(userId);
    const sb = this.supabase.getClient();
    if (filters.clienteId) {
      const { data } = await sb.from('demanda_cliente').select('demanda_id').eq('cliente_id', filters.clienteId);
      ids = ids.filter((id) => (data ?? []).some((d: any) => d.demanda_id === id));
    }
    if (ids.length === 0) return [];
    let q = sb.from('Demanda').select('*').in('id', ids).order('created_at', { ascending: false }).limit(10000);
    if (filters.status) q = q.eq('status', filters.status);
    if (filters.assunto) q = q.ilike('assunto', `%${filters.assunto}%`);
    const { data: rows } = await q;
    const relationMaps = await this.loadDemandaRelationsBatch(rows ?? [], false);
    const result = [];
    for (const row of rows ?? []) {
      const demandaId = String(row?.id ?? '');
      result.push(
        mapDemandaList(
          row,
          relationMaps.criadorByDemanda.get(demandaId) ?? undefined,
          relationMaps.responsaveisByDemanda.get(demandaId) ?? [],
          relationMaps.setoresByDemanda.get(demandaId) ?? [],
          relationMaps.clientesByDemanda.get(demandaId) ?? [],
        ),
      );
    }
    return result;
  }

  private sanitizeIaLinkUrl(url: string): string | null {
    const cleaned = url.trim();
    if (!cleaned) return null;
    if (cleaned.startsWith('/')) return cleaned;
    if (/^https?:\/\/[^\s]+$/i.test(cleaned)) return cleaned;
    return null;
  }

  private buildDemandasLinkFromFilters(filters: ListDemandasFiltersDto): string {
    const params = new URLSearchParams();
    const append = (key: string, value: unknown) => {
      if (value === undefined || value === null || value === '') return;
      if (Array.isArray(value)) {
        value.forEach((v) => params.append(key, String(v)));
      } else {
        params.append(key, String(value));
      }
    };

    append('clienteId', filters.clienteId);
    append('assunto', filters.assunto);
    append('status', filters.status);
    append('ocultarStandby', filters.ocultarStandby);
    append('tipoRecorrencia', filters.tipoRecorrencia);
    append('protocolo', filters.protocolo);
    append('prioridade', filters.prioridade);
    append('criadorId', filters.criadorId);
    append('responsavelPrincipalId', filters.responsavelPrincipalId);
    append('responsavelApenasPrincipal', filters.responsavelApenasPrincipal);
    append('setorIds', filters.setorIds);
    append('condicaoPrazo', filters.condicaoPrazo);
    append('pesquisarTarefaOuObservacao', filters.pesquisarTarefaOuObservacao);
    append('pesquisaGeral', filters.pesquisaGeral);
    append('dataCriacaoDe', filters.dataCriacaoDe);
    append('dataCriacaoAte', filters.dataCriacaoAte);
    append('prazoDe', filters.prazoDe);
    append('prazoAte', filters.prazoAte);

    const query = params.toString();
    return query ? `/demandas?${query}` : '/demandas';
  }

  private normalizeIaText(value: string): string {
    return value
      .toLowerCase()
      .normalize('NFD')
      .replace(/[\u0300-\u036f]/g, '')
      .trim();
  }

  private escapeRegExp(value: string): string {
    return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  }

  private extractHeuristicIaFilters(
    query: string,
    setores: { id: string; name: string }[],
    clientes: { id: string; name: string }[],
    users: { id: string; name: string }[],
  ): ListDemandasFiltersDto {
    const filters: ListDemandasFiltersDto = {};
    const normalizedQuery = this.normalizeIaText(query);
    const searchMode = this.parsePesquisaGeralMode(query);
    const findUniqueNameMatch = <T extends { id: string; name: string }>(items: T[]): T | null => {
      const matches = items.filter((item) => {
        const normalizedName = this.normalizeIaText(String(item.name || ''));
        if (!normalizedName) return false;
        if (normalizedName.length <= 2) {
          return new RegExp(`\\b${this.escapeRegExp(normalizedName)}\\b`).test(normalizedQuery);
        }
        return normalizedQuery.includes(normalizedName);
      });
      return matches.length === 1 ? matches[0] : null;
    };
    const clienteContextHint =
      normalizedQuery.includes('cliente') ||
      normalizedQuery.includes('empresa') ||
      normalizedQuery.includes('cnpj') ||
      normalizedQuery.includes('cpf');
    const setorContextHint =
      normalizedQuery.includes('setor') ||
      normalizedQuery.includes('equipe') ||
      normalizedQuery.includes('time') ||
      normalizedQuery.includes('area');
    const criadorContextHint =
      normalizedQuery.includes('criador') ||
      normalizedQuery.includes('criado por');
    const responsavelContextHint =
      normalizedQuery.includes('responsavel') ||
      normalizedQuery.includes('responsável') ||
      normalizedQuery.includes('da vez');

    if (normalizedQuery.includes('standby') || normalizedQuery.includes('stand by')) filters.status = 'standby';
    else if (normalizedQuery.includes('cancelad')) filters.status = 'cancelado';
    else if (normalizedQuery.includes('em andamento') || normalizedQuery.includes('andamento')) filters.status = 'em_andamento';
    else if (normalizedQuery.includes('em aberto') || normalizedQuery.includes('aberta') || normalizedQuery.includes('aberto')) filters.status = 'em_aberto';
    else if (normalizedQuery.includes('concluid')) filters.status = 'concluido';

    if (
      normalizedQuery.includes('sem prioridade') ||
      normalizedQuery.includes('nao prioridade') ||
      normalizedQuery.includes('sem urgencia')
    ) {
      filters.prioridade = false;
    } else if (
      normalizedQuery.includes('prioridade') ||
      normalizedQuery.includes('urgente') ||
      normalizedQuery.includes('alta prioridade')
    ) {
      filters.prioridade = true;
    }

    if (normalizedQuery.includes('vencid')) filters.condicaoPrazo = 'vencido';
    else if (
      normalizedQuery.includes('no prazo') ||
      normalizedQuery.includes('dentro do prazo') ||
      normalizedQuery.includes('vai vencer') ||
      normalizedQuery.includes('ira vencer')
    ) {
      filters.condicaoPrazo = 'no_prazo';
    } else if (normalizedQuery.includes('finalizada')) {
      filters.condicaoPrazo = 'finalizada';
    }

    if (normalizedQuery.includes('diaria') || normalizedQuery.includes('diario')) filters.tipoRecorrencia = 'diaria';
    else if (normalizedQuery.includes('quinzenal')) filters.tipoRecorrencia = 'quinzenal';
    else if (normalizedQuery.includes('semanal')) filters.tipoRecorrencia = 'semanal';
    else if (normalizedQuery.includes('mensal')) filters.tipoRecorrencia = 'mensal';

    const protocoloMatch =
      query.match(/\b([A-Za-z]{2,}-\d{4}-\d{3,})\b/i) ||
      query.match(/\b([A-Za-z0-9]{2,}-\d{2,}-\d{2,})\b/i);
    if (protocoloMatch?.[1]) {
      filters.protocolo = protocoloMatch[1].toUpperCase();
    }

    const setorIds = setores
      .filter((setor) => {
        const setorName = this.normalizeIaText(String(setor.name || ''));
        if (!setorName) return false;
        if (setorName.length <= 2) {
          return new RegExp(`\\b${this.escapeRegExp(setorName)}\\b`).test(normalizedQuery);
        }
        return normalizedQuery.includes(setorName);
      })
      .map((setor) => setor.id);
    if (setorIds.length) filters.setorIds = [...new Set(setorIds)];
    else if (setorContextHint) {
      const uniqueSetor = findUniqueNameMatch(setores);
      if (uniqueSetor?.id) filters.setorIds = [uniqueSetor.id];
    }

    if (clienteContextHint || !criadorContextHint && !responsavelContextHint) {
      const cliente = findUniqueNameMatch(clientes);
      if (cliente?.id) filters.clienteId = cliente.id;
    }

    if (criadorContextHint) {
      const criador = findUniqueNameMatch(users);
      if (criador?.id) filters.criadorId = criador.id;
    }

    if (normalizedQuery.includes('responsavel') || normalizedQuery.includes('responsável')) {
      const responsavel = users.find((u) => {
        const n = this.normalizeIaText(String(u.name || ''));
        if (!n) return false;
        if (n.length <= 2) return new RegExp(`\\b${this.escapeRegExp(n)}\\b`).test(normalizedQuery);
        return normalizedQuery.includes(n);
      });
      if (responsavel?.id) filters.responsavelPrincipalId = responsavel.id;
    }

    const normalizedPesquisa = this.normalizePesquisaGeralFromQuery(query, filters.pesquisaGeral);
    if (normalizedPesquisa) filters.pesquisaGeral = normalizedPesquisa;

    if (searchMode === 'status_only' && !filters.pesquisaGeral && filters.status) {
      filters.pesquisaGeral = this.statusLabelPt(filters.status);
    }

    const hasSpecificFilter = Object.keys(filters).some((k) => k !== 'pesquisaGeral');
    if (!filters.pesquisaGeral && !hasSpecificFilter && normalizedQuery.length >= 3) {
      const freeTextTokens = this.tokenizePesquisaGeral(query);
      if (freeTextTokens.length) {
        filters.pesquisaGeral = freeTextTokens.slice(0, 4).join(' ');
      }
    }

    return filters;
  }

  private getPagesContextForIa(): { label: string; url: string; descricao: string }[] {
    return [
      { label: 'Lista de demandas', url: '/demandas', descricao: 'Consultar, filtrar e acompanhar demandas' },
      { label: 'Nova demanda', url: '/demandas/nova', descricao: 'Criar uma nova demanda manualmente ou via template' },
      { label: 'Cadastros', url: '/cadastros', descricao: 'Gerenciar setores, clientes e responsáveis' },
      { label: 'Templates', url: '/templates', descricao: 'Criar e manter modelos de demandas recorrentes' },
      { label: 'Dashboard KPIs', url: '/dashboard', descricao: 'Visualizar indicadores e métricas de desempenho' },
      { label: 'Login', url: '/login', descricao: 'Acesso ao sistema' },
    ];
  }

  private inferLinksFromQuery(query: string): { label: string; url: string }[] {
    const q = this.normalizeIaText(query);
    const out: { label: string; url: string }[] = [];
    const push = (label: string, url: string) => {
      if (!out.some((x) => x.url === url)) out.push({ label, url });
    };

    if (q.includes('dashboard') || q.includes('kpi') || q.includes('indicador')) {
      push('Abrir Dashboard KPIs', '/dashboard');
    }
    if (q.includes('template') || q.includes('modelo')) {
      push('Abrir Templates', '/templates');
    }
    if (
      q.includes('cadastro') ||
      q.includes('cliente') ||
      q.includes('setor') ||
      q.includes('responsavel') ||
      q.includes('responsável')
    ) {
      push('Abrir Cadastros', '/cadastros');
    }
    if (q.includes('nova demanda') || q.includes('criar demanda') || q.includes('abrir demanda')) {
      push('Criar Nova Demanda', '/demandas/nova');
    }
    if (!out.length || q.includes('demanda')) {
      push('Abrir Demandas', '/demandas');
    }
    return out;
  }

  private async buildSystemContextForIa(userId: string): Promise<{
    totalDemandasVisiveis: number;
    porStatus: Record<string, number>;
    demandasRecentes: { protocolo: string; assunto: string; status: string }[];
    setores: string[];
    clientesAtivos: string[];
    templates: string[];
    paginas: { label: string; url: string; descricao: string }[];
  }> {
    const rpcContext = await this.loadIaSystemContextViaRpc(userId);
    if (rpcContext) {
      return {
        ...rpcContext,
        paginas: this.getPagesContextForIa(),
      };
    }

    const sb = this.supabase.getClient();
    const paginas = this.getPagesContextForIa();
    const visibleIds = await this.visibility.visibleDemandaIds(userId);

    const porStatus: Record<string, number> = {};
    let demandasRecentes: { protocolo: string; assunto: string; status: string }[] = [];

    if (visibleIds.length) {
      const [statusRows, recentRows] = await Promise.all([
        sb.from('Demanda').select('status').in('id', visibleIds).limit(5000),
        sb
          .from('Demanda')
          .select('protocolo, assunto, status, created_at')
          .in('id', visibleIds)
          .order('created_at', { ascending: false })
          .limit(5),
      ]);

      for (const row of statusRows.data ?? []) {
        const key = String((row as any)?.status ?? 'desconhecido');
        porStatus[key] = (porStatus[key] ?? 0) + 1;
      }

      demandasRecentes = (recentRows.data ?? []).map((r: any) => ({
        protocolo: r?.protocolo ?? '—',
        assunto: r?.assunto ?? '—',
        status: r?.status ?? '—',
      }));
    }

    const [setoresRes, clientesRes, templatesRes] = await Promise.all([
      sb.from('Setor').select('name').order('name').limit(12),
      sb.from('Cliente').select('name').eq('active', true).order('name').limit(12),
      sb.from('Template').select('name').order('updated_at', { ascending: false }).limit(8),
    ]);

    return {
      totalDemandasVisiveis: visibleIds.length,
      porStatus,
      demandasRecentes,
      setores: (setoresRes.data ?? []).map((s: any) => String(s?.name ?? '')).filter(Boolean),
      clientesAtivos: (clientesRes.data ?? []).map((c: any) => String(c?.name ?? '')).filter(Boolean),
      templates: (templatesRes.data ?? []).map((t: any) => String(t?.name ?? '')).filter(Boolean),
      paginas,
    };
  }

  private extractProtocolsFromText(message: string): string[] {
    return [...new Set(message.match(/\b[A-Z]{2,}-\d{4}-\d{3,6}\b/g) ?? [])];
  }

  private validateGeneratedIaAnswer(
    message: string,
    preview: { total: number; protocolos: string[] },
    evidencias: {
      contagemModulos: { module: SistemaModulo; label: string; count: number }[];
    },
  ): { valid: boolean; reason?: string } {
    const normalized = this.normalizeIaText(message);
    if (!normalized || normalized.length < 8) {
      return { valid: false, reason: 'Resposta vazia ou curta demais.' };
    }

    const allowedProtocols = new Set(preview.protocolos.map((p) => p.toUpperCase()));
    const citedProtocols = this.extractProtocolsFromText(message).map((p) => p.toUpperCase());
    if (citedProtocols.some((p) => !allowedProtocols.has(p))) {
      return { valid: false, reason: 'Protocolos citados não batem com o resultado real.' };
    }

    const demandaCountMatch = normalized.match(/(\d+)\s+demandas?/);
    if (demandaCountMatch?.[1]) {
      const cited = Number(demandaCountMatch[1]);
      if (!Number.isNaN(cited) && cited !== preview.total) {
        return {
          valid: false,
          reason: `Contagem de demandas incorreta (citado ${cited}, real ${preview.total}).`,
        };
      }
    }

    const moduleWords: Array<{ module: SistemaModulo; regex: RegExp }> = [
      { module: 'setores', regex: /(\d+)\s+setores?/ },
      { module: 'clientes', regex: /(\d+)\s+clientes?/ },
      { module: 'templates', regex: /(\d+)\s+templates?/ },
      { module: 'usuarios', regex: /(\d+)\s+usuarios?/ },
      { module: 'paginas', regex: /(\d+)\s+paginas?/ },
      { module: 'demandas', regex: /(\d+)\s+demandas?/ },
    ];
    const moduleCountByKey = new Map(evidencias.contagemModulos.map((m) => [m.module, m.count]));
    for (const mw of moduleWords) {
      const m = normalized.match(mw.regex);
      if (!m?.[1]) continue;
      const cited = Number(m[1]);
      const real = moduleCountByKey.get(mw.module);
      if (real != null && !Number.isNaN(cited) && cited !== real) {
        return {
          valid: false,
          reason: `Contagem do módulo ${mw.module} incorreta (citado ${cited}, real ${real}).`,
        };
      }
    }

    return { valid: true };
  }

  private async logIaSearch(event: {
    userId: string;
    query: string;
    scope: IaSearchScope;
    searchMode: PesquisaGeralMode;
    filters: ListDemandasFiltersDto;
    previewTotal: number;
    previewProtocolos: string[];
    moduleCounts: { module: SistemaModulo; label: string; count: number }[];
    success: boolean;
    errorMessage?: string;
    latencyMs: number;
  }): Promise<void> {
    try {
      await this.supabase.getClient().from('ia_busca_log').insert({
        user_id: event.userId,
        query: event.query,
        scope: event.scope,
        search_mode: event.searchMode,
        filters_json: event.filters,
        preview_total: event.previewTotal,
        preview_protocolos: event.previewProtocolos,
        module_counts_json: event.moduleCounts,
        success: event.success,
        error_message: event.errorMessage ?? null,
        latency_ms: event.latencyMs,
      });
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err ?? 'unknown');
      console.warn(`[IA_SEARCH_LOG] falha ao registrar log: ${message}`);
    }
  }

  private async buildNaturalIaAnswer(
    apiKey: string,
    query: string,
    filters: ListDemandasFiltersDto,
    preview: { total: number; protocolos: string[]; exemplos: { protocolo: string; assunto: string; status: string }[] },
    evidencias: {
      modo: PesquisaGeralMode;
      modoLabel: string;
      termoPesquisa: string | null;
      contagemCampos: { key: EvidenciaCampoChave; label: string; count: number }[];
      contagemModulos: { module: SistemaModulo; label: string; count: number }[];
      topMatches: {
        protocolo: string;
        assunto: string;
        campos: { key: EvidenciaCampoChave; label: string; snippet: string }[];
      }[];
      topGlobalMatches: {
        module: SistemaModulo;
        moduleLabel: string;
        title: string;
        snippet: string;
        route: string;
      }[];
    },
    systemContext: {
      totalDemandasVisiveis: number;
      porStatus: Record<string, number>;
      demandasRecentes: { protocolo: string; assunto: string; status: string }[];
      setores: string[];
      clientesAtivos: string[];
      templates: string[];
      paginas: { label: string; url: string; descricao: string }[];
    },
    learnedContext: string,
  ): Promise<string | null> {
    try {
      const openai = new OpenAI({ apiKey });
      let lastReason = '';
      for (let attempt = 1; attempt <= 3; attempt++) {
        const completion = await openai.chat.completions.create({
          model: 'gpt-4o-mini',
          temperature: 0,
          messages: [
            {
              role: 'system',
              content:
                'Você é um assistente de operações. Responda em português natural, direto e útil. ' +
                'Não use frases mecânicas como "Foi solicitado". Não invente dados além do contexto fornecido. ' +
                'Baseie a resposta SOMENTE em "resultado" e "evidencias". "resultado" representa as demandas da tabela filtrada e é a fonte principal da verdade. ' +
                'Use EXATAMENTE os números e protocolos em "resultado". Não altere contagens. ' +
                'Quando a pergunta for de quantidade/listagem (ex.: "quantas demandas temos"), informe o total de "resultado.total" e cite nomes/protocolos de "resultado.exemplos" e "resultado.protocolos". ' +
                'Se "resultado.total" for 0 e não houver "evidencias.topGlobalMatches", diga que não encontrou resultados para o termo. ' +
                'Se "resultado.total" for 0 mas houver "evidencias.topGlobalMatches", explique que não encontrou em demandas, mas encontrou em outros módulos. ' +
                'Quando houver "evidencias.contagemCampos", cite os campos encontrados de forma clara. ' +
                'Quando houver "evidencias.contagemModulos" ou "evidencias.topGlobalMatches", cite os módulos/páginas com clareza. ' +
                'Quando houver "evidencias.topMatches", use isso para justificar a resposta. ' +
                'Não assuma que o termo buscado é usuário/pessoa; trate como termo de busca, a menos que o contexto diga explicitamente isso. ' +
                'Se a pergunta for sobre "onde fazer algo", cite a página e rota exata do sistema. ' +
                'Use no máximo 4 frases curtas. ' +
                'Quando "contextoAlimentado" estiver disponível, use como referência adicional sem contradizer "resultado" e "evidencias".',
            },
            {
              role: 'user',
              content: JSON.stringify({
                pedidoUsuario: query,
                filtrosAplicados: filters,
                resultado: {
                  total: preview.total,
                  protocolos: preview.protocolos,
                  exemplos: preview.exemplos,
                },
                evidencias,
                sistema: systemContext,
                contextoAlimentado: learnedContext || undefined,
                tentativa: attempt,
                feedbackValidacao: lastReason || undefined,
              }),
            },
          ],
        });

        const text = completion.choices?.[0]?.message?.content?.trim() || '';
        if (!text) {
          lastReason = 'Resposta vazia.';
          continue;
        }
        const validation = this.validateGeneratedIaAnswer(text, preview, {
          contagemModulos: evidencias.contagemModulos,
        });
        if (validation.valid) return text;
        lastReason = validation.reason || 'Resposta inconsistente com os dados.';
      }
      return null;
    } catch {
      return null;
    }
  }

  /** Converte busca em linguagem natural em filtros usando IA. Retorna os filtros para o frontend aplicar. */
  async buscarIa(
    userId: string,
    query: string,
    options?: { scope?: string; context?: IaSearchContext },
  ): Promise<{
    filters: ListDemandasFiltersDto;
    message: string;
    links: { label: string; url: string }[];
    preview: { total: number; protocolos: string[] };
    evidence: {
      mode: PesquisaGeralMode;
      modeLabel: string;
      searchTerm: string | null;
      fieldCounts: { key: EvidenciaCampoChave; label: string; count: number }[];
      moduleCounts: { module: SistemaModulo; label: string; count: number }[];
      topMatches: {
        demandaId: string;
        route: string;
        protocolo: string;
        assunto: string;
        fields: { key: EvidenciaCampoChave; label: string; snippet: string }[];
      }[];
      globalMatches: {
        module: SistemaModulo;
        moduleLabel: string;
        title: string;
        snippet: string;
        route: string;
      }[];
    };
  }> {
    const startedAt = Date.now();
    const requestScope = this.normalizeIaScope(options?.scope);
    let searchMode: PesquisaGeralMode = this.parsePesquisaGeralMode(query, requestScope);
    let filtersForLog: ListDemandasFiltersDto = {};
    let previewTotalForLog = 0;
    let previewProtocolsForLog: string[] = [];
    let moduleCountsForLog: { module: SistemaModulo; label: string; count: number }[] = [];

    try {
      const apiKey = process.env.OPENAI_API_KEY;
      if (!apiKey?.trim()) {
        throw new ServiceUnavailableException(
          'Busca por IA não configurada. Defina OPENAI_API_KEY no servidor.',
        );
      }

      searchMode = this.parsePesquisaGeralMode(query, requestScope);
      const { setores, clientes, users } = await this.loadIaReferenceData();

    const statusValues = ['em_aberto', 'em_andamento', 'concluido', 'standby', 'cancelado'];
    const recorrenciaValues = ['diaria', 'semanal', 'quinzenal', 'mensal'];
    const condicaoPrazoValues = ['vencido', 'no_prazo', 'finalizada'];

    const systemPrompt = `Você é um assistente que converte pedidos em português em um JSON para filtrar demandas e devolver um resumo amigável.
Retorne APENAS JSON válido, sem markdown e sem texto fora do JSON.
Formato obrigatório:
{
  "filters": {
    "clienteId": "UUID opcional",
    "assunto": "string opcional",
    "status": "um de ${statusValues.join(', ')}",
    "tipoRecorrencia": "um de ${recorrenciaValues.join(', ')}",
    "protocolo": "string opcional",
    "prioridade": true/false,
    "criadorId": "UUID opcional",
    "responsavelPrincipalId": "UUID opcional",
    "responsavelApenasPrincipal": true/false,
    "setorIds": ["UUID", "..."],
    "condicaoPrazo": "um de ${condicaoPrazoValues.join(', ')}",
    "pesquisarTarefaOuObservacao": "string opcional",
    "pesquisaGeral": "texto opcional para buscar em todos os campos básicos (inclui observações gerais, observações e recorrência)",
    "dataCriacaoDe": "YYYY-MM-DD",
    "dataCriacaoAte": "YYYY-MM-DD",
    "prazoDe": "YYYY-MM-DD",
    "prazoAte": "YYYY-MM-DD"
  },
  "message": "explicação curta em português do que foi entendido",
  "links": [
    { "label": "texto do link", "url": "/rota-interna-ou-https://..." }
  ]
}
Para clienteId, criadorId, responsavelPrincipalId e setorIds use SOMENTE IDs da lista abaixo. Não invente IDs.
Em links, use preferência por rotas internas do sistema (ex.: /demandas, /cadastros, /dashboard).

Setores disponíveis (id, name):
${JSON.stringify(setores)}

Clientes disponíveis (id, name):
${JSON.stringify(clientes)}

Usuários disponíveis (id, name):
${JSON.stringify(users)}

Se o usuário mencionar um nome (ex: "Comercial", "João"), use o id correspondente. Se não houver correspondência, omita o campo.
Quando o pedido citar observações gerais, observações, texto livre, campos básicos completos ou datas específicas (ex: 31/12/2024), preencha "pesquisaGeral" com o termo-chave.
Se o pedido disser "apenas observação geral", "somente status" ou "qualquer campo", respeite isso no texto da resposta e nos filtros.
Só preencha "prioridade" quando o usuário mencionar explicitamente prioridade/urgência (ou ausência delas). Nunca defina prioridade por padrão.
Se não conseguir extrair filtros, retorne filters como {} e explique isso no campo "message".`;

    const openai = new OpenAI({ apiKey });
    let parsed: Record<string, unknown> = {};
    try {
      const completion = await openai.chat.completions.create({
        model: 'gpt-4o-mini',
        messages: [
          { role: 'system', content: systemPrompt },
          { role: 'user', content: query.trim() },
        ],
        temperature: 0,
      });
      const content = completion.choices[0]?.message?.content?.trim() || '{}';
      const cleaned = content.replace(/^```json\s*/i, '').replace(/\s*```$/i, '').trim();
      parsed = JSON.parse(cleaned);
    } catch {
      parsed = {};
    }

    const rawFilters =
      parsed.filters && typeof parsed.filters === 'object' && !Array.isArray(parsed.filters)
        ? (parsed.filters as Record<string, unknown>)
        : parsed;

    const validSetorIds = new Set(setores.map((s: any) => s.id));
    const validClienteIds = new Set(clientes.map((c: any) => c.id));
    const validUserIds = new Set(users.map((u: any) => u.id));
    const prioridadeExplicitamenteMencionada = this.queryMentionsPrioridade(query);
    const statusExplicitamenteMencionado = this.queryMentionsStatus(query);

    const filters: ListDemandasFiltersDto = {};
    if (rawFilters.clienteId && validClienteIds.has(rawFilters.clienteId as string)) filters.clienteId = rawFilters.clienteId as string;
    if (typeof rawFilters.assunto === 'string' && rawFilters.assunto.trim()) filters.assunto = rawFilters.assunto.trim();
    if (statusExplicitamenteMencionado && rawFilters.status && statusValues.includes(rawFilters.status as string)) {
      filters.status = rawFilters.status as string;
    }
    if (rawFilters.tipoRecorrencia && recorrenciaValues.includes(rawFilters.tipoRecorrencia as string)) filters.tipoRecorrencia = rawFilters.tipoRecorrencia as string;
    if (typeof rawFilters.protocolo === 'string' && rawFilters.protocolo.trim()) filters.protocolo = rawFilters.protocolo.trim();
    if (prioridadeExplicitamenteMencionada && rawFilters.prioridade === true) filters.prioridade = true;
    if (prioridadeExplicitamenteMencionada && rawFilters.prioridade === false) filters.prioridade = false;
    if (rawFilters.criadorId && validUserIds.has(rawFilters.criadorId as string)) filters.criadorId = rawFilters.criadorId as string;
    if (rawFilters.responsavelPrincipalId && validUserIds.has(rawFilters.responsavelPrincipalId as string)) filters.responsavelPrincipalId = rawFilters.responsavelPrincipalId as string;
    if (filters.responsavelPrincipalId && rawFilters.responsavelApenasPrincipal === true) filters.responsavelApenasPrincipal = true;
    if (Array.isArray(rawFilters.setorIds)) {
      const ids = (rawFilters.setorIds as string[]).filter((id) => validSetorIds.has(id));
      if (ids.length) filters.setorIds = ids;
    }
    if (rawFilters.condicaoPrazo && condicaoPrazoValues.includes(rawFilters.condicaoPrazo as string)) {
      filters.condicaoPrazo = rawFilters.condicaoPrazo as 'vencido' | 'no_prazo' | 'finalizada';
    }
    if (typeof rawFilters.pesquisarTarefaOuObservacao === 'string' && rawFilters.pesquisarTarefaOuObservacao.trim()) {
      filters.pesquisarTarefaOuObservacao = rawFilters.pesquisarTarefaOuObservacao.trim();
    }
    const rawPesquisaGeral = typeof rawFilters.pesquisaGeral === 'string' ? rawFilters.pesquisaGeral : undefined;
    const normalizedPesquisaGeral = this.normalizePesquisaGeralFromQuery(query, rawPesquisaGeral);
    if (normalizedPesquisaGeral) filters.pesquisaGeral = normalizedPesquisaGeral;
    if (typeof rawFilters.dataCriacaoDe === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(rawFilters.dataCriacaoDe)) filters.dataCriacaoDe = rawFilters.dataCriacaoDe;
    if (typeof rawFilters.dataCriacaoAte === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(rawFilters.dataCriacaoAte)) filters.dataCriacaoAte = rawFilters.dataCriacaoAte;
    if (typeof rawFilters.prazoDe === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(rawFilters.prazoDe)) filters.prazoDe = rawFilters.prazoDe;
    if (typeof rawFilters.prazoAte === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(rawFilters.prazoAte)) filters.prazoAte = rawFilters.prazoAte;

    if (!Object.keys(filters).length) {
      const heuristicFilters = this.extractHeuristicIaFilters(query, setores, clientes, users);
      Object.assign(filters, heuristicFilters);
    }
    const normalizedPesquisaAfterHeuristic = this.normalizePesquisaGeralFromQuery(query, filters.pesquisaGeral);
    if (normalizedPesquisaAfterHeuristic) filters.pesquisaGeral = normalizedPesquisaAfterHeuristic;
    if (searchMode === 'status_only' && !filters.pesquisaGeral && filters.status) {
      filters.pesquisaGeral = this.statusLabelPt(filters.status);
    }
    if (searchMode === 'status_only') {
      delete filters.status;
    }
    Object.assign(filters, this.applyIaContextToFilters(query, filters, options?.context));
    const shouldSearchDemandas =
      requestScope === 'all' ||
      requestScope === 'demandas' ||
      requestScope === 'observacoes_gerais' ||
      requestScope === 'status';
    filtersForLog = { ...filters };

    const links: { label: string; url: string }[] = [];
    if (Array.isArray(parsed.links)) {
      for (const link of parsed.links.slice(0, 6)) {
        if (!link || typeof link !== 'object') continue;
        const label = typeof (link as any).label === 'string' ? (link as any).label.trim() : '';
        const url = typeof (link as any).url === 'string' ? this.sanitizeIaLinkUrl((link as any).url) : null;
        if (!label || !url) continue;
        if (links.some((l) => l.url === url)) continue;
        links.push({ label, url });
      }
    }

    const demandasLink = this.buildDemandasLinkFromFilters(filters);
    if (!links.some((l) => l.url === demandasLink)) {
      links.unshift({
        label: Object.keys(filters).length ? 'Abrir demandas com esses filtros' : 'Abrir lista de demandas',
        url: demandasLink,
      });
    }
    for (const inferred of this.inferLinksFromQuery(query)) {
      if (!links.some((l) => l.url === inferred.url)) links.push(inferred);
    }

    let pesquisaEvidence: PesquisaGeralResult | undefined;
    let previewResult: { data: any[]; total: number } = { data: [], total: 0 };
    if (shouldSearchDemandas) {
      const resolved = await this.resolveFilteredIds(userId, filters, searchMode);
      pesquisaEvidence = resolved.pesquisaEvidence;
      previewResult = await this.listByIds(resolved.ids, filters, resolved.pesquisaEvidence?.scoreByDemanda);
    }
    const protocolos = (previewResult.data ?? [])
      .map((d: any) => d?.protocolo)
      .filter((p: unknown): p is string => typeof p === 'string')
      .slice(0, 3);
    const exemplos = (previewResult.data ?? [])
      .slice(0, 3)
      .map((d: any) => ({ protocolo: d?.protocolo ?? '—', assunto: d?.assunto ?? '—', status: d?.status ?? '—' }));

    const fieldCounts = [...(pesquisaEvidence?.contagemCampos ?? new Map()).entries()]
      .map(([key, value]) => ({ key, label: value.label, count: value.count }))
      .sort((a, b) => b.count - a.count);
    const evidenceByDemanda = pesquisaEvidence?.evidenciasByDemanda ?? new Map<string, EvidenciaDemanda>();
    const topMatches = (previewResult.data ?? [])
      .slice(0, 5)
      .map((d: any) => {
        const found = evidenceByDemanda.get(String(d?.id ?? ''));
        return {
          demandaId: String(d?.id ?? ''),
          route: `/demandas/${String(d?.id ?? '')}`,
          protocolo: String(d?.protocolo ?? '—'),
          assunto: String(d?.assunto ?? '—'),
          fields: (found?.matchedFields ?? []).map((f) => ({ key: f.key, label: f.label, snippet: f.snippet })),
        };
      });
    previewTotalForLog = previewResult.total;
    previewProtocolsForLog = protocolos;

    const demandaGlobalMatches: GlobalMatchItem[] = topMatches.map((m) => ({
      module: 'demandas',
      moduleLabel: this.globalModuleLabel('demandas'),
      title: `${m.protocolo} - ${m.assunto}`,
      snippet: m.fields.length
        ? this.sanitizeEvidenceSnippet(m.fields.map((f) => `${f.label}: ${f.snippet}`).join(' | '))
        : 'Resultado encontrado em demandas.',
      route: m.route || demandasLink,
    }));
    const globalEvidence = await this.buildGlobalSystemEvidence(
      query,
      searchMode,
      requestScope,
      filters.pesquisaGeral?.trim() || null,
      previewResult.total,
      demandaGlobalMatches,
    );
    moduleCountsForLog = globalEvidence.moduleCounts;
    for (const item of globalEvidence.globalMatches) {
      if (!links.some((l) => l.url === item.route)) {
        links.push({ label: `Abrir ${item.moduleLabel}`, url: item.route });
      }
    }

    const evidencePayload = {
      modo: searchMode,
      modoLabel: this.pesquisaGeralModeLabel(searchMode),
      termoPesquisa: filters.pesquisaGeral?.trim() || null,
      contagemCampos: fieldCounts,
      contagemModulos: globalEvidence.moduleCounts,
      topMatches: topMatches.map((x) => ({
        demandaId: x.demandaId,
        route: x.route,
        protocolo: x.protocolo,
        assunto: x.assunto,
        campos: x.fields,
      })),
      topGlobalMatches: globalEvidence.globalMatches,
    };

    const systemContext = await this.buildSystemContextForIa(userId);
    const learnedContext = this.iaContextService.buildRelevantContext(
      query,
      this.parsePesquisaGeralMode(query, requestScope) === 'status_only'
        ? 'conferencia_mensagens'
        : 'filtros_demandas',
    );
    const natural = await this.buildNaturalIaAnswer(apiKey, query, filters, {
      total: previewResult.total,
      protocolos,
      exemplos,
    }, evidencePayload, systemContext, learnedContext);
    if (!natural) {
      throw new ServiceUnavailableException('Não foi possível gerar resposta da IA neste momento. Tente novamente.');
    }

    const response = {
      filters,
      message: natural,
      links,
      preview: { total: previewResult.total, protocolos },
      evidence: {
        mode: searchMode,
        modeLabel: this.pesquisaGeralModeLabel(searchMode),
        searchTerm: filters.pesquisaGeral?.trim() || null,
        fieldCounts,
        moduleCounts: globalEvidence.moduleCounts,
        topMatches,
        globalMatches: globalEvidence.globalMatches,
      },
    };
    await this.logIaSearch({
      userId,
      query,
      scope: requestScope,
      searchMode,
      filters: filtersForLog,
      previewTotal: previewTotalForLog,
      previewProtocolos: previewProtocolsForLog,
      moduleCounts: moduleCountsForLog,
      success: true,
      latencyMs: Date.now() - startedAt,
    });
    return response;
    } catch (error) {
      await this.logIaSearch({
        userId,
        query,
        scope: requestScope,
        searchMode,
        filters: filtersForLog,
        previewTotal: previewTotalForLog,
        previewProtocolos: previewProtocolsForLog,
        moduleCounts: moduleCountsForLog,
        success: false,
        errorMessage: error instanceof Error ? error.message : String(error ?? 'Erro desconhecido'),
        latencyMs: Date.now() - startedAt,
      });
      throw error;
    }
  }

  /** Dashboard KPIs para usuário master: métricas de tempo de resolução e atualização, e opcional avaliação por IA. */
  async getDashboardKpis(userId: string, avaliarComIa: boolean): Promise<{
    metricas: {
      totalDemandas: number;
      concluidas: number;
      emAberto: number;
      tempoMedioResolucaoHoras: number | null;
      demandasSemObservacaoRecente: number;
      tempoMedioDesdeUltimaObservacaoHoras: number | null;
      porStatus: Record<string, number>;
    };
    avaliacaoIa?: { resumo: string; kpis: { nome: string; situacao: 'ok' | 'desconto_leve' | 'desconto_grave'; comentario: string }[] };
  }> {
    void userId;
    let metricas = await this.loadDashboardKpisViaRpc();
    if (!metricas) {
      const sb = this.supabase.getClient();
      const { data: rows } = await sb
        .from('Demanda')
        .select('id, status, created_at, updated_at, resolvido_em, ultima_observacao_em')
        .order('created_at', { ascending: false })
        .limit(5000);
      const list = rows ?? [];

      const concluidas = list.filter((r: any) => r.status === 'concluido');
      const comResolvidoEm = concluidas
        .filter((r: any) => r.resolvido_em);
      const temposResolucao = comResolvidoEm
        .map((r: any) => computeTempoHoras(r.created_at, r.resolvido_em))
        .filter((x): x is number => x != null);
      const tempoMedioResolucaoHoras = temposResolucao.length
        ? temposResolucao.reduce((a, b) => a + b, 0) / temposResolucao.length
        : null;

      const comUltimaObs = list.filter((r: any) => r.ultima_observacao_em);
      const agora = new Date().toISOString();
      const temposDesdeObs = comUltimaObs
        .map((r: any) => computeTempoHoras(r.ultima_observacao_em, agora))
        .filter((x): x is number => x != null);
      const tempoMedioDesdeUltimaObservacaoHoras = temposDesdeObs.length
        ? temposDesdeObs.reduce((a, b) => a + b, 0) / temposDesdeObs.length
        : null;
      const demandasSemObservacaoRecente = list.filter((r: any) => {
        const ultima = r.ultima_observacao_em as string | null | undefined;
        if (ultima == null) return true;
        return (computeTempoHoras(ultima, agora) ?? 0) > 24 * 7;
      }).length;

      const porStatus: Record<string, number> = {};
      list.forEach((r: any) => { porStatus[r.status] = (porStatus[r.status] || 0) + 1; });

      metricas = {
        totalDemandas: list.length,
        concluidas: concluidas.length,
        emAberto: list.filter((r: any) => r.status === 'em_aberto').length,
        tempoMedioResolucaoHoras: tempoMedioResolucaoHoras != null ? Math.round(tempoMedioResolucaoHoras * 10) / 10 : null,
        demandasSemObservacaoRecente,
        tempoMedioDesdeUltimaObservacaoHoras:
          tempoMedioDesdeUltimaObservacaoHoras != null
            ? Math.round(tempoMedioDesdeUltimaObservacaoHoras * 10) / 10
            : null,
        porStatus,
      };
    }

    let avaliacaoIa: { resumo: string; kpis: { nome: string; situacao: 'ok' | 'desconto_leve' | 'desconto_grave'; comentario: string }[] } | undefined;
    if (avaliarComIa && process.env.OPENAI_API_KEY) {
      const kpiTable = `SUGESTÕES KPIs (referência):
- Envio faturas/cobranças/NF clientes no prazo: desconto leve até 24h após; grave >24h.
- Cobranças inadimplentes: leve 5/10 dias e bloqueios mensais; grave >1 mês.
- Tempo médio atendimento suporte técnico: leve 8–10 min; grave >15 min.
- Tempo médio atendimento: leve 8–10 min; grave >10 min.
- Envio contratos: leve 24–36h úteis; grave >36h.
- Solicitações fidelidades/operadoras: leve 24–36h úteis; grave >36h.
- Acompanhamento assinaturas: leve 24–36h; grave >36h.
- Avaliação atendimento: leve média 6–7; grave <6.
- Cadastro/ajustes linhas até sexta: leve até segunda; grave após segunda.
- Entrega relatórios no prazo: leve até 24h após; grave >5 dias.
- Documentação operadoras: leve sem erro com custo; grave ≥1 erro.
- Envio faturas (Assessoria/Fixo): leve até 24h após; grave >48h.
- Atualização Vivo Gestão: leve 24–36h; grave >24h.
- Atendimento parceiros: leve 8–10 min; grave >15 min.
- Cálculo cobrança: leve envio com <10 dias até vencimento; grave <5 dias.
- Contestação/acompanhamento operadoras: leve 24h após; grave >5 dias.`;

      const openai = new OpenAI({ apiKey: process.env.OPENAI_API_KEY });
      const completion = await openai.chat.completions.create({
        model: 'gpt-4o-mini',
        messages: [
          {
            role: 'system',
            content: `Você avalia indicadores operacionais com base em métricas de demandas. Retorne um JSON com: "resumo" (string, um parágrafo em português) e "kpis" (array de objetos com "nome" (string), "situacao" ("ok" | "desconto_leve" | "desconto_grave"), "comentario" (string)). Use a tabela de referência para comparar. Se não houver dado suficiente para um KPI, use situacao "ok" e comentario explicando. Retorne APENAS o JSON, sem markdown.`,
          },
          {
            role: 'user',
            content: `${kpiTable}\n\nMétricas atuais do sistema:\n${JSON.stringify(metricas, null, 2)}\n\nAvalie e retorne o JSON com resumo e kpis.`,
          },
        ],
        temperature: 0.3,
      });
      const content = completion.choices[0]?.message?.content?.trim() || '{}';
      try {
        const cleaned = content.replace(/^```json\s*/i, '').replace(/\s*```$/i, '').trim();
        const parsed = JSON.parse(cleaned);
        avaliacaoIa = {
          resumo: parsed.resumo || 'Avaliação gerada.',
          kpis: Array.isArray(parsed.kpis) ? parsed.kpis.map((k: any) => ({
            nome: k.nome || '',
            situacao: ['ok', 'desconto_leve', 'desconto_grave'].includes(k.situacao) ? k.situacao : 'ok',
            comentario: k.comentario || '',
          })) : [],
        };
      } catch {
        avaliacaoIa = { resumo: 'Não foi possível gerar avaliação.', kpis: [] };
      }
    }

    return { metricas, avaliacaoIa };
  }
}
