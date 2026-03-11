import { Injectable, ForbiddenException, NotFoundException, ServiceUnavailableException } from '@nestjs/common';
import OpenAI from 'openai';
import { SupabaseService } from '../supabase/supabase.service';
import * as fs from 'fs';
import * as path from 'path';
import { v4 as uuidv4 } from 'uuid';
import { DemandaVisibilityService } from './demanda-visibility.service';
import { RecorrenciaService } from './recorrencia.service';
import { TemplatesService } from '../templates/templates.service';
import { CreateDemandaDto } from './dto/create-demanda.dto';
import { UpdateDemandaDto } from './dto/update-demanda.dto';
import { ListDemandasFiltersDto } from './dto/list-demandas-filters.dto';
import { CreateDemandaFromTemplateDto } from '../templates/dto/create-demanda-from-template.dto';
import type { DemandaStatus } from '../types/enums';

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

@Injectable()
export class DemandasService {
  constructor(
    private supabase: SupabaseService,
    private visibility: DemandaVisibilityService,
    private recorrencia: RecorrenciaService,
    private templatesService: TemplatesService,
  ) {}

  private async gerarProtocolo(): Promise<string> {
    const sb = this.supabase.getClient();
    const year = new Date().getFullYear();
    const start = `${year}-01-01`;
    const end = `${year + 1}-01-01`;
    const { count } = await sb.from('Demanda').select('*', { count: 'exact', head: true }).gte('created_at', start).lt('created_at', end);
    return `LUX-${year}-${String((count ?? 0) + 1).padStart(5, '0')}`;
  }

