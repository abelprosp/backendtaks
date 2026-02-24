import { Module } from '@nestjs/common';
import { ClientesService } from './clientes.service';
import { ClientesController } from './clientes.controller';

@Module({
  providers: [ClientesService],
  controllers: [ClientesController],
  exports: [ClientesService],
})
export class ClientesModule {}
