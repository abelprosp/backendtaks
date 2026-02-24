import { IsString, IsOptional, MinLength } from 'class-validator';

export class CreateSetorDto {
  @IsString()
  @MinLength(1)
  name: string;

  @IsOptional()
  @IsString()
  slug?: string;
}
