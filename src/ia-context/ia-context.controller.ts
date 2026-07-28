import {
  BadRequestException,
  Body,
  Controller,
  Delete,
  Get,
  Param,
  Post,
  Req,
  UploadedFile,
  UseGuards,
  UseInterceptors,
} from '@nestjs/common';
import { FileInterceptor } from '@nestjs/platform-express';
import { memoryStorage } from 'multer';
import { JwtAuthGuard } from '../auth/jwt-auth.guard';
import { AdminGuard } from '../auth/admin.guard';
import { IaContextService } from './ia-context.service';
import { UploadIaContextDto } from './dto/upload-ia-context.dto';

@Controller('ia-context')
@UseGuards(JwtAuthGuard, AdminGuard)
export class IaContextController {
  constructor(private readonly iaContextService: IaContextService) {}

  @Post('upload')
  @UseInterceptors(
    FileInterceptor('file', {
      storage: memoryStorage(),
      limits: { fileSize: 10 * 1024 * 1024 },
    }),
  )
  async upload(
    @Req() req: { user: { id: string } },
    @UploadedFile() file: Express.Multer.File,
    @Body() dto: UploadIaContextDto,
  ) {
    if (!file) throw new BadRequestException('Envie um arquivo (campo "file").');
    try {
      return await this.iaContextService.ingestFile(req.user.id, file, {
        title: dto.title,
        scope: dto.scope,
      });
    } catch (error) {
      if (error instanceof BadRequestException) throw error;
      throw new BadRequestException('não foi possível processar o arquivo, tente novamente');
    }
  }

  @Get()
  list() {
    return this.iaContextService.list();
  }

  @Delete(':id')
  remove(@Param('id') id: string) {
    return this.iaContextService.remove(id);
  }
}
