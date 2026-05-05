import { Module } from '@nestjs/common';
import { AssistenteController } from './assistente.controller';
import { AssistenteService } from './assistente.service';

@Module({
  controllers: [AssistenteController],
  providers: [AssistenteService],
})
export class AssistenteModule {}
