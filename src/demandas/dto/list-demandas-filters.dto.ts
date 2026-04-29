import { IsOptional, IsString, IsUUID, IsBoolean, IsDateString, IsEnum, IsArray, IsInt, Min, Max } from 'class-validator';
import { Transform, Type } from 'class-transformer';

function queryBoolean(value: unknown): boolean | undefined {
  if (value === undefined || value === null || value === '') return undefined;
  if (typeof value === 'boolean') return value;
  if (typeof value === 'string') {
    const normalized = value.trim().toLowerCase();
    if (['true', '1', 'sim'].includes(normalized)) return true;
    if (['false', '0', 'nao', 'não'].includes(normalized)) return false;
  }
  return undefined;
}

export class ListDemandasFiltersDto {
  @IsOptional()
  @IsUUID()
  clienteId?: string;

  @IsOptional()
  @IsString()
  assunto?: string;

  @IsOptional()
  @IsEnum(['em_aberto', 'em_andamento', 'concluido', 'standby', 'cancelado'])
  status?: string;

  @IsOptional()
  @Transform(({ value }) => queryBoolean(value))
  @IsBoolean()
  ocultarStandby?: boolean;

  @IsOptional()
  @IsEnum(['diaria', 'semanal', 'quinzenal', 'mensal'])
  tipoRecorrencia?: string;

  @IsOptional()
  @IsString()
  protocolo?: string;

  @IsOptional()
  @Transform(({ value }) => queryBoolean(value))
  @IsBoolean()
  prioridade?: boolean;

  @IsOptional()
  @IsUUID()
  criadorId?: string;

  @IsOptional()
  @IsUUID()
  responsavelPrincipalId?: string;

  @IsOptional()
  @Transform(({ value }) => queryBoolean(value))
  @IsBoolean()
  responsavelApenasPrincipal?: boolean;

  @IsOptional()
  @IsArray()
  @IsUUID('4', { each: true })
  setorIds?: string[];

  @IsOptional()
  @IsEnum(['vencido', 'no_prazo', 'finalizada'])
  condicaoPrazo?: 'vencido' | 'no_prazo' | 'finalizada';

  @IsOptional()
  @IsString()
  pesquisarTarefaOuObservacao?: string;

  @IsOptional()
  @IsString()
  pesquisaGeral?: string;

  @IsOptional()
  @IsDateString()
  dataCriacaoDe?: string;

  @IsOptional()
  @IsDateString()
  dataCriacaoAte?: string;

  @IsOptional()
  @IsDateString()
  prazoDe?: string;

  @IsOptional()
  @IsDateString()
  prazoAte?: string;

  @IsOptional()
  @Type(() => Number)
  @IsInt()
  @Min(1)
  page?: number;

  @IsOptional()
  @Type(() => Number)
  @IsInt()
  @Min(1)
  @Max(200)
  pageSize?: number;
}
