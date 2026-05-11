import { IsOptional, IsString, MinLength } from 'class-validator';

export class GerarMensagemDto {
  @IsString()
  @MinLength(5)
  descricaoBruta!: string;

  @IsOptional()
  @IsString()
  canal?: string;

  @IsOptional()
  @IsString()
  objetivo?: string;

  @IsOptional()
  @IsString()
  tom?: string;

  @IsOptional()
  @IsString()
  instrucoesAdicionais?: string;
}
