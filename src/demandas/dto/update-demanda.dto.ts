import { Transform } from 'class-transformer';
import { IsString, IsBoolean, IsOptional, IsArray, IsUUID, IsDateString, IsEnum, MinLength, ValidateIf } from 'class-validator';

export class UpdateDemandaDto {
  @IsOptional()
  @IsString()
  @MinLength(1)
  assunto?: string;

  @IsOptional()
  @IsBoolean()
  prioridade?: boolean;

  @IsOptional()
  @Transform(({ value }) => (value === '' ? null : value))
  @ValidateIf((_, v) => v != null)
  @IsDateString()
  prazo?: string | null;

  @IsOptional()
  @IsEnum(['em_aberto', 'em_andamento', 'concluido', 'standby', 'cancelado'])
  status?: string;

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
  subtarefas?: { id?: string; titulo: string; concluida?: boolean; ordem?: number; responsavelUserId?: string }[];

  @IsOptional()
  @IsBoolean()
  isRecorrente?: boolean;

  @IsOptional()
  recorrencia?: {
    dataBase: string;
    tipo: 'diaria' | 'semanal' | 'quinzenal' | 'mensal';
    prazoReaberturaDias?: number;
  };
}
