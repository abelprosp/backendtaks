import { Logger } from '@nestjs/common';
import { NestFactory } from '@nestjs/core';
import { AppModule } from './app.module';
import { configureApp } from './app.factory';
import { APP_NAME, getNodeEnv, getPort } from './common/runtime-config';

async function bootstrap() {
  const logger = new Logger('Bootstrap');
  const app = await NestFactory.create(AppModule, { bufferLogs: true });
  const { allowedOrigins } = await configureApp(app);

  const port = getPort();
  await app.listen(port, '0.0.0.0');

  logger.log(`${APP_NAME} iniciada na porta ${port} (${getNodeEnv()})`);
  logger.log(`CORS habilitado para: ${allowedOrigins.join(', ')}`);
}

bootstrap().catch((error: unknown) => {
  const logger = new Logger('Bootstrap');
  logger.error(
    'Falha ao iniciar a API',
    error instanceof Error ? error.stack : String(error),
  );
  process.exit(1);
});
