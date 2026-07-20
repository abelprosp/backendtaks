import { Injectable, NotFoundException } from '@nestjs/common';
import { SupabaseService } from '../supabase/supabase.service';
import { CreateClienteDto } from './dto/create-cliente.dto';
import { UpdateClienteDto } from './dto/update-cliente.dto';
import { MemoryTtlCache } from '../common/memory-ttl-cache';
import { normalizeClienteMultivalueField } from './cliente-contact.util';

@Injectable()
export class ClientesService {
  private readonly listCache = new MemoryTtlCache<string, any[]>(60_000);

  constructor(private supabase: SupabaseService) {}

  async findAll(activeOnly = true) {
    const key = activeOnly ? 'active' : 'all';
    return this.listCache.getOrLoad(key, async () => {
      let q = this.supabase.getClient().from('Cliente').select('*').order('name');
      if (activeOnly) q = q.eq('active', true);
      const { data } = await q;
      return data ?? [];
    });
  }

  private trimOrNull(value: string | null | undefined): string | null {
    if (value == null) return null;
    const t = String(value).trim();
    return t || null;
  }

  /** Monta colunas do perfil (snake_case) a partir do DTO; só inclui chaves presentes em `source`. */
  private buildProfilePatch(
    source: Partial<CreateClienteDto & UpdateClienteDto>,
    keys: (keyof CreateClienteDto)[],
  ): Record<string, unknown> {
    const out: Record<string, unknown> = {};
    const setText = (dtoKey: keyof CreateClienteDto, col: string) => {
      if (!(dtoKey in source)) return;
      const raw = source[dtoKey];
      out[col] = this.trimOrNull(raw as string);
    };
    const setMultivalue = (dtoKey: keyof CreateClienteDto, col: string) => {
      if (!(dtoKey in source)) return;
      const raw = source[dtoKey] as string | undefined;
      out[col] = normalizeClienteMultivalueField(raw);
    };

    for (const key of keys) {
      switch (key) {
        case 'tipoPessoa':
          setText('tipoPessoa', 'tipo_pessoa');
          break;
        case 'documento':
          setText('documento', 'documento');
          break;
        case 'nomeFantasia':
          setText('nomeFantasia', 'nome_fantasia');
          break;
        case 'ramoAtividade':
          setText('ramoAtividade', 'ramo_atividade');
          break;
        case 'inscricaoEstadual':
          setText('inscricaoEstadual', 'inscricao_estadual');
          break;
        case 'cep':
          setText('cep', 'cep');
          break;
        case 'endereco':
          setText('endereco', 'endereco');
          break;
        case 'numero':
          setText('numero', 'numero');
          break;
        case 'complemento':
          setText('complemento', 'complemento');
          break;
        case 'bairro':
          setText('bairro', 'bairro');
          break;
        case 'cidade':
          setText('cidade', 'cidade');
          break;
        case 'uf':
          setText('uf', 'uf');
          break;
        case 'telefone':
          setMultivalue('telefone', 'telefone');
          break;
        case 'celular':
          setMultivalue('celular', 'celular');
          break;
        case 'contato':
          setMultivalue('contato', 'contato');
          break;
        case 'email':
          setMultivalue('email', 'email');
          break;
        case 'observacoesCadastro':
          setText('observacoesCadastro', 'observacoes_cadastro');
          break;
        default:
          break;
      }
    }
    return out;
  }

  private readonly profileKeys: (keyof CreateClienteDto)[] = [
    'tipoPessoa',
    'documento',
    'nomeFantasia',
    'ramoAtividade',
    'inscricaoEstadual',
    'cep',
    'endereco',
    'numero',
    'complemento',
    'bairro',
    'cidade',
    'uf',
    'telefone',
    'celular',
    'contato',
    'email',
    'observacoesCadastro',
  ];

  async create(dto: CreateClienteDto) {
    const sb = this.supabase.getClient();
    const profile = this.buildProfilePatch(dto, this.profileKeys);
    const { data, error } = await sb
      .from('Cliente')
      .insert({
        name: dto.name.trim(),
        active: dto.active ?? true,
        ...profile,
      })
      .select()
      .single();
    if (error) throw new Error(error.message);
    this.listCache.clear();
    return data;
  }

  async update(id: string, dto: UpdateClienteDto) {
    const sb = this.supabase.getClient();
    const { data: row } = await sb.from('Cliente').select('*').eq('id', id).single();
    if (!row) throw new NotFoundException('Cliente não encontrado');

    const upd: Record<string, unknown> = {};
    if (dto.name != null) upd.name = dto.name.trim();
    if (dto.active !== undefined) upd.active = dto.active;

    const profile = this.buildProfilePatch(dto, this.profileKeys);
    Object.assign(upd, profile);

    if (Object.keys(upd).length === 0) return row;

    const { data, error } = await sb.from('Cliente').update(upd).eq('id', id).select().single();
    if (error) throw new Error(error.message);
    this.listCache.clear();
    return data;
  }

  async remove(id: string) {
    const sb = this.supabase.getClient();
    const { data: row } = await sb.from('Cliente').select('id').eq('id', id).single();
    if (!row) throw new NotFoundException('Cliente não encontrado');
    await sb.from('Cliente').delete().eq('id', id);
    this.listCache.clear();
    return { id };
  }
}
