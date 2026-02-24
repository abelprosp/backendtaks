import { NestFactory } from '@nestjs/core';
import { ValidationPipe } from '@nestjs/common';
import { AppModule } from './app.module';

async function bootstrap() {
  const app = await NestFactory.create(AppModule);
  app.useGlobalPipes(
    new ValidationPipe({
      whitelist: true,
      transform: true,
      transformOptions: { enableImplicitConversion: true },
    }),
  );
  const allowedOrigins = (process.env.FRONTEND_ORIGIN || 'http://localhost:3000').split(',').map((o) => o.trim());
  app.enableCors({ origin: allowedOrigins, credentials: true });
  const port = process.env.PORT || 4000;
  await app.listen(port);
  console.log(`Luxus Tasks API rodando em http://localhost:${port}`);
}
bootstrap();
