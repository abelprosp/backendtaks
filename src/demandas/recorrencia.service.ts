import { Injectable } from '@nestjs/common';
import { SupabaseService } from '../supabase/supabase.service';
import type { RecorrenciaTipo } from '../types/enums';

@Injectable()
export class RecorrenciaService {
  constructor(private supabase: SupabaseService) {}

  nextDataBase(dataBase: Date, tipo: RecorrenciaTipo): Date {
    const d = new Date(dataBase);
    switch (tipo) {
      case 'diaria':
        d.setDate(d.getDate() + 1);
        break;
      case 'semanal':
        d.setDate(d.getDate() + 7);
        break;
      case 'quinzenal':
        d.setDate(d.getDate() + 15);
        break;
      case 'mensal':
        d.setMonth(d.getMonth() + 1);
        break;
      default:
        d.setDate(d.getDate() + 1);
    }
    return d;
  }

  async gerarProximaDemanda(demandaOrigemId: string): Promise<string | null> {
    const sb = this.supabase.getClient();
    const { data: origem } = await sb.from('Demanda').select('*').eq('id', demandaOrigemId).single();
    if (!origem) return null;
    const { data: config } = await sb.from('recorrencia_config').select('*').eq('demanda_id', demandaOrigemId).single();
    if (!config) return null;

    const [setores, responsaveis, clientes] = await Promise.all([
      sb.from('demanda_setor').select('setor_id').eq('demanda_id', demandaOrigemId),
      sb.from('demanda_responsavel').select('user_id, is_principal').eq('demanda_id', demandaOrigemId),
      sb.from('demanda_cliente').select('cliente_id').eq('demanda_id', demandaOrigemId),
    ]);

    const proximaDataBase = this.nextDataBase(new Date(config.data_base), config.tipo as RecorrenciaTipo);
    const prazo = new Date(proximaDataBase);
    prazo.setDate(prazo.getDate() + config.prazo_reabertura_dias);

    const protocolo = await this.gerarProtocolo();
    const { data: nova, error } = await sb
      .from('Demanda')
      .insert({
        protocolo,
        assunto: origem.assunto,
        prioridade: origem.prioridade,
        prazo: prazo.toISOString().slice(0, 10),
        status: 'em_aberto',
        criador_id: origem.criador_id,
        is_recorrente: true,
        demanda_origem_id: demandaOrigemId,
      })
      .select('id')
      .single();
    if (error || !nova) return null;

    await Promise.all([
      (setores.data ?? []).length ? sb.from('demanda_setor').insert((setores.data as any[]).map((s) => ({ demanda_id: nova.id, setor_id: s.setor_id }))) : Promise.resolve(),
      (responsaveis.data ?? []).length ? sb.from('demanda_responsavel').insert((responsaveis.data as any[]).map((r) => ({ demanda_id: nova.id, user_id: r.user_id, is_principal: r.is_principal }))) : Promise.resolve(),
      (clientes.data ?? []).length ? sb.from('demanda_cliente').insert((clientes.data as any[]).map((c) => ({ demanda_id: nova.id, cliente_id: c.cliente_id }))) : Promise.resolve(),
    ]);

    await sb.from('recorrencia_config').update({ data_base: proximaDataBase.toISOString().slice(0, 10) }).eq('demanda_id', demandaOrigemId);

    return nova.id;
  }

  private async gerarProtocolo(): Promise<string> {
    const sb = this.supabase.getClient();
    const year = new Date().getFullYear();
    const start = `${year}-01-01`;
    const end = `${year + 1}-01-01`;
    const { count } = await sb.from('Demanda').select('*', { count: 'exact', head: true }).gte('created_at', start).lt('created_at', end);
    const seq = String((count ?? 0) + 1).padStart(5, '0');
    return `LUX-${year}-${seq}`;
  }
}
