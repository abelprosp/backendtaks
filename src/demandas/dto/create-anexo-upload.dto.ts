import {
  IsBoolean,
  IsInt,
  IsOptional,
  IsString,
  Min,
  MinLength,
} from 'class-validator';

export class CreateAnexoUploadUrlDto {
  @IsString()
  @MinLength(1)
  filename: string;

  @IsOptional()
  @IsString()
  mimeType?: string;

  @IsInt()
  @Min(1)
  originalSize: number;

  @IsInt()
  @Min(1)
  uploadSize: number;

  @IsOptional()
  @IsBoolean()
  compressed?: boolean;
}

export class FinalizeAnexoUploadDto {
  @IsString()
  @MinLength(1)
  objectPath: string;

  @IsString()
  @MinLength(1)
  originalFilename: string;

  @IsOptional()
  @IsString()
  mimeType?: string;

  @IsInt()
  @Min(1)
  originalSize: number;

  @IsInt()
  @Min(1)
  uploadedSize: number;

  @IsOptional()
  @IsBoolean()
  compressed?: boolean;

  @IsOptional()
  @IsString()
  displayName?: string;
}
