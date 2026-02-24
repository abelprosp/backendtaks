import { IsString, IsOptional, IsBoolean, MinLength } from 'class-validator';

export class UpdateClienteDto {
  @IsOptional()
  @IsString()
  @MinLength(1)
  name?: string;

  @IsOptional()
  @IsBoolean()
  active?: boolean;
}
