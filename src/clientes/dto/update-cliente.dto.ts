import { IsString, IsOptional, IsBoolean, MinLength, MaxLength } from 'class-validator';

export class UpdateClienteDto {
  @IsOptional()
  @IsString()
  @MinLength(1)
  @MaxLength(500)
  name?: string;

  @IsOptional()
  @IsBoolean()
  active?: boolean;

  @IsOptional()
  @IsString()
  @MaxLength(8)
  tipoPessoa?: string;

  @IsOptional()
  @IsString()
  @MaxLength(32)
  documento?: string;

  @IsOptional()
  @IsString()
  @MaxLength(500)
  nomeFantasia?: string;

  @IsOptional()
  @IsString()
  @MaxLength(500)
  ramoAtividade?: string;

  @IsOptional()
  @IsString()
  @MaxLength(120)
  inscricaoEstadual?: string;

  @IsOptional()
  @IsString()
  @MaxLength(16)
  cep?: string;

  @IsOptional()
  @IsString()
  @MaxLength(500)
  endereco?: string;

  @IsOptional()
  @IsString()
  @MaxLength(50)
  numero?: string;

  @IsOptional()
  @IsString()
  @MaxLength(200)
  complemento?: string;

  @IsOptional()
  @IsString()
  @MaxLength(200)
  bairro?: string;

  @IsOptional()
  @IsString()
  @MaxLength(200)
  cidade?: string;

  @IsOptional()
  @IsString()
  @MaxLength(4)
  uf?: string;

  @IsOptional()
  @IsString()
  @MaxLength(4000)
  telefone?: string;

  @IsOptional()
  @IsString()
  @MaxLength(4000)
  celular?: string;

  @IsOptional()
  @IsString()
  @MaxLength(4000)
  contato?: string;

  @IsOptional()
  @IsString()
  @MaxLength(4000)
  email?: string;

  @IsOptional()
  @IsString()
  @MaxLength(8000)
  observacoesCadastro?: string;
}
