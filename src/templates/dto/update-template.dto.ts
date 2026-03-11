import {
  IsString,
  IsBoolean,
  IsOptional,
  IsArray,
  IsUUID,
  IsEnum,
  IsInt,
  MinLength,
  Min,
} from 'class-validator';

export class UpdateTemplateDto {
  @IsOptional()
  @IsString()
  @MinLength(1)
  name?: string;

  @IsOptional()
  @IsString()
  descricao?: string;

  @IsOptional()
  @IsString()
  assuntoTemplate?: string;

  @IsOptional()
  @IsBoolean()
  prioridadeDefault?: boolean;

  @IsOptional()
  @IsString()
  observacoesGeraisTemplate?: string;

  @IsOptional()
  @IsBoolean()
  isRecorrenteDefault?: boolean;

  @IsOptional()
  @IsEnum(['diaria', 'semanal', 'quinzenal', 'mensal'])
  recorrenciaTipo?: 'diaria' | 'semanal' | 'quinzenal' | 'mensal';

  @IsOptional()
  @IsInt()
  @Min(0)
  recorrenciaPrazoReaberturaDias?: number;

  @IsOptional()
  @IsArray()
  @IsUUID('4', { each: true })
  setorIds?: string[];

  @IsOptional()
  @IsArray()
  responsaveis?: { userId: string; isPrincipal?: boolean }[];

  @IsOptional()
  @IsArray()
  subtarefas?: { titulo: string; ordem?: number }[];
}
