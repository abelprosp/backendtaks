import { Injectable, ConflictException, NotFoundException } from '@nestjs/common';
import { SupabaseService } from '../supabase/supabase.service';
import * as bcrypt from 'bcryptjs';
import { CreateUserDto } from './dto/create-user.dto';
import { UpdateUserDto } from './dto/update-user.dto';
import { MemoryTtlCache } from '../common/memory-ttl-cache';

function toUser(row: any) {
  if (!row) return null;
  return {
    id: row.id,
    email: row.email,
    passwordHash: row.password_hash,
    name: row.name,
    active: row.active,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
  };
}

type UserRole = { role: { id: string; name: string; slug: string } };
type AuthSnapshot = {
  id: string;
  email: string;
  name: string;
  active: boolean;
};

@Injectable()
export class UsersService {
  private readonly authSnapshotCache = new MemoryTtlCache<string, AuthSnapshot | null>(15_000);
  private readonly loginUserCache = new MemoryTtlCache<string, any | null>(30_000);
  private readonly fullUserCache = new MemoryTtlCache<string, any | null>(30_000);
  private readonly tokenUserCache = new MemoryTtlCache<string, any | null>(30_000);
  private readonly rolesByUserCache = new MemoryTtlCache<string, UserRole[]>(60_000);
  private readonly setorPermissoesCache = new MemoryTtlCache<string, any[]>(60_000);
  private readonly dropdownCache = new MemoryTtlCache<string, any[]>(60_000);
  private readonly rolesListCache = new MemoryTtlCache<string, any[]>(300_000);
  private readonly listAllCache = new MemoryTtlCache<string, any[]>(60_000);

  constructor(private supabase: SupabaseService) {}

  private async loadRoles(userId: string): Promise<UserRole[]> {
    return this.rolesByUserCache.getOrLoad(userId, async () => {
      const sb = this.supabase.getClient();
      const { data: roleLinks } = await sb.from('user_role').select('role_id').eq('user_id', userId);
      const roleIds = (roleLinks ?? []).map((x: any) => x.role_id).filter(Boolean);
      if (!roleIds.length) return [];
      const { data: roleRows } = await sb.from('Role').select('id, name, slug').in('id', roleIds);
      return (roleRows ?? []).map((role: any) => ({ role }));
    });
  }

  private async loadSetorPermissoes(userId: string) {
    return this.setorPermissoesCache.getOrLoad(userId, async () => {
      const sb = this.supabase.getClient();
      const { data: rawPerms } = await sb.from('user_setor_permissao').select('setor_id, can_view').eq('user_id', userId);
      const list = rawPerms ?? [];
      if (!list.length) return [];
      const { data: setores } = await sb.from('Setor').select('id, name, slug').in('id', list.map((x: any) => x.setor_id));
      const setorMap = new Map((setores ?? []).map((s: any) => [s.id, s]));
      return list.map((p: any) => ({
        setorId: p.setor_id,
        canView: p.can_view,
        setor: setorMap.get(p.setor_id),
      }));
    });
  }

  private clearListCaches() {
    this.dropdownCache.clear();
    this.listAllCache.clear();
  }

  private clearRoleCaches(userId?: string) {
    if (userId) this.rolesByUserCache.delete(userId);
    this.rolesListCache.clear();
    this.listAllCache.clear();
  }

  private clearUserCaches(userId?: string, email?: string) {
    if (userId) {
      this.authSnapshotCache.delete(userId);
      this.fullUserCache.delete(userId);
      this.tokenUserCache.delete(userId);
      this.setorPermissoesCache.delete(userId);
      this.rolesByUserCache.delete(userId);
    }
    if (email) this.loginUserCache.delete(email.toLowerCase());
  }

  async findByEmail(email: string) {
    const normalizedEmail = email.toLowerCase().trim();
    return this.loginUserCache.getOrLoad(normalizedEmail, async () => {
      const sb = this.supabase.getClient();
      const { data: rows } = await sb
        .from('User')
        .select('id, email, password_hash, name, active, created_at, updated_at')
        .eq('email', normalizedEmail)
        .limit(1);
      const row = rows?.[0];
      if (!row) return null;
      const roles = await this.loadRoles(row.id);
      const user = toUser(row);
      return user ? { ...user, roles } : null;
    });
  }

  async findById(id: string) {
    return this.fullUserCache.getOrLoad(id, async () => {
      const sb = this.supabase.getClient();
      const { data: rows } = await sb
        .from('User')
        .select('id, email, password_hash, name, active, created_at, updated_at')
        .eq('id', id)
        .limit(1);
      const row = rows?.[0];
      if (!row) return null;
      const [roles, setorPermissoes] = await Promise.all([this.loadRoles(id), this.loadSetorPermissoes(id)]);
      const user = toUser(row);
      return user ? { ...user, roles, setorPermissoes } : null;
    });
  }

  async findTokenUserById(id: string) {
    return this.tokenUserCache.getOrLoad(id, async () => {
      const sb = this.supabase.getClient();
      const { data: rows } = await sb
        .from('User')
        .select('id, email, password_hash, name, active, created_at, updated_at')
        .eq('id', id)
        .limit(1);
      const row = rows?.[0];
      if (!row) return null;
      const roles = await this.loadRoles(id);
      const user = toUser(row);
      return user ? { ...user, roles } : null;
    });
  }

