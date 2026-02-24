import { Injectable, ConflictException, NotFoundException } from '@nestjs/common';
import { SupabaseService } from '../supabase/supabase.service';
import * as bcrypt from 'bcrypt';
import { CreateUserDto } from './dto/create-user.dto';
import { UpdateUserDto } from './dto/update-user.dto';

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

@Injectable()
export class UsersService {
  constructor(private supabase: SupabaseService) {}

  async findByEmail(email: string) {
    const sb = this.supabase.getClient();
    const { data: rows } = await sb.from('User').select('*').ilike('email', email.toLowerCase()).limit(1);
    const row = rows?.[0];
    if (!row) return null;
    const [roles, setorPermissoes] = await Promise.all([
      sb.from('user_role').select('role_id').eq('user_id', row.id).then(async (r) => {
        if (!r.data?.length) return [];
        const { data: roleRows } = await sb.from('Role').select('id, name, slug').in('id', r.data.map((x) => x.role_id));
        return roleRows?.map((rr) => ({ role: rr })) ?? [];
      }),
      sb.from('user_setor_permissao').select('setor_id, can_view').eq('user_id', row.id).then(async (r) => {
        const list = r.data ?? [];
        if (!list.length) return [];
        const { data: setores } = await sb.from('Setor').select('id, name, slug').in('id', list.map((x: any) => x.setor_id));
        const setorMap = new Map((setores ?? []).map((s: any) => [s.id, s]));
        return list.map((p: any) => ({
          setorId: p.setor_id,
          canView: p.can_view,
          setor: setorMap.get(p.setor_id),
        }));
      }),
    ]);
    const user = toUser(row);
    return user ? { ...user, roles: roles.map((r) => ({ role: r.role })), setorPermissoes } : null;
  }

  async findById(id: string) {
    const sb = this.supabase.getClient();
    const { data: rows } = await sb.from('User').select('*').eq('id', id).limit(1);
    const row = rows?.[0];
    if (!row) return null;
    const [roles, setorPermissoes] = await Promise.all([
      sb.from('user_role').select('role_id').eq('user_id', id).then(async (r) => {
        if (!r.data?.length) return [];
        const { data: roleRows } = await sb.from('Role').select('id, name, slug').in('id', r.data.map((x) => x.role_id));
        return roleRows?.map((rr) => ({ role: rr })) ?? [];
      }),
      sb.from('user_setor_permissao').select('setor_id, can_view').eq('user_id', id).then(async (r) => {
        const list = r.data ?? [];
        if (!list.length) return [];
        const { data: setores } = await sb.from('Setor').select('id, name, slug').in('id', list.map((x: any) => x.setor_id));
        const setorMap = new Map((setores ?? []).map((s: any) => [s.id, s]));
        return list.map((p: any) => ({
          setorId: p.setor_id,
          canView: p.can_view,
          setor: setorMap.get(p.setor_id),
        }));
      }),
    ]);
    const user = toUser(row);
    return user ? { ...user, roles: roles.map((r) => ({ role: r.role })), setorPermissoes } : null;
  }

  async create(dto: CreateUserDto) {
    const sb = this.supabase.getClient();
    const email = dto.email.toLowerCase();
    const { data: existing } = await sb.from('User').select('id').ilike('email', email).limit(1);
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
    return { ...out, roles };
  }

  async listForDropdown() {
    const sb = this.supabase.getClient();
    const { data: rows } = await sb.from('User').select('id, name, email').eq('active', true).order('name');
    return rows ?? [];
  }

  async listAll() {
    const sb = this.supabase.getClient();
    const { data: rows } = await sb.from('User').select('id, name, email, active, created_at').order('name');
    return rows ?? [];
  }

  async update(id: string, dto: UpdateUserDto) {
    const sb = this.supabase.getClient();
    const { data: row } = await sb.from('User').select('*').eq('id', id).single();
    if (!row) throw new NotFoundException('Usuário não encontrado');
    const upd: { name?: string; active?: boolean } = {};
    if (dto.name != null) upd.name = dto.name.trim();
    if (dto.active !== undefined) upd.active = dto.active;
    if (Object.keys(upd).length === 0) return { id: row.id, name: row.name, email: row.email, active: row.active, created_at: row.created_at };
    const { data, error } = await sb.from('User').update(upd).eq('id', id).select('id, name, email, active, created_at').single();
    if (error) throw new Error(error.message);
    return data;
  }

  async remove(id: string) {
    const sb = this.supabase.getClient();
    const { data: row } = await sb.from('User').select('id').eq('id', id).single();
    if (!row) throw new NotFoundException('Usuário não encontrado');
    await sb.from('User').update({ active: false }).eq('id', id);
    return { id };
  }
}
