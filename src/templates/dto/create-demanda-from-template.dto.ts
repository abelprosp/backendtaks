import {
  IsString,
  IsBoolean,
  IsOptional,
  IsArray,
  IsUUID,
  IsDateString,
  MinLength,
} from 'class-validator';

/**
 * Particularidades ao criar uma demanda a partir de um template.
 * O resto (setores, responsáveis, subtarefas, recorrência) vem do template.
 */
export class CreateDemandaFromTemplateDto {
  @IsString()
  @MinLength(1)
  assunto: string;

  @IsOptional()
  @IsDateString()
  prazo?: string;

  @IsOptional()
  @IsBoolean()
  prioridade?: boolean;

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
  @IsArray()
  @IsUUID('4', { each: true })
  clienteIds?: string[];

  /** Sobrescrever responsáveis do template (opcional). Se não enviado, usa do template. */
  @IsOptional()
  @IsArray()
  responsaveis?: { userId: string; isPrincipal?: boolean }[];

  /** Sobrescrever setores do template (opcional). Se não enviado, usa do template. */
  @IsOptional()
  @IsArray()
  @IsUUID('4', { each: true })
  setorIds?: string[];

  /** Sobrescrever subtarefas do template (opcional). Se não enviado, usa do template. */
  @IsOptional()
  @IsArray()
  subtarefas?: { titulo: string; responsavelUserId?: string }[];

  /** Se demanda será recorrente; data base para a recorrência */
  @IsOptional()
  @IsDateString()
  recorrenciaDataBase?: string;
}