  async findAuthSnapshot(id: string): Promise<AuthSnapshot | null> {
    return this.authSnapshotCache.getOrLoad(id, async () => {
      const { data: rows } = await this.supabase.getClient().from('User').select('id, email, name, active').eq('id', id).limit(1);
      return rows?.[0]
        ? {
            id: rows[0].id as string,
            email: rows[0].email as string,
            name: rows[0].name as string,
            active: !!rows[0].active,
          }
        : null;
    });
  }

  async create(dto: CreateUserDto) {
    const sb = this.supabase.getClient();
    const email = dto.email.toLowerCase();
    const { data: existing } = await sb.from('User').select('id').eq('email', email).limit(1);
    if (existing?.length) throw new ConflictException('E-mail já cadastrado');
    const passwordHash = await bcrypt.hash(dto.password, 10);
    const { data: userRow, error } = await sb
      .from('User')
      .insert({ email, password_hash: passwordHash, name: dto.name })
      .select()
      .single();
    if (error) throw new Error(error.message);
    if (dto.roleIds?.length) {
      await sb.from('user_role').insert(dto.roleIds.map((roleId) => ({ user_id: userRow.id, role_id: roleId })));
    }
    const roles = userRow ? await sb.from('user_role').select('role_id').eq('user_id', userRow.id).then(async (r) => {
      if (!r.data?.length) return [];
      const { data: roleRows } = await sb.from('Role').select('id, name, slug').in('id', r.data.map((x: any) => x.role_id));
      return roleRows?.map((rr) => ({ role: rr })) ?? [];
    }) : [];
    const user = toUser(userRow);
    if (!user) return userRow;
    const { passwordHash: _, ...out } = user;
    this.clearListCaches();
    this.clearRoleCaches(userRow.id);
    this.clearUserCaches(userRow.id, email);
    return { ...out, roles };
  }

  async listForDropdown() {
    return this.dropdownCache.getOrLoad('dropdown', async () => {
      const sb = this.supabase.getClient();
      const { data: rows } = await sb.from('User').select('id, name, email').eq('active', true).order('name');
      return rows ?? [];
    });
  }

  async listRoles() {
    return this.rolesListCache.getOrLoad('roles', async () => {
      const sb = this.supabase.getClient();
      const { data: rows } = await sb.from('Role').select('id, name, slug').order('name');
      return rows ?? [];
    });
  }

  async listAll() {
    return this.listAllCache.getOrLoad('all', async () => {
      const sb = this.supabase.getClient();
      const { data: rows } = await sb.from('User').select('id, name, email, active, created_at').order('name');
      const users = rows ?? [];
      const { data: allUserRoles } = await sb.from('user_role').select('user_id, role_id');
      const allRoles = await this.listRoles();
      const roleMap = new Map((allRoles ?? []).map((r: any) => [r.id, r]));
      const rolesByUser = new Map<string, any[]>();
      for (const ur of allUserRoles ?? []) {
        const list = rolesByUser.get(ur.user_id) ?? [];
        const role = roleMap.get(ur.role_id);
        if (role) list.push(role);
        rolesByUser.set(ur.user_id, list);
      }
      return users.map((u: any) => ({ ...u, roles: rolesByUser.get(u.id) ?? [] }));
    });
  }

  async update(id: string, dto: UpdateUserDto) {
    const sb = this.supabase.getClient();
    const { data: row } = await sb.from('User').select('*').eq('id', id).single();
    if (!row) throw new NotFoundException('Usuário não encontrado');
    const upd: { name?: string; active?: boolean } = {};
    if (dto.name != null) upd.name = dto.name.trim();
    if (dto.active !== undefined) upd.active = dto.active;
    if (Object.keys(upd).length > 0) {
      const { error } = await sb.from('User').update(upd).eq('id', id);
      if (error) throw new Error(error.message);
    }
    if (dto.roleIds !== undefined) {
      await sb.from('user_role').delete().eq('user_id', id);
      if (dto.roleIds.length > 0) {
        await sb.from('user_role').insert(dto.roleIds.map((roleId) => ({ user_id: id, role_id: roleId })));
      }
    }
    const { data: roleLinks } = await sb.from('user_role').select('role_id').eq('user_id', id);
    const roleIds = (roleLinks ?? []).map((r: any) => r.role_id);
    const { data: roleRows } = roleIds.length
      ? await sb.from('Role').select('id, name, slug').in('id', roleIds)
      : { data: [] };
    this.clearListCaches();
    this.clearRoleCaches(id);
    this.clearUserCaches(id, row.email);
    return {
      id: row.id,
      name: (upd.name ?? row.name) as string,
      email: row.email,
      active: (upd.active ?? row.active) as boolean,
      created_at: row.created_at,
      roles: roleRows ?? [],
    };
  }

  async remove(id: string) {
    const sb = this.supabase.getClient();
    const { data: row } = await sb.from('User').select('id, email').eq('id', id).single();
    if (!row) throw new NotFoundException('Usuário não encontrado');
    await sb.from('User').update({ active: false }).eq('id', id);
    this.clearListCaches();
    this.clearRoleCaches(id);
    this.clearUserCaches(id, row.email);
    return { id };
  }
}
