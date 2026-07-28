import { IsBoolean, IsOptional, IsString, MinLength } from 'class-validator';

export class RevisarMensagemDto {
  @IsString()
  @MinLength(5)
  texto!: string;

  @IsOptional()
  @IsString()
  canal?: string;

  @IsOptional()
  @IsString()
  objetivo?: string;

  @IsOptional()
  @IsString()
  instrucoesAdicionais?: string;

  @IsOptional()
  @IsBoolean()
  manterTomOriginal?: boolean;
}
