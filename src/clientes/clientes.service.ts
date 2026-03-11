import { Injectable, NotFoundException } from '@nestjs/common';
import { SupabaseService } from '../supabase/supabase.service';
import { CreateClienteDto } from './dto/create-cliente.dto';
import { UpdateClienteDto } from './dto/update-cliente.dto';

@Injectable()
export class ClientesService {
  constructor(private supabase: SupabaseService) {}

  async findAll(activeOnly = true) {
    let q = this.supabase.getClient().from('Cliente').select('*').order('name');
    if (activeOnly) q = q.eq('active', true);
    const { data } = await q;
    return data ?? [];
  }

  async create(dto: CreateClienteDto) {
    const sb = this.supabase.getClient();
    const { data, error } = await sb
      .from('Cliente')
      .insert({ name: dto.name.trim(), active: dto.active ?? true })
      .select()
      .single();
    if (error) throw new Error(error.message);
    return data;
  }

  async update(id: string, dto: UpdateClienteDto) {
    const sb = this.supabase.getClient();
    const { data: row } = await sb.from('Cliente').select('*').eq('id', id).single();
    if (!row) throw new NotFoundException('Cliente não encontrado');
    const upd: { name?: string; active?: boolean } = {};
    if (dto.name != null) upd.name = dto.name.trim();
    if (dto.active !== undefined) upd.active = dto.active;
    if (Object.keys(upd).length === 0) return row;
    const { data, error } = await sb.from('Cliente').update(upd).eq('id', id).select().single();
    if (error) throw new Error(error.message);
    return data;
  }

  async remove(id: string) {
    const sb = this.supabase.getClient();
    const { data: row } = await sb.from('Cliente').select('id').eq('id', id).single();
    if (!row) throw new NotFoundException('Cliente não encontrado');
    await sb.from('Cliente').delete().eq('id', id);
    return { id };
  }
}
