import {
  IsString,
  IsBoolean,
  IsOptional,
  IsArray,
  IsUUID,
  IsDateString,
  IsEnum,
  MinLength,
} from 'class-validator';

export class RecorrenciaDto {
  @IsDateString()
  dataBase: string;

  @IsEnum(['diaria', 'semanal', 'quinzenal', 'mensal'])
  tipo: 'diaria' | 'semanal' | 'quinzenal' | 'mensal';

  @IsOptional()
  prazoReaberturaDias?: number;
}

export class CreateDemandaDto {
  @IsString()
  @MinLength(1)
  assunto: string;

  @IsOptional()
  @IsBoolean()
  prioridade?: boolean;

  @IsOptional()
  @IsDateString()
  prazo?: string;

  @IsOptional()
  @IsEnum(['em_aberto', 'em_andamento', 'concluido', 'standby', 'cancelado'])
  status?: 'em_aberto' | 'em_andamento' | 'concluido' | 'standby' | 'cancelado';

  @IsOptional()
  @IsString()
  observacoesGerais?: string;

  @IsOptional()
  @IsBoolean()
  isPrivada?: boolean;

  @IsOptional()
  @IsArray()
  @IsUUID('4', { each: true })
  privateViewerIds?: string[];

  @IsOptional()
  @IsBoolean()
  isRecorrente?: boolean;

  @IsOptional()
  setores?: string[]; // setorIds (UUIDs)

  @IsOptional()
  @IsArray()
  @IsUUID('4', { each: true })
  clienteIds?: string[];

  @IsOptional()
  @IsArray()
  responsaveis?: { userId: string; isPrincipal?: boolean }[];

  @IsOptional()
  @IsArray()
  subtarefas?: { titulo: string; responsavelUserId?: string }[];

  @IsOptional()
  recorrencia?: RecorrenciaDto;
}
