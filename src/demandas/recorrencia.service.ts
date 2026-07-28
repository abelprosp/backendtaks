import { Injectable } from '@nestjs/common';
import { SupabaseService } from '../supabase/supabase.service';
import type { RecorrenciaTipo } from '../types/enums';

type RecorrenciaConfigRow = {
  demanda_id: string;
  data_base: string | null;
  tipo: RecorrenciaTipo | string | null;
  prazo_reabertura_dias: number | null;
};

type ReopenSchedule = {
  scheduledDate: string;
  nextDataBase: string;
  /** Data limite da demanda após reabrir (sempre a partir da data atual da reabertura). */
  prazo: string;
};

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

function parseDateOnly(value: string | null | undefined): Date | null {
  const raw = String(value ?? '').slice(0, 10);
  const [year, month, day] = raw.split('-').map((part) => Number(part));
  if (!year || !month || !day) return null;

  const date = new Date(Date.UTC(year, month - 1, day));
  if (
    date.getUTCFullYear() !== year ||
    date.getUTCMonth() !== month - 1 ||
    date.getUTCDate() !== day
  ) {
    return null;
  }

  return date;
}

function parseIsoDateOnly(value: string | null | undefined): string | null {
  if (!value) return null;
  const trimmed = String(value).trim();
  if (/^\d{4}-\d{2}-\d{2}$/.test(trimmed.slice(0, 10))) {
    return trimmed.slice(0, 10);
  }

  const hasZone = /Z$/i.test(trimmed) || /[+-]\d{2}/.test(trimmed);
  const instant = hasZone ? trimmed : `${trimmed}Z`;
  const date = new Date(instant);
  if (Number.isNaN(date.getTime())) {
    const parsed = parseDateOnly(trimmed);
    return parsed ? formatDateOnly(parsed) : null;
  }

  return new Intl.DateTimeFormat('en-CA', {
    timeZone: 'America/Sao_Paulo',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).format(date);
}

function formatDateOnly(date: Date): string {
  return date.toISOString().slice(0, 10);
}

function addDays(date: Date, days: number): Date {
  const next = new Date(date);
  next.setUTCDate(next.getUTCDate() + days);
  return next;
}

function isRecorrenciaTipo(value: string | null | undefined): value is RecorrenciaTipo {
  return value === 'diaria' || value === 'semanal' || value === 'quinzenal' || value === 'mensal';
}

@Injectable()
export class RecorrenciaService {
  private refreshPromise: Promise<number> | null = null;

  constructor(private supabase: SupabaseService) {}

  nextDataBase(dataBase: Date, tipo: RecorrenciaTipo): Date {
    const d = new Date(dataBase);
    switch (tipo) {
      case 'diaria':
        d.setUTCDate(d.getUTCDate() + 1);
        break;
      case 'semanal':
        d.setUTCDate(d.getUTCDate() + 7);
        break;
      case 'quinzenal':
        d.setUTCDate(d.getUTCDate() + 15);
        break;
      case 'mensal':
        d.setUTCMonth(d.getUTCMonth() + 1);
        break;
      default:
        d.setUTCDate(d.getUTCDate() + 1);
    }
    return d;
  }

  private buildReopenSchedule(config: RecorrenciaConfigRow, today = getTodayInSaoPaulo()): ReopenSchedule | null {
    const tipo = isRecorrenciaTipo(config.tipo) ? config.tipo : null;
    const todayDate = parseDateOnly(today);
    const firstScheduledDate = parseDateOnly(config.data_base);
    if (!tipo || !todayDate || !firstScheduledDate || firstScheduledDate.getTime() > todayDate.getTime()) {
      return null;
    }

    let scheduledDate = firstScheduledDate;
    let nextScheduledDate = this.nextDataBase(scheduledDate, tipo);
    let guard = 0;

    while (nextScheduledDate.getTime() <= todayDate.getTime() && guard < 1000) {
      scheduledDate = nextScheduledDate;
      nextScheduledDate = this.nextDataBase(scheduledDate, tipo);
      guard += 1;
    }

    const prazoDias = Math.max(Number(config.prazo_reabertura_dias ?? 0) || 0, 0);
    /** Prazo conta a partir do dia em que a demanda reabre (hoje), não da data recorrente — evita reabrir já “vencida”. */
    return {
      scheduledDate: formatDateOnly(scheduledDate),
      nextDataBase: formatDateOnly(nextScheduledDate),
      prazo: formatDateOnly(addDays(todayDate, prazoDias)),
    };
  }

  private isCycleFulfilledByCompletion(
    resolvedAt: string | null,
    schedule: ReopenSchedule,
    cycleDataBase: string | null | undefined,
  ): boolean {
    const resolved = parseDateOnly(resolvedAt);
    const scheduled = parseDateOnly(schedule.scheduledDate);
    const cycleStart = parseDateOnly(cycleDataBase);
    if (!resolved) return false;
    if (scheduled && resolved.getTime() >= scheduled.getTime()) return true;
    if (cycleStart && resolved.getTime() >= cycleStart.getTime()) return true;
    return false;
  }

  private async advanceNextDataBase(demandaId: string, nextDataBase: string): Promise<void> {
    const sb = this.supabase.getClient();
    await sb.from('recorrencia_config').update({ data_base: nextDataBase }).eq('demanda_id', demandaId);
  }

  /**
   * Conclusão em ou após a data do ciclo vigente: avança data_base sem reabrir.
   * Evita reabertura tardia (ex.: concluiu na sexta, job reabre no domingo pelo ciclo do dia 10).
   */
  async fulfillCycleOnCompletion(demandaId: string, resolvedAtIso: string): Promise<void> {
    const sb = this.supabase.getClient();
    const today = getTodayInSaoPaulo();
    const { data: demanda } = await sb.from('Demanda').select('is_recorrente').eq('id', demandaId).single();
    if (!demanda?.is_recorrente) return;

    const { data: config } = await sb
      .from('recorrencia_config')
      .select('demanda_id,data_base,tipo,prazo_reabertura_dias')
      .eq('demanda_id', demandaId)
      .maybeSingle();
    if (!config) return;

    const schedule = this.buildReopenSchedule(config as RecorrenciaConfigRow, today);
    if (!schedule) return;

    const resolvedAt = parseIsoDateOnly(resolvedAtIso);
    if (!this.isCycleFulfilledByCompletion(resolvedAt, schedule, config.data_base)) return;

    await this.advanceNextDataBase(demandaId, schedule.nextDataBase);
  }

  private buildHistoryMetadata(schedule: ReopenSchedule): Record<string, unknown> {
    return {
      title: 'Demanda reaberta automaticamente',
      summary: `Recorrencia de ${schedule.scheduledDate}.`,
      entries: [
        { label: 'Data recorrente', value: schedule.scheduledDate },
        { label: 'Novo prazo', value: schedule.prazo },
        { label: 'Proxima recorrencia', value: schedule.nextDataBase },
      ],
    };
  }

  private async applyReopen(demandaId: string, schedule: ReopenSchedule): Promise<boolean> {
    const sb = this.supabase.getClient();
    const now = new Date().toISOString();
    const { data: demanda, error: errDemanda } = await sb
      .from('Demanda')
      .update({
        status: 'em_aberto',
        prazo: schedule.prazo,
        resolvido_em: null,
        updated_at: now,
      })
      .eq('id', demandaId)
      .eq('status', 'concluido')
      .select('id')
      .maybeSingle();

    if (errDemanda || !demanda?.id) return false;

    const { error: errConfig } = await sb
      .from('recorrencia_config')
      .update({ data_base: schedule.nextDataBase })
      .eq('demanda_id', demandaId);
    if (errConfig) return true;

    await sb.from('demanda_evento').insert({
      demanda_id: demandaId,
      user_id: null,
      tipo: 'demanda_recorrente_reaberta',
      descricao: `Demanda reaberta automaticamente pela recorrencia de ${schedule.scheduledDate}.`,
      metadata: this.buildHistoryMetadata(schedule),
      created_at: now,
    });

    return true;
  }

  /**
   * Reabre a mesma demanda recorrente: atualiza status para em_aberto, novo prazo e próxima data base.
   * Ex.: todo mês baixa fatura → conclui em março → em abril a mesma demanda reabre sozinha.
   */
  async reabrirDemanda(demandaId: string): Promise<string | null> {
    const sb = this.supabase.getClient();
    const today = getTodayInSaoPaulo();
    const { data: demanda } = await sb.from('Demanda').select('id,status,is_recorrente,resolvido_em').eq('id', demandaId).single();
    if (!demanda) return null;
    const resolvedAt = parseIsoDateOnly(typeof demanda.resolvido_em === 'string' ? demanda.resolvido_em : null);
    const { data: config } = await sb.from('recorrencia_config').select('demanda_id,data_base,tipo,prazo_reabertura_dias').eq('demanda_id', demandaId).single();
    if (!config) return null;

    const schedule = this.buildReopenSchedule(config as RecorrenciaConfigRow, today);
    if (!schedule) return null;

    if (this.isCycleFulfilledByCompletion(resolvedAt, schedule, config.data_base)) {
      await this.advanceNextDataBase(demandaId, schedule.nextDataBase);
      return null;
    }

    return await this.applyReopen(demandaId, schedule) ? demandaId : null;
  }

  async reabrirDemandasVencidas(): Promise<number> {
    if (this.refreshPromise) return this.refreshPromise;

    this.refreshPromise = this.runReabrirDemandasVencidas().finally(() => {
      this.refreshPromise = null;
    });

    return this.refreshPromise;
  }

  private async runReabrirDemandasVencidas(): Promise<number> {
    const sb = this.supabase.getClient();
    const today = getTodayInSaoPaulo();
    const { data: configs, error } = await sb
      .from('recorrencia_config')
      .select('demanda_id,data_base,tipo,prazo_reabertura_dias')
      .lte('data_base', today);
    if (error || !configs?.length) return 0;

    const demandaIds = Array.from(new Set(configs.map((item: any) => String(item?.demanda_id ?? '')).filter(Boolean)));
    if (!demandaIds.length) return 0;

    const { data: demandas } = await sb
      .from('Demanda')
      .select('id,status,is_recorrente,resolvido_em')
      .in('id', demandaIds);

    const demandasById = new Map((demandas ?? []).map((row: any) => [String(row?.id ?? ''), row]));
    let reopenedCount = 0;

    for (const config of configs as RecorrenciaConfigRow[]) {
      const demanda = demandasById.get(config.demanda_id);
      if (!demanda?.is_recorrente || demanda.status !== 'concluido') {
        continue;
      }
      const resolvedAt = parseIsoDateOnly(typeof demanda.resolvido_em === 'string' ? demanda.resolvido_em : null);

      const schedule = this.buildReopenSchedule(config, today);
      if (!schedule) continue;

      if (this.isCycleFulfilledByCompletion(resolvedAt, schedule, config.data_base)) {
        try {
          await this.advanceNextDataBase(config.demanda_id, schedule.nextDataBase);
        } catch {
          continue;
        }
        continue;
      }

      try {
        if (await this.applyReopen(config.demanda_id, schedule)) {
          reopenedCount += 1;
        }
      } catch {
        continue;
      }
    }

    return reopenedCount;
  }

  /** @deprecated Use reabrirDemanda. Mantido por compatibilidade; reabre a mesma demanda em vez de criar nova. */
  async gerarProximaDemanda(demandaOrigemId: string): Promise<string | null> {
    return this.reabrirDemanda(demandaOrigemId);
  }
}
