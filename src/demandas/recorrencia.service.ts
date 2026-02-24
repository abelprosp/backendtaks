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

  /**
   * Reabre a mesma demanda recorrente: atualiza status para em_aberto, novo prazo e próxima data base.
   * Ex.: todo mês baixa fatura → conclui em março → em abril a mesma demanda reabre sozinha.
   */
  async reabrirDemanda(demandaId: string): Promise<string | null> {
    const sb = this.supabase.getClient();
    const { data: demanda } = await sb.from('Demanda').select('id').eq('id', demandaId).single();
    if (!demanda) return null;
    const { data: config } = await sb.from('recorrencia_config').select('*').eq('demanda_id', demandaId).single();
    if (!config) return null;

    const proximaDataBase = this.nextDataBase(new Date(config.data_base), config.tipo as RecorrenciaTipo);
    const prazo = new Date(proximaDataBase);
    prazo.setDate(prazo.getDate() + config.prazo_reabertura_dias);

    const { error: errDemanda } = await sb
      .from('Demanda')
      .update({
        status: 'em_aberto',
        prazo: prazo.toISOString().slice(0, 10),
        resolvido_em: null,
        updated_at: new Date().toISOString(),
      })
      .eq('id', demandaId);
    if (errDemanda) return null;

    const { error: errConfig } = await sb
      .from('recorrencia_config')
      .update({ data_base: proximaDataBase.toISOString().slice(0, 10) })
      .eq('demanda_id', demandaId);
    if (errConfig) return null;

    return demandaId;
  }

  /** @deprecated Use reabrirDemanda. Mantido por compatibilidade; reabre a mesma demanda em vez de criar nova. */
  async gerarProximaDemanda(demandaOrigemId: string): Promise<string | null> {
    return this.reabrirDemanda(demandaOrigemId);
  }
}
