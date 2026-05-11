import { Type } from 'class-transformer';
import { IsIn, IsObject, IsOptional, IsString, MinLength, ValidateNested } from 'class-validator';

const IA_SCOPE_VALUES = [
  'all',
  'demandas',
  'setores',
  'clientes',
  'templates',
  'usuarios',
  'paginas',
  'observacoes_gerais',
  'status',
] as const;

export class BuscarIaContextDto {
  @IsOptional()
  @IsString()
  previousQuery?: string;

  @IsOptional()
  @IsString()
  previousScope?: string;

  @IsOptional()
  @IsString()
  previousSearchTerm?: string;

  @IsOptional()
  @IsObject()
  previousFilters?: Record<string, unknown>;
}

export class BuscarIaDto {
  @IsString()
  @MinLength(2, { message: 'Digite pelo menos 2 caracteres para buscar.' })
  query: string;

  @IsOptional()
  @IsString()
  @IsIn(IA_SCOPE_VALUES)
  scope?: string;

  @IsOptional()
  @ValidateNested()
  @Type(() => BuscarIaContextDto)
  context?: BuscarIaContextDto;
}
