import { IsOptional, IsString, IsUUID, IsBoolean, IsDateString, IsEnum, IsArray } from 'class-validator';
import { Type } from 'class-transformer';

export class ListDemandasFiltersDto {
  @IsOptional()
  @IsUUID()
  clienteId?: string;

  @IsOptional()
  @IsString()
  assunto?: string;

  @IsOptional()
  @IsEnum(['em_aberto', 'concluido', 'pendente', 'pendente_de_resposta'])
  status?: string;

  @IsOptional()
  @IsEnum(['diaria', 'semanal', 'quinzenal', 'mensal'])
  tipoRecorrencia?: string;

  @IsOptional()
  @IsString()
  protocolo?: string;

  @IsOptional()
  @Type(() => Boolean)
  @IsBoolean()
  prioridade?: boolean;

  @IsOptional()
  @IsUUID()
  criadorId?: string;

  @IsOptional()
  @IsUUID()
  responsavelPrincipalId?: string;

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
}
