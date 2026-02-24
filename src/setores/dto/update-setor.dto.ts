import { IsString, IsOptional, MinLength } from 'class-validator';

export class UpdateSetorDto {
  @IsOptional()
  @IsString()
  @MinLength(1)
  name?: string;

  @IsOptional()
  @IsString()
  @MinLength(1)
  slug?: string;
}
