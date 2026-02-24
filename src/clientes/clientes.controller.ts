import { Body, Controller, Delete, Get, Param, Post, Put, Query, UseGuards } from '@nestjs/common';
import { ClientesService } from './clientes.service';
import { JwtAuthGuard } from '../auth/jwt-auth.guard';
import { CreateClienteDto } from './dto/create-cliente.dto';
import { UpdateClienteDto } from './dto/update-cliente.dto';

@Controller('clientes')
@UseGuards(JwtAuthGuard)
export class ClientesController {
  constructor(private clientesService: ClientesService) {}

  @Get()
  findAll(@Query('all') all?: string) {
    return this.clientesService.findAll(all !== 'true');
  }

  @Post()
  create(@Body() dto: CreateClienteDto) {
    return this.clientesService.create(dto);
  }

  @Put(':id')
  update(@Param('id') id: string, @Body() dto: UpdateClienteDto) {
    return this.clientesService.update(id, dto);
  }

  @Delete(':id')
  remove(@Param('id') id: string) {
    return this.clientesService.remove(id);
  }
}
