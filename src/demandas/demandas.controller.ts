import {
  BadRequestException,
  Body,
  Controller,
  Get,
  Post,
  Put,
  Param,
  Query,
  UseGuards,
  Req,
  Res,
  UseInterceptors,
  UploadedFile,
} from '@nestjs/common';
import { Response } from 'express';
import { FileInterceptor } from '@nestjs/platform-express';
import { DemandasService } from './demandas.service';
import { JwtAuthGuard } from '../auth/jwt-auth.guard';
import { CreateDemandaDto } from './dto/create-demanda.dto';
import { UpdateDemandaDto } from './dto/update-demanda.dto';
import { ListDemandasFiltersDto } from './dto/list-demandas-filters.dto';
import { CreateDemandaFromTemplateDto } from '../templates/dto/create-demanda-from-template.dto';
import { BuscarIaDto } from './dto/buscar-ia.dto';
import { stringToBool } from './utils';

@Controller('demandas')
@UseGuards(JwtAuthGuard)
export class DemandasController {
  constructor(private demandasService: DemandasService) {}

  @Post()
  create(@Req() req: { user: { id: string } }, @Body() dto: CreateDemandaDto) {
    return this.demandasService.create(req.user.id, dto);
  }

  @Post('from-template/:templateId')
  createFromTemplate(
    @Req() req: { user: { id: string } },
    @Param('templateId') templateId: string,
    @Body() dto: CreateDemandaFromTemplateDto,
  ) {
    return this.demandasService.createFromTemplate(req.user.id, templateId, dto);
  }

  @Post('buscar-ia')
  async buscarIa(
    @Req() req: { user: { id: string } },
    @Body() dto: BuscarIaDto,
  ) {
    return this.demandasService.buscarIa(req.user.id, dto.query);
  }

  @Get()
  async list(
    @Req() req: { user: { id: string } },
    @Query() query: ListDemandasFiltersDto,
  ) {
    const filters = { ...query, prioridade: stringToBool(query.prioridade) } as ListDemandasFiltersDto;
    return this.demandasService.list(req.user.id, filters);
  }

  @Get('export/excel')
  async exportExcel(
    @Req() req: { user: { id: string } },
    @Res() res: Response,
    @Query() query: ListDemandasFiltersDto,
  ) {
    const filters = { ...query, prioridade: stringToBool(query.prioridade) } as ListDemandasFiltersDto;
    const data = await this.demandasService.exportExcel(req.user.id, filters);
    res.setHeader('Content-Type', 'application/json');
    res.setHeader('Content-Disposition', 'attachment; filename=demandas.json');
    return res.send(JSON.stringify(data, null, 2));
  }

  @Get(':id')
  findOne(@Req() req: { user: { id: string } }, @Param('id') id: string) {
    return this.demandasService.findOne(req.user.id, id);
  }

  @Put(':id')
  update(
    @Req() req: { user: { id: string } },
    @Param('id') id: string,
    @Body() dto: UpdateDemandaDto,
  ) {
    return this.demandasService.update(req.user.id, id, dto);
  }

  @Post(':id/observacoes')
  addObservacao(
    @Req() req: { user: { id: string } },
    @Param('id') id: string,
    @Body() body: { texto: string },
  ) {
    return this.demandasService.addObservacao(req.user.id, id, body.texto);
  }

  @Post(':id/anexos')
  @UseInterceptors(FileInterceptor('file'))
  addAnexo(
    @Req() req: { user: { id: string } },
    @Param('id') id: string,
    @UploadedFile() file: Express.Multer.File,
  ) {
    if (!file) throw new BadRequestException('Envie um arquivo (campo "file")');
    return this.demandasService.addAnexo(req.user.id, id, file);
  }

  @Get(':id/anexos/:anexoId/download')
  async downloadAnexo(
    @Req() req: { user: { id: string } },
    @Param('id') id: string,
    @Param('anexoId') anexoId: string,
    @Res() res: Response,
  ) {
    const { path: filePath, filename, mimeType } = await this.demandasService.getAnexoForDownload(req.user.id, id, anexoId);
    res.setHeader('Content-Type', mimeType);
    res.setHeader('Content-Disposition', `attachment; filename="${encodeURIComponent(filename)}"`);
    return res.sendFile(filePath);
  }
}
