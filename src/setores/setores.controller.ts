import { Body, Controller, Delete, Get, Param, Post, Put, UseGuards } from '@nestjs/common';
import { SetoresService } from './setores.service';
import { JwtAuthGuard } from '../auth/jwt-auth.guard';
import { CreateSetorDto } from './dto/create-setor.dto';
import { UpdateSetorDto } from './dto/update-setor.dto';

@Controller('setores')
@UseGuards(JwtAuthGuard)
export class SetoresController {
  constructor(private setoresService: SetoresService) {}

  @Get()
  findAll() {
    return this.setoresService.findAll();
  }

  @Post()
  create(@Body() dto: CreateSetorDto) {
    return this.setoresService.create(dto);
  }

  @Put(':id')
  update(@Param('id') id: string, @Body() dto: UpdateSetorDto) {
    return this.setoresService.update(id, dto);
  }

  @Delete(':id')
  remove(@Param('id') id: string) {
    return this.setoresService.remove(id);
  }
}
