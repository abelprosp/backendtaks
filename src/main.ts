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
  const allowedOrigins = (process.env.FRONTEND_ORIGIN || 'http://localhost:3000')
    .split(',')
    .map((o) => o.trim().replace(/\/$/, ''))
    .filter(Boolean);
  const allowOrigin = (origin: string | undefined): string | false => {
    if (!origin) return false;
    const normalized = origin.replace(/\/$/, '');
    if (allowedOrigins.includes(normalized)) return origin;
    if (allowedOrigins.some((o) => normalized === o.replace(/\/$/, ''))) return origin;
    if (normalized.includes('luxustasks') && normalized.endsWith('vercel.app')) return origin;
    return false;
  };
  app.enableCors({
    origin: (origin, cb) => {
      const allowed = allowOrigin(origin);
      if (allowed) return cb(null, allowed);
      cb(null, false);
    },
    credentials: true,
    methods: ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'OPTIONS'],
    allowedHeaders: ['Content-Type', 'Authorization'],
  });
  const port = process.env.PORT || 4000;
  await app.listen(port);
  console.log(`Luxus Tasks API rodando em http://localhost:${port}`);
}
bootstrap();
