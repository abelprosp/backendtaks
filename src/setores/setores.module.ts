import { Module } from '@nestjs/common';
import { SetoresService } from './setores.service';
import { SetoresController } from './setores.controller';

@Module({
  providers: [SetoresService],
  controllers: [SetoresController],
  exports: [SetoresService],
})
export class SetoresModule {}
