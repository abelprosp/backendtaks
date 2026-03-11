import { Injectable } from '@nestjs/common';
import { SupabaseService } from '../supabase/supabase.service';

@Injectable()
export class DemandaVisibilityService {
  constructor(private supabase: SupabaseService) {}

  async canViewDemanda(userId: string, demandaId: string): Promise<boolean> {
    const sb = this.supabase.getClient();
    const { data: demanda } = await sb.from('Demanda').select('criador_id').eq('id', demandaId).single();
    if (!demanda) return false;
    if (demanda.criador_id === userId) return true;
    const { data: resp } = await sb.from('demanda_responsavel').select('user_id').eq('demanda_id', demandaId);
    if (resp?.some((r: any) => r.user_id === userId)) return true;
    const { data: setores } = await sb.from('demanda_setor').select('setor_id').eq('demanda_id', demandaId);
    const setorIds = (setores ?? []).map((s: any) => s.setor_id);
    if (setorIds.length === 0) return false;
    const { data: perm } = await sb
      .from('user_setor_permissao')
      .select('id')
      .eq('user_id', userId)
      .eq('can_view', true)
      .in('setor_id', setorIds)
      .limit(1);
    return !!perm?.length;
  }

  /** Retorna lista de demanda IDs que o usuário pode ver (para filtrar na listagem). */
  async visibleDemandaIds(userId: string): Promise<string[]> {
    const sb = this.supabase.getClient();
    const [asCriador, asResponsavel, bySetor] = await Promise.all([
      sb.from('Demanda').select('id').eq('criador_id', userId),
      sb.from('demanda_responsavel').select('demanda_id').eq('user_id', userId),
      sb.from('user_setor_permissao').select('setor_id').eq('user_id', userId).eq('can_view', true),
    ]);
    const setorIds = (bySetor.data ?? []).map((s: any) => s.setor_id);
    let bySetorDemandas: { demanda_id: string }[] = [];
    if (setorIds.length > 0) {
      const r = await sb.from('demanda_setor').select('demanda_id').in('setor_id', setorIds);
      bySetorDemandas = r.data ?? [];
    }
    const ids = new Set<string>();
    (asCriador.data ?? []).forEach((d: any) => ids.add(d.id));
    (asResponsavel.data ?? []).forEach((d: any) => ids.add(d.demanda_id));
    bySetorDemandas.forEach((d: any) => ids.add(d.demanda_id));
    return Array.from(ids);
  }
}
