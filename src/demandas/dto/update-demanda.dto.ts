import { IsString, IsBoolean, IsOptional, IsArray, IsUUID, IsDateString, IsEnum, MinLength } from 'class-validator';

export class UpdateDemandaDto {
  @IsOptional()
  @IsString()
  @MinLength(1)
  assunto?: string;

  @IsOptional()
  @IsBoolean()
  prioridade?: boolean;

  @IsOptional()
  @IsDateString()
  prazo?: string;

  @IsOptional()
  @IsEnum(['em_aberto', 'pendente', 'concluido', 'pendente_de_resposta'])
  status?: string;

  @IsOptional()
  @IsString()
  observacoesGerais?: string;

  @IsOptional()
  setores?: string[];

  @IsOptional()
  @IsArray()
  @IsUUID('4', { each: true })
  clienteIds?: string[];

  @IsOptional()
  @IsArray()
  responsaveis?: { userId: string; isPrincipal?: boolean }[];

  @IsOptional()
  @IsArray()
  subtarefas?: { titulo: string; concluida?: boolean }[];
}
