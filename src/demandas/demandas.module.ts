import { Module } from '@nestjs/common';
import { DemandasService } from './demandas.service';
import { DemandasController } from './demandas.controller';
import { DemandaVisibilityService } from './demanda-visibility.service';
import { RecorrenciaService } from './recorrencia.service';
import { TemplatesModule } from '../templates/templates.module';
import { AdminGuard } from '../auth/admin.guard';
import { DemandaDeleteGuard } from '../auth/demanda-delete.guard';
import { IaContextModule } from '../ia-context/ia-context.module';

@Module({
  imports: [TemplatesModule, IaContextModule],
  providers: [DemandasService, DemandaVisibilityService, RecorrenciaService, AdminGuard, DemandaDeleteGuard],
  controllers: [DemandasController],
  exports: [DemandasService, RecorrenciaService],
})
export class DemandasModule {}
