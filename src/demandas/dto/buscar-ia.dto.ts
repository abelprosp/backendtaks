import { IsString, MinLength } from 'class-validator';

export class BuscarIaDto {
  @IsString()
  @MinLength(2, { message: 'Digite pelo menos 2 caracteres para buscar.' })
  query: string;
}
