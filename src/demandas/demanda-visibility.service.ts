import { Injectable } from '@nestjs/common';
import { SupabaseService } from '../supabase/supabase.service';
import { MemoryTtlCache } from '../common/memory-ttl-cache';

@Injectable()
export class DemandaVisibilityService {
  private readonly visibleIdsCache = new MemoryTtlCache<string, string[]>(15_000);

  constructor(private supabase: SupabaseService) {}

  private async getVisibleIdsViaRpc(userId: string): Promise<string[] | null> {
    const { data, error } = await this.supabase.getClient().rpc('rpc_visible_demanda_ids', { p_user_id: userId });
    if (error || !Array.isArray(data)) return null;
    return data
      .map((item: any) => String(item?.demanda_id ?? ''))
      .filter(Boolean);
  }

  clearVisibleDemandaIdsCache() {
    this.visibleIdsCache.clear();
  }

  async canViewDemanda(userId: string, demandaId: string, criadorId?: string | null): Promise<boolean> {
    const sb = this.supabase.getClient();
    const demandaCriadorId =
      criadorId ??
      (
        await sb.from('Demanda').select('criador_id').eq('id', demandaId).single()
      ).data?.criador_id;
    if (!demandaCriadorId) return false;
    if (demandaCriadorId === userId) return true;

    const [respRes, setoresRes] = await Promise.all([
      sb.from('demanda_responsavel').select('user_id').eq('demanda_id', demandaId),
      sb.from('demanda_setor').select('setor_id').eq('demanda_id', demandaId),
    ]);
    const resp = respRes.data;
    if (resp?.some((r: any) => r.user_id === userId)) return true;
    const setorIds = (setoresRes.data ?? []).map((s: any) => s.setor_id);
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
    return this.visibleIdsCache.getOrLoad(userId, async () => {
      const rpcIds = await this.getVisibleIdsViaRpc(userId);
      if (rpcIds) return [...new Set(rpcIds)];

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
    });
  }
}
