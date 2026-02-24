import { Module } from '@nestjs/common';
import { DemandasService } from './demandas.service';
import { DemandasController } from './demandas.controller';
import { DemandaVisibilityService } from './demanda-visibility.service';
import { RecorrenciaService } from './recorrencia.service';
import { TemplatesModule } from '../templates/templates.module';

@Module({
  imports: [TemplatesModule],
  providers: [DemandasService, DemandaVisibilityService, RecorrenciaService],
  controllers: [DemandasController],
  exports: [DemandasService, RecorrenciaService],
})
export class DemandasModule {}
