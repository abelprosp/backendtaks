import {
  Body,
  Controller,
  Delete,
  Get,
  Param,
  Post,
  Put,
  Req,
  UseGuards,
} from '@nestjs/common';
import { TemplatesService } from './templates.service';
import { JwtAuthGuard } from '../auth/jwt-auth.guard';
import { CreateTemplateDto } from './dto/create-template.dto';
import { UpdateTemplateDto } from './dto/update-template.dto';

@Controller('templates')
@UseGuards(JwtAuthGuard)
export class TemplatesController {
  constructor(private templatesService: TemplatesService) {}

  @Post()
  create(@Req() req: { user: { id: string } }, @Body() dto: CreateTemplateDto) {
    return this.templatesService.create(req.user.id, dto);
  }

  @Get()
  findAll() {
    return this.templatesService.findAll();
  }

  @Get(':id')
  findOne(@Param('id') id: string) {
    return this.templatesService.findOne(id);
  }

  @Put(':id')
  update(
    @Req() req: { user: { id: string } },
    @Param('id') id: string,
    @Body() dto: UpdateTemplateDto,
  ) {
    return this.templatesService.update(req.user.id, id, dto);
  }

  @Delete(':id')
  remove(@Param('id') id: string) {
    return this.templatesService.remove(id);
  }
}