  private async loadDemandaRelations(demandaId: string, detail = false) {
    const sb = this.supabase.getClient();
    const [criador, responsaveis, setores, clientes, subtarefas, observacoes, anexos, recorrenciaConfig] = await Promise.all([
      sb.from('Demanda').select('criador_id').eq('id', demandaId).single().then(async (r) => {
        if (!r.data?.criador_id) return null;
        const u = await sb.from('User').select('id, name, email').eq('id', r.data.criador_id).single();
        return u.data;
      }),
      sb.from('demanda_responsavel').select('user_id, is_principal').eq('demanda_id', demandaId).then(async (r) => {
        const list = r.data ?? [];
        if (!list.length) return [];
        const u = await sb.from('User').select('id, name, email').in('id', list.map((x: any) => x.user_id));
        const userMap = new Map((u.data ?? []).map((x: any) => [x.id, x]));
        return list.map((p: any) => ({ userId: p.user_id, isPrincipal: p.is_principal, user: userMap.get(p.user_id) }));
      }),
      sb.from('demanda_setor').select('setor_id').eq('demanda_id', demandaId).then(async (r) => {
        const ids = (r.data ?? []).map((x: any) => x.setor_id);
        if (!ids.length) return [];
        const s = await sb.from('Setor').select('id, name, slug').in('id', ids);
        return (s.data ?? []).map((x: any) => ({ setor: x }));
      }),
      sb.from('demanda_cliente').select('cliente_id').eq('demanda_id', demandaId).then(async (r) => {
        const ids = (r.data ?? []).map((x: any) => x.cliente_id);
        if (!ids.length) return [];
        const c = await sb.from('Cliente').select('id, name').in('id', ids);
        return (c.data ?? []).map((x: any) => ({ cliente: x }));
      }),
      detail ? sb.from('subtarefa').select('*').eq('demanda_id', demandaId).order('ordem', { ascending: true }).order('id', { ascending: true }) : Promise.resolve({ data: [] }),
      detail ? sb.from('observacao').select('*').eq('demanda_id', demandaId).order('created_at', { ascending: false }).then(async (r) => {
        const list = r.data ?? [];
        if (!list.length) return { data: [] };
        const userIds = [...new Set(list.map((o: any) => o.user_id))];
        const { data: users } = await sb.from('User').select('id, name').in('id', userIds);
        const userMap = new Map((users ?? []).map((u: any) => [u.id, u]));
        return {
          data: list.map((o: any) => {
            const u = userMap.get(o.user_id);
            return {
              id: o.id,
              texto: o.texto ?? '',
              createdAt: toDateISO(o.created_at) ?? undefined,
              user: u ? { id: u.id, name: u.name } : undefined,
            };
          }),
        };
      }) : Promise.resolve({ data: [] }),
      detail ? sb.from('anexo').select('*').eq('demanda_id', demandaId) : Promise.resolve({ data: [] }),
      detail ? sb.from('recorrencia_config').select('*').eq('demanda_id', demandaId).single() : Promise.resolve({ data: null }),
    ]);
    return {
      criador,
      responsaveis,
      setores,
      clientes,
      subtarefas: detail ? (subtarefas as any).data ?? [] : [],
      observacoes: detail ? (observacoes as any).data ?? [] : [],
      anexos: detail ? (anexos as any).data ?? [] : [],
      recorrenciaConfig: detail ? (recorrenciaConfig as any).data : null,
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
        is_recorrente: dto.isRecorrente ?? false,
      })
      .select()
      .single();
    if (error) throw new Error(error.message);
    if (dto.setores?.length) await sb.from('demanda_setor').insert(dto.setores.map((setorId) => ({ demanda_id: demanda.id, setor_id: setorId })));
    if (dto.clienteIds?.length) await sb.from('demanda_cliente').insert(dto.clienteIds.map((clienteId) => ({ demanda_id: demanda.id, cliente_id: clienteId })));
    if (dto.responsaveis?.length) await sb.from('demanda_responsavel').insert(dto.responsaveis.map((r) => ({ demanda_id: demanda.id, user_id: r.userId, is_principal: r.isPrincipal ?? false })));
    if (dto.subtarefas?.length) await sb.from('subtarefa').insert(dto.subtarefas.map((t, i) => ({ demanda_id: demanda.id, titulo: t.titulo, ordem: (t as any).ordem ?? i })));
    if (dto.isRecorrente && dto.recorrencia) {
      await sb.from('recorrencia_config').insert({
        demanda_id: demanda.id,
        data_base: dto.recorrencia.dataBase,
        tipo: dto.recorrencia.tipo,
        prazo_reabertura_dias: dto.recorrencia.prazoReaberturaDias ?? 0,
      });
    }
    const rel = await this.loadDemandaRelations(demanda.id, false);
    return mapDemandaList(demanda, rel.criador, rel.responsaveis, rel.setores, rel.clientes);
  }

  async createFromTemplate(userId: string, templateId: string, dto: CreateDemandaFromTemplateDto) {
    const template = await this.templatesService.getForDemanda(templateId) as any;
    const protocolo = await this.gerarProtocolo();
    const prioridade = dto.prioridade ?? template.prioridadeDefault;
    const observacoesGerais = dto.observacoesGerais ?? template.observacoesGeraisTemplate ?? undefined;
    const isRecorrente = !!dto.recorrenciaDataBase && !!template.isRecorrenteDefault && !!template.recorrenciaTipo;
    const setorIds = (dto.setorIds?.length ? dto.setorIds : template.setores?.map((s: any) => s.setor?.id ?? s.setorId)?.filter(Boolean)) ?? [];
    const responsaveisDto = (dto.responsaveis?.length ? dto.responsaveis : template.responsaveis?.map((r: any) => ({ userId: r.userId ?? r.user?.id, isPrincipal: r.isPrincipal ?? false }))) ?? [];
    const subtarefasTemplate = (dto.subtarefas?.length ? dto.subtarefas : template.subtarefas?.map((t: any) => ({ titulo: t.titulo ?? t.titulo }))) ?? [];

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
        is_recorrente: isRecorrente,
      })
      .select()
      .single();
    if (error) throw new Error(error.message);

    if (setorIds.length) await sb.from('demanda_setor').insert(setorIds.map((setorId: string) => ({ demanda_id: demanda.id, setor_id: setorId })));
    if (dto.clienteIds?.length) await sb.from('demanda_cliente').insert(dto.clienteIds.map((clienteId) => ({ demanda_id: demanda.id, cliente_id: clienteId })));
    if (responsaveisDto.length) await sb.from('demanda_responsavel').insert(responsaveisDto.map((r: any) => ({ demanda_id: demanda.id, user_id: r.userId, is_principal: r.isPrincipal ?? false })));
    if (subtarefasTemplate.length) await sb.from('subtarefa').insert(subtarefasTemplate.map((t: any, i: number) => ({ demanda_id: demanda.id, titulo: t.titulo, ordem: i })));
    if (isRecorrente && dto.recorrenciaDataBase && template.recorrenciaTipo) {
      await sb.from('recorrencia_config').insert({
        demanda_id: demanda.id,
        data_base: dto.recorrenciaDataBase,
        tipo: template.recorrenciaTipo,
        prazo_reabertura_dias: template.recorrenciaPrazoReaberturaDias ?? 0,
      });
    }
    const rel = await this.loadDemandaRelations(demanda.id, false);
    return mapDemandaList(demanda, rel.criador, rel.responsaveis, rel.setores, rel.clientes);
  }

  async list(userId: string, filters: ListDemandasFiltersDto) {
    let ids = await this.visibility.visibleDemandaIds(userId);
    const sb = this.supabase.getClient();

    if (filters.clienteId) {
      const { data } = await sb.from('demanda_cliente').select('demanda_id').eq('cliente_id', filters.clienteId);
      const clienteIds = new Set((data ?? []).map((d: any) => d.demanda_id));
      ids = ids.filter((id) => clienteIds.has(id));
    }
    if (filters.responsavelPrincipalId) {
      const { data } = await sb.from('demanda_responsavel').select('demanda_id').eq('user_id', filters.responsavelPrincipalId).eq('is_principal', true);
      const set = new Set((data ?? []).map((d: any) => d.demanda_id));
      ids = ids.filter((id) => set.has(id));
    }
    if (filters.setorIds?.length) {
      const { data } = await sb.from('demanda_setor').select('demanda_id').in('setor_id', filters.setorIds);
      const set = new Set((data ?? []).map((d: any) => d.demanda_id));
      ids = ids.filter((id) => set.has(id));
    }
    if (filters.tipoRecorrencia) {
      const { data: recIds } = await sb.from('recorrencia_config').select('demanda_id').eq('tipo', filters.tipoRecorrencia);
      const recSet = new Set((recIds ?? []).map((d: any) => d.demanda_id));
      ids = ids.filter((id) => recSet.has(id));
    }
    if (filters.pesquisarTarefaOuObservacao) {
      const term = `%${filters.pesquisarTarefaOuObservacao}%`;
      const [sub, obs] = await Promise.all([
        sb.from('subtarefa').select('demanda_id').ilike('titulo', term),
        sb.from('observacao').select('demanda_id').ilike('texto', term),
      ]);
      const set = new Set([...(sub.data ?? []).map((d: any) => d.demanda_id), ...(obs.data ?? []).map((d: any) => d.demanda_id)]);
      ids = ids.filter((id) => set.has(id));
    }

    if (ids.length === 0) return { data: [], total: 0 };

    let q = sb.from('Demanda').select('*', { count: 'exact' }).in('id', ids).order('created_at', { ascending: false }).range(0, 99);
    if (filters.assunto) q = q.ilike('assunto', `%${filters.assunto}%`);
    if (filters.status) q = q.eq('status', filters.status);
    if (filters.protocolo) q = q.ilike('protocolo', `%${filters.protocolo}%`);
    if (filters.prioridade !== undefined) q = q.eq('prioridade', filters.prioridade);
    if (filters.criadorId) q = q.eq('criador_id', filters.criadorId);
    if (filters.prazoDe) q = q.gte('prazo', filters.prazoDe);
    if (filters.prazoAte) q = q.lte('prazo', filters.prazoAte);
    if (filters.condicaoPrazo === 'vencido') q = q.lt('prazo', new Date().toISOString().slice(0, 10));
    if (filters.condicaoPrazo === 'no_prazo') q = q.gte('prazo', new Date().toISOString().slice(0, 10));
    if (filters.condicaoPrazo === 'finalizada') q = q.eq('status', 'concluido');
    if (filters.dataCriacaoDe) q = q.gte('created_at', filters.dataCriacaoDe);
    if (filters.dataCriacaoAte) q = q.lte('created_at', filters.dataCriacaoAte);

    const { data: rows, count: total } = await q;
    const data = [];
    for (const row of rows ?? []) {
      const rel = await this.loadDemandaRelations(row.id, false);
      data.push(mapDemandaList(row, rel.criador, rel.responsaveis, rel.setores, rel.clientes));
    }
    return { data, total: total ?? 0 };
  }

  async findOne(userId: string, id: string) {
    const can = await this.visibility.canViewDemanda(userId, id);
    if (!can) throw new ForbiddenException('Sem permissão para ver esta demanda');
    const sb = this.supabase.getClient();
    const { data: row } = await sb.from('Demanda').select('*').eq('id', id).single();
    if (!row) throw new NotFoundException('Demanda não encontrada');
    const rel = await this.loadDemandaRelations(id, true);
    const rec = rel.recorrenciaConfig as { data_base?: unknown; tipo?: string; prazo_reabertura_dias?: number } | null;
    return {
      ...mapDemandaList(row, rel.criador, rel.responsaveis, rel.setores, rel.clientes),
      subtarefas: rel.subtarefas,
      observacoes: rel.observacoes,
      anexos: rel.anexos,
      recorrenciaConfig: rec
        ? { dataBase: toDateISO(rec.data_base) ?? (typeof rec.data_base === 'string' ? rec.data_base : null), tipo: rec.tipo ?? '', prazoReaberturaDias: rec.prazo_reabertura_dias ?? 0 }
        : null,
    };
  }

  async update(userId: string, id: string, dto: UpdateDemandaDto) {
    await this.findOne(userId, id);
    const isResponsavel = await this.isResponsavelPrincipal(userId, id);
    let newStatus = dto.status as DemandaStatus | undefined;
    if (newStatus && !isResponsavel && newStatus !== 'pendente_de_resposta') newStatus = 'pendente_de_resposta';

    const sb = this.supabase.getClient();
    const upd: any = {};
    if (dto.assunto != null) upd.assunto = dto.assunto;
    if (dto.prioridade !== undefined) upd.prioridade = dto.prioridade;
    if (dto.prazo != null) upd.prazo = dto.prazo;
    if (newStatus) {
      upd.status = newStatus;
      if (newStatus === 'concluido') upd.resolvido_em = new Date().toISOString();
    }
    if (dto.observacoesGerais !== undefined) upd.observacoes_gerais = dto.observacoesGerais;
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
    if (dto.subtarefas) {
      await sb.from('subtarefa').delete().eq('demanda_id', id);
      if (dto.subtarefas.length) await sb.from('subtarefa').insert(dto.subtarefas.map((t, i) => ({ demanda_id: id, titulo: t.titulo, concluida: t.concluida ?? false, ordem: (t as any).ordem ?? i })));
    }
    return this.findOne(userId, id);
  }

  /** Exclui a demanda (apenas admin). Relacionamentos são removidos em cascata. */
  async remove(userId: string, id: string) {
    await this.findOne(userId, id);
    const sb = this.supabase.getClient();
    const { error } = await sb.from('Demanda').delete().eq('id', id);
    if (error) throw new Error(error.message);
    return { id };
  }

  async addObservacao(userId: string, demandaId: string, texto: string) {
    await this.findOne(userId, demandaId);
    const sb = this.supabase.getClient();
    const isResponsavel = await this.isResponsavelPrincipal(userId, demandaId);
    await sb.from('observacao').insert({ demanda_id: demandaId, user_id: userId, texto });
    const demandaUpd: { status?: string; ultima_observacao_em: string } = { ultima_observacao_em: new Date().toISOString() };
    if (!isResponsavel) demandaUpd.status = 'pendente_de_resposta';
    await sb.from('Demanda').update(demandaUpd).eq('id', demandaId);
    return this.findOne(userId, demandaId);
  }

  async addAnexo(userId: string, demandaId: string, file: Express.Multer.File) {
    await this.findOne(userId, demandaId);
    const uploadDir = process.env.UPLOAD_DIR || './uploads';
    const dir = path.join(uploadDir, 'demandas', demandaId);
    fs.mkdirSync(dir, { recursive: true });
    const safeName = `${uuidv4()}-${(file.originalname || 'file').replace(/[^a-zA-Z0-9._-]/g, '_')}`;
    const storagePath = path.join('demandas', demandaId, safeName);
    const fullPath = path.join(uploadDir, storagePath);
    fs.writeFileSync(fullPath, file.buffer);
    const sb = this.supabase.getClient();
    const { data, error } = await sb
      .from('anexo')
      .insert({
        demanda_id: demandaId,
        filename: file.originalname || 'file',
        mime_type: file.mimetype || 'application/octet-stream',
        size: file.size,
        storage_path: storagePath,
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
    const uploadDir = process.env.UPLOAD_DIR || './uploads';
    const fullPath = path.resolve(path.join(uploadDir, anexo.storage_path));
    if (!fs.existsSync(fullPath)) throw new NotFoundException('Arquivo não encontrado');
    return { path: fullPath, filename: anexo.filename, mimeType: anexo.mime_type };
  }

  private async isResponsavelPrincipal(userId: string, demandaId: string): Promise<boolean> {
    const { data } = await this.supabase.getClient().from('demanda_responsavel').select('id').eq('demanda_id', demandaId).eq('user_id', userId).eq('is_principal', true).limit(1);
    return !!data?.length;
  }

  async exportExcel(userId: string, filters: ListDemandasFiltersDto) {
    let ids = await this.visibility.visibleDemandaIds(userId);
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
    const result = [];
    for (const row of rows ?? []) {
      const rel = await this.loadDemandaRelations(row.id, false);
      result.push(mapDemandaList(row, rel.criador, rel.responsaveis, rel.setores, rel.clientes));
    }
    return result;
  }

  /** Converte busca em linguagem natural em filtros usando IA. Retorna os filtros para o frontend aplicar. */
  async buscarIa(userId: string, query: string): Promise<{ filters: ListDemandasFiltersDto }> {
    const apiKey = process.env.OPENAI_API_KEY;
    if (!apiKey?.trim()) {
      throw new ServiceUnavailableException(
        'Busca por IA não configurada. Defina OPENAI_API_KEY no servidor.',
      );
    }

    const sb = this.supabase.getClient();
    const [setoresRes, clientesRes, usersRes] = await Promise.all([
      sb.from('Setor').select('id, name').order('name'),
      sb.from('Cliente').select('id, name').eq('active', true).order('name'),
      sb.from('User').select('id, name').eq('active', true).order('name'),
    ]);
    const setores = setoresRes.data ?? [];
    const clientes = clientesRes.data ?? [];
    const users = usersRes.data ?? [];

    const statusValues = ['em_aberto', 'concluido', 'pendente', 'pendente_de_resposta'];
    const recorrenciaValues = ['diaria', 'semanal', 'quinzenal', 'mensal'];
    const condicaoPrazoValues = ['vencido', 'no_prazo', 'finalizada'];

    const systemPrompt = `Você é um assistente que converte pedidos em português em um objeto JSON de filtros para listar demandas.
Retorne APENAS um JSON válido, sem markdown e sem texto extra. Use apenas as chaves permitidas.
Chaves permitidas: clienteId (UUID do cliente), assunto (string), status (um de: ${statusValues.join(', ')}), tipoRecorrencia (um de: ${recorrenciaValues.join(', ')}), protocolo (string), prioridade (true ou false), criadorId (UUID do usuário criador), responsavelPrincipalId (UUID do responsável principal), setorIds (array de UUIDs de setores), condicaoPrazo (um de: ${condicaoPrazoValues.join(', ')}), pesquisarTarefaOuObservacao (string), dataCriacaoDe (YYYY-MM-DD), dataCriacaoAte (YYYY-MM-DD), prazoDe (YYYY-MM-DD), prazoAte (YYYY-MM-DD).
Para clienteId, criadorId, responsavelPrincipalId e setorIds use SOMENTE os IDs da lista abaixo. Não invente IDs.

Setores disponíveis (id, name):
${JSON.stringify(setores)}

Clientes disponíveis (id, name):
${JSON.stringify(clientes)}

Usuários disponíveis (id, name):
${JSON.stringify(users)}

Se o usuário mencionar um nome (ex: "Comercial", "João"), use o id correspondente da lista. Se não houver correspondência, omita o campo. Retorne {} se não conseguir extrair filtros.`;

    const openai = new OpenAI({ apiKey });
    const completion = await openai.chat.completions.create({
      model: 'gpt-4o-mini',
      messages: [
        { role: 'system', content: systemPrompt },
        { role: 'user', content: query.trim() },
      ],
      temperature: 0.2,
    });
    const content = completion.choices[0]?.message?.content?.trim() || '{}';
    let parsed: Record<string, unknown> = {};
    try {
      const cleaned = content.replace(/^```json\s*/i, '').replace(/\s*```$/i, '').trim();
      parsed = JSON.parse(cleaned);
    } catch {
      return { filters: {} };
    }

    const validSetorIds = new Set(setores.map((s: any) => s.id));
    const validClienteIds = new Set(clientes.map((c: any) => c.id));
    const validUserIds = new Set(users.map((u: any) => u.id));

    const filters: ListDemandasFiltersDto = {};
    if (parsed.clienteId && validClienteIds.has(parsed.clienteId as string)) filters.clienteId = parsed.clienteId as string;
    if (typeof parsed.assunto === 'string' && parsed.assunto.trim()) filters.assunto = parsed.assunto.trim();
    if (parsed.status && statusValues.includes(parsed.status as string)) filters.status = parsed.status as string;
    if (parsed.tipoRecorrencia && recorrenciaValues.includes(parsed.tipoRecorrencia as string)) filters.tipoRecorrencia = parsed.tipoRecorrencia as string;
    if (typeof parsed.protocolo === 'string' && parsed.protocolo.trim()) filters.protocolo = parsed.protocolo.trim();
    if (parsed.prioridade === true) filters.prioridade = true;
    if (parsed.prioridade === false) filters.prioridade = false;
    if (parsed.criadorId && validUserIds.has(parsed.criadorId as string)) filters.criadorId = parsed.criadorId as string;
    if (parsed.responsavelPrincipalId && validUserIds.has(parsed.responsavelPrincipalId as string)) filters.responsavelPrincipalId = parsed.responsavelPrincipalId as string;
    if (Array.isArray(parsed.setorIds)) {
      const ids = (parsed.setorIds as string[]).filter((id) => validSetorIds.has(id));
      if (ids.length) filters.setorIds = ids;
    }
    if (parsed.condicaoPrazo && condicaoPrazoValues.includes(parsed.condicaoPrazo as string)) filters.condicaoPrazo = parsed.condicaoPrazo as 'vencido' | 'no_prazo';
    if (typeof parsed.pesquisarTarefaOuObservacao === 'string' && parsed.pesquisarTarefaOuObservacao.trim()) filters.pesquisarTarefaOuObservacao = parsed.pesquisarTarefaOuObservacao.trim();
    if (typeof parsed.dataCriacaoDe === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(parsed.dataCriacaoDe)) filters.dataCriacaoDe = parsed.dataCriacaoDe;
    if (typeof parsed.dataCriacaoAte === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(parsed.dataCriacaoAte)) filters.dataCriacaoAte = parsed.dataCriacaoAte;
    if (typeof parsed.prazoDe === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(parsed.prazoDe)) filters.prazoDe = parsed.prazoDe;
    if (typeof parsed.prazoAte === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(parsed.prazoAte)) filters.prazoAte = parsed.prazoAte;

    return { filters };
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
    const sb = this.supabase.getClient();
    const { data: rows } = await sb
      .from('Demanda')
      .select('id, status, created_at, updated_at, resolvido_em, ultima_observacao_em')
      .order('created_at', { ascending: false })
      .limit(5000);
    const list = rows ?? [];

    const concluidas = list.filter((r: any) => r.status === 'concluido');
    const comResolvidoEm = concluidas.filter((r: any) => r.resolvido_em);
    const temposResolucao = comResolvidoEm.map((r: any) => computeTempoHoras(r.created_at, r.resolvido_em)).filter((x): x is number => x != null);
    const tempoMedioResolucaoHoras = temposResolucao.length ? temposResolucao.reduce((a, b) => a + b, 0) / temposResolucao.length : null;

    const comUltimaObs = list.filter((r: any) => r.ultima_observacao_em);
    const agora = new Date().toISOString();
    const temposDesdeObs = comUltimaObs.map((r: any) => computeTempoHoras(r.ultima_observacao_em, agora)).filter((x): x is number => x != null);
    const tempoMedioDesdeUltimaObservacaoHoras = temposDesdeObs.length ? temposDesdeObs.reduce((a, b) => a + b, 0) / temposDesdeObs.length : null;
    const demandasSemObservacaoRecente = list.filter((r: any) => {
      const ultima = r.ultima_observacao_em as string | null | undefined;
      if (ultima == null) return true;
      return (computeTempoHoras(ultima, agora) ?? 0) > 24 * 7;
    }).length;

    const porStatus: Record<string, number> = {};
    list.forEach((r: any) => { porStatus[r.status] = (porStatus[r.status] || 0) + 1; });

    const metricas = {
      totalDemandas: list.length,
      concluidas: concluidas.length,
      emAberto: list.filter((r: any) => r.status === 'em_aberto').length,
      tempoMedioResolucaoHoras: tempoMedioResolucaoHoras != null ? Math.round(tempoMedioResolucaoHoras * 10) / 10 : null,
      demandasSemObservacaoRecente,
      tempoMedioDesdeUltimaObservacaoHoras: tempoMedioDesdeUltimaObservacaoHoras != null ? Math.round(tempoMedioDesdeUltimaObservacaoHoras * 10) / 10 : null,
      porStatus,
    };

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
