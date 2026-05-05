import { Injectable } from '@nestjs/common';
import { SupabaseService } from '../supabase/supabase.service';
import { MemoryTtlCache } from '../common/memory-ttl-cache';

@Injectable()
export class DemandaVisibilityService {
  private readonly visibleIdsCache = new MemoryTtlCache<string, string[]>(15_000);
  private readonly adminCache = new MemoryTtlCache<string, boolean>(60_000);

  constructor(private supabase: SupabaseService) {}

  private async isAdmin(userId: string): Promise<boolean> {
    return this.adminCache.getOrLoad(userId, async () => {
      const sb = this.supabase.getClient();
      const { data: roleLinks } = await sb.from('user_role').select('role_id').eq('user_id', userId);
      const roleIds = (roleLinks ?? []).map((item: any) => item.role_id).filter(Boolean);
      if (!roleIds.length) return false;

      const { data: roles } = await sb.from('Role').select('slug').in('id', roleIds);
      return (roles ?? []).some((role: any) => String(role?.slug ?? '').toLowerCase() === 'admin');
    });
  }

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

  async canViewDemanda(userId: string, demandaId: string, _criadorId?: string | null): Promise<boolean> {
    if (await this.isAdmin(userId)) return true;

    const sb = this.supabase.getClient();
    const { data: row } = await sb
      .from('Demanda')
      .select('criador_id, is_privada, private_owner_user_id')
      .eq('id', demandaId)
      .maybeSingle();
    if (!row) return false;

    if (!row.is_privada) return true;

    if (row.criador_id === userId || row.private_owner_user_id === userId) return true;

    const { data: privView } = await sb
      .from('demanda_private_viewer')
      .select('demanda_id')
      .eq('demanda_id', demandaId)
      .eq('user_id', userId)
      .limit(1);
    if (privView?.length) return true;

    const [respRes, setoresRes, subtarefaRes] = await Promise.all([
      sb.from('demanda_responsavel').select('user_id').eq('demanda_id', demandaId),
      sb.from('demanda_setor').select('setor_id').eq('demanda_id', demandaId),
      sb.from('subtarefa').select('demanda_id').eq('demanda_id', demandaId).eq('responsavel_user_id', userId).limit(1),
    ]);
    if (respRes.data?.some((r: any) => r.user_id === userId)) return true;
    if (subtarefaRes.data?.length) return true;

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
      if (await this.isAdmin(userId)) {
        const { data } = await this.supabase.getClient().from('Demanda').select('id');
        return [...new Set((data ?? []).map((item: any) => String(item?.id ?? '')).filter(Boolean))];
      }

      const rpcIds = await this.getVisibleIdsViaRpc(userId);
      if (rpcIds) return [...new Set(rpcIds)];

      const sb = this.supabase.getClient();
      const [asCriador, asResponsavel, bySetor, asSubtarefa, todasPublicas] = await Promise.all([
        sb.from('Demanda').select('id').eq('criador_id', userId),
        sb.from('demanda_responsavel').select('demanda_id').eq('user_id', userId),
        sb.from('user_setor_permissao').select('setor_id').eq('user_id', userId).eq('can_view', true),
        sb.from('subtarefa').select('demanda_id').eq('responsavel_user_id', userId),
        sb.from('Demanda').select('id').or('is_privada.is.null,is_privada.eq.false'),
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
      (asSubtarefa.data ?? []).forEach((d: any) => ids.add(d.demanda_id));
      bySetorDemandas.forEach((d: any) => ids.add(d.demanda_id));
      (todasPublicas.data ?? []).forEach((d: any) => ids.add(d.id));
      return Array.from(ids);
    });
  }
}
