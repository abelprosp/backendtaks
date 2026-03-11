import { Injectable, ConflictException, NotFoundException } from '@nestjs/common';
import { SupabaseService } from '../supabase/supabase.service';
import { CreateSetorDto } from './dto/create-setor.dto';
import { UpdateSetorDto } from './dto/update-setor.dto';

function slugify(s: string): string {
  return s
    .toLowerCase()
    .normalize('NFD')
    .replace(/\p{Diacritic}/gu, '')
    .replace(/\s+/g, '-')
    .replace(/[^a-z0-9-]/g, '')
    .replace(/-+/g, '-')
    .replace(/^-|-$/g, '') || 'setor';
}

@Injectable()
export class SetoresService {
  constructor(private supabase: SupabaseService) {}

  async findAll() {
    const { data } = await this.supabase.getClient().from('Setor').select('*').order('name');
    return data ?? [];
  }

  async create(dto: CreateSetorDto) {
    const sb = this.supabase.getClient();
    const slug = (dto.slug?.trim() || slugify(dto.name)) || 'setor';
    const { data: existing } = await sb.from('Setor').select('id').eq('slug', slug).limit(1);
    if (existing?.length) throw new ConflictException('Já existe um setor com esse slug');
    const { data, error } = await sb.from('Setor').insert({ name: dto.name.trim(), slug }).select().single();
    if (error) throw new Error(error.message);
    return data;
  }

  async update(id: string, dto: UpdateSetorDto) {
    const sb = this.supabase.getClient();
    const { data: row } = await sb.from('Setor').select('*').eq('id', id).single();
    if (!row) throw new NotFoundException('Setor não encontrado');
    const upd: { name?: string; slug?: string } = {};
    if (dto.name != null) upd.name = dto.name.trim();
    if (dto.slug != null) upd.slug = dto.slug.trim();
    if (Object.keys(upd).length === 0) return row;
    if (upd.slug) {
      const { data: ex } = await sb.from('Setor').select('id').eq('slug', upd.slug).neq('id', id).limit(1);
      if (ex?.length) throw new ConflictException('Já existe um setor com esse slug');
    }
    const { data, error } = await sb.from('Setor').update(upd).eq('id', id).select().single();
    if (error) throw new Error(error.message);
    return data;
  }

  async remove(id: string) {
    const sb = this.supabase.getClient();
    const { data: row } = await sb.from('Setor').select('id').eq('id', id).single();
    if (!row) throw new NotFoundException('Setor não encontrado');
    await sb.from('Setor').delete().eq('id', id);
    return { id };
  }
}
