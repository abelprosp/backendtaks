import { Injectable, NotFoundException } from '@nestjs/common';
import { SupabaseService } from '../supabase/supabase.service';
import { CreateTemplateDto } from './dto/create-template.dto';
import { UpdateTemplateDto } from './dto/update-template.dto';

function mapTemplate(row: any, criador?: any, setores?: any[], responsaveis?: any[], subtarefas?: any[]) {
  if (!row) return null;
  return {
    id: row.id,
    name: row.name,
    descricao: row.descricao,
    assuntoTemplate: row.assunto_template,
    prioridadeDefault: row.prioridade_default,
    observacoesGeraisTemplate: row.observacoes_gerais_template,
    isRecorrenteDefault: row.is_recorrente_default,
    recorrenciaTipo: row.recorrencia_tipo,
    recorrenciaPrazoReaberturaDias: row.recorrencia_prazo_reabertura_dias,
    criadorId: row.criador_id,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
    criador: criador ? { id: criador.id, name: criador.name, email: criador.email } : undefined,
    setores: setores?.map((s) => ({ setor: { id: s.id, name: s.name, slug: s.slug } })) ?? [],
    responsaveis: responsaveis ?? [],
    subtarefas: subtarefas?.sort((a, b) => (a.ordem ?? 0) - (b.ordem ?? 0)) ?? [],
  };
}

@Injectable()
export class TemplatesService {
  constructor(private supabase: SupabaseService) {}

  private async loadTemplateRelations(templateId: string) {
    const sb = this.supabase.getClient();
    const [criadorRes, setorRes, respRes, subRes] = await Promise.all([
      sb.from('Template').select('criador_id').eq('id', templateId).single().then(async (r) => {
        if (!r.data?.criador_id) return null;
        const u = await sb.from('User').select('id, name, email').eq('id', r.data.criador_id).single();
        return u.data;
      }),
      sb.from('template_setor').select('setor_id').eq('template_id', templateId).then(async (r) => {
        const ids = (r.data ?? []).map((x: any) => x.setor_id);
        if (!ids.length) return [];
        const s = await sb.from('Setor').select('id, name, slug').in('id', ids);
        return s.data ?? [];
      }),
      sb.from('template_responsavel').select('user_id, is_principal').eq('template_id', templateId).then(async (r) => {
        const list = r.data ?? [];
        if (!list.length) return [];
        const ids = list.map((x: any) => x.user_id);
        const u = await sb.from('User').select('id, name, email').in('id', ids);
        const userMap = new Map((u.data ?? []).map((x: any) => [x.id, x]));
        return list.map((p: any) => ({ userId: p.user_id, isPrincipal: p.is_principal, user: userMap.get(p.user_id) }));
      }),
      sb.from('template_subtarefa').select('*').eq('template_id', templateId),
    ]);
    return {
      criador: criadorRes,
      setores: setorRes,
      responsaveis: respRes,
      subtarefas: subRes.data ?? [],
    };
  }

  async create(userId: string, dto: CreateTemplateDto) {
    const sb = this.supabase.getClient();
    const { data: row, error } = await sb
      .from('Template')
      .insert({
        name: dto.name,
        descricao: dto.descricao,
        assunto_template: dto.assuntoTemplate,
        prioridade_default: dto.prioridadeDefault ?? false,
        observacoes_gerais_template: dto.observacoesGeraisTemplate,
        is_recorrente_default: dto.isRecorrenteDefault ?? false,
        recorrencia_tipo: dto.recorrenciaTipo ?? null,
        recorrencia_prazo_reabertura_dias: dto.recorrenciaPrazoReaberturaDias ?? null,
        criador_id: userId,
      })
      .select()
      .single();
    if (error) throw new Error(error.message);
    if (dto.setorIds?.length) await sb.from('template_setor').insert(dto.setorIds.map((setorId) => ({ template_id: row.id, setor_id: setorId })));
    if (dto.responsaveis?.length) await sb.from('template_responsavel').insert(dto.responsaveis.map((r) => ({ template_id: row.id, user_id: r.userId, is_principal: r.isPrincipal ?? false })));
    if (dto.subtarefas?.length) await sb.from('template_subtarefa').insert(dto.subtarefas.map((t, i) => ({ template_id: row.id, titulo: t.titulo, ordem: t.ordem ?? i })));
    const rel = await this.loadTemplateRelations(row.id);
    return mapTemplate(row, rel.criador, rel.setores, rel.responsaveis, rel.subtarefas);
  }

  async findAll() {
    const sb = this.supabase.getClient();
    const { data: rows } = await sb.from('Template').select('*').order('updated_at', { ascending: false });
    const result = [];
    for (const row of rows ?? []) {
      const rel = await this.loadTemplateRelations(row.id);
      result.push(mapTemplate(row, rel.criador, rel.setores, rel.responsaveis, rel.subtarefas));
    }
    return result;
  }

  async findOne(id: string) {
    const sb = this.supabase.getClient();
    const { data: row } = await sb.from('Template').select('*').eq('id', id).single();
    if (!row) throw new NotFoundException('Template não encontrado');
    const rel = await this.loadTemplateRelations(id);
    return mapTemplate(row, rel.criador, rel.setores, rel.responsaveis, rel.subtarefas);
  }

  async update(userId: string, id: string, dto: UpdateTemplateDto) {
    await this.findOne(id);
    const sb = this.supabase.getClient();
    const upd: any = {};
    if (dto.name != null) upd.name = dto.name;
    if (dto.descricao !== undefined) upd.descricao = dto.descricao;
    if (dto.assuntoTemplate !== undefined) upd.assunto_template = dto.assuntoTemplate;
    if (dto.prioridadeDefault !== undefined) upd.prioridade_default = dto.prioridadeDefault;
    if (dto.observacoesGeraisTemplate !== undefined) upd.observacoes_gerais_template = dto.observacoesGeraisTemplate;
    if (dto.isRecorrenteDefault !== undefined) upd.is_recorrente_default = dto.isRecorrenteDefault;
    if (dto.recorrenciaTipo !== undefined) upd.recorrencia_tipo = dto.recorrenciaTipo;
    if (dto.recorrenciaPrazoReaberturaDias !== undefined) upd.recorrencia_prazo_reabertura_dias = dto.recorrenciaPrazoReaberturaDias;
    if (Object.keys(upd).length) await sb.from('Template').update(upd).eq('id', id);
    if (dto.setorIds) {
      await sb.from('template_setor').delete().eq('template_id', id);
      if (dto.setorIds.length) await sb.from('template_setor').insert(dto.setorIds.map((setorId) => ({ template_id: id, setor_id: setorId })));
    }
    if (dto.responsaveis) {
      await sb.from('template_responsavel').delete().eq('template_id', id);
      if (dto.responsaveis.length) await sb.from('template_responsavel').insert(dto.responsaveis.map((r) => ({ template_id: id, user_id: r.userId, is_principal: r.isPrincipal ?? false })));
    }
    if (dto.subtarefas) {
      await sb.from('template_subtarefa').delete().eq('template_id', id);
      if (dto.subtarefas.length) await sb.from('template_subtarefa').insert(dto.subtarefas.map((t, i) => ({ template_id: id, titulo: t.titulo, ordem: t.ordem ?? i })));
    }
    return this.findOne(id);
  }

  async remove(id: string) {
    await this.findOne(id);
    const sb = this.supabase.getClient();
    await sb.from('Template').delete().eq('id', id);
    return { id };
  }

  async getForDemanda(id: string) {
    return this.findOne(id);
  }
}
