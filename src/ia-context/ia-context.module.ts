import { Module } from '@nestjs/common';
import { IaContextService } from './ia-context.service';
import { IaContextController } from './ia-context.controller';
import { AdminGuard } from '../auth/admin.guard';

@Module({
  providers: [IaContextService, AdminGuard],
  controllers: [IaContextController],
  exports: [IaContextService],
})
export class IaContextModule {}
