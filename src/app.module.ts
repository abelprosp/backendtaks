import { Module } from '@nestjs/common';
import { ConfigModule } from '@nestjs/config';
import { SupabaseModule } from './supabase/supabase.module';
import { AuthModule } from './auth/auth.module';
import { UsersModule } from './users/users.module';
import { SetoresModule } from './setores/setores.module';
import { ClientesModule } from './clientes/clientes.module';
import { DemandasModule } from './demandas/demandas.module';
import { TemplatesModule } from './templates/templates.module';
import { HealthModule } from './health/health.module';
import { IaContextModule } from './ia-context/ia-context.module';
import { AssistenteModule } from './assistente/assistente.module';

@Module({
  imports: [
    ConfigModule.forRoot({ isGlobal: true }),
    SupabaseModule,
    AuthModule,
    UsersModule,
    SetoresModule,
    ClientesModule,
    DemandasModule,
    TemplatesModule,
    HealthModule,
    IaContextModule,
    AssistenteModule,
  ],
})
export class AppModule {}
