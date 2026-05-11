import { IsIn, IsOptional, IsString, MaxLength, MinLength } from 'class-validator';

const IA_CONTEXT_SCOPE_VALUES = ['geral', 'filtros_demandas', 'conferencia_mensagens'] as const;

export class UploadIaContextDto {
  @IsOptional()
  @IsString()
  @MinLength(2)
  @MaxLength(120)
  title?: string;

  @IsOptional()
  @IsString()
  @IsIn(IA_CONTEXT_SCOPE_VALUES)
  scope?: 'geral' | 'filtros_demandas' | 'conferencia_mensagens';
}
