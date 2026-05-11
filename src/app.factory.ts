import { INestApplication, ValidationPipe } from '@nestjs/common';
import { json, urlencoded } from 'express';
import { AllExceptionsFilter } from './common/all-exceptions.filter';
import { getAllowedOrigins, resolveAllowedOrigin } from './common/runtime-config';

export async function configureApp(app: INestApplication) {
  /** Aumenta o limite do body para acomodar prints colados em base64
   *  dentro de observações/instruções (default do express é 100kb). */
  const httpAdapter = app.getHttpAdapter();
  const expressApp = httpAdapter?.getInstance?.() as { use?: (...args: unknown[]) => unknown } | undefined;
  if (expressApp?.use) {
    expressApp.use(json({ limit: '25mb' }));
    expressApp.use(urlencoded({ extended: true, limit: '25mb' }));
  }

  app.useGlobalPipes(
    new ValidationPipe({
      whitelist: true,
      transform: true,
      transformOptions: { enableImplicitConversion: true },
    }),
  );
  app.useGlobalFilters(new AllExceptionsFilter());
  app.enableShutdownHooks();

  const allowedOrigins = getAllowedOrigins();
  app.enableCors({
    origin: (origin, cb) => {
      const allowed = resolveAllowedOrigin(origin, allowedOrigins);
      if (allowed) return cb(null, allowed);
      return cb(null, false);
    },
    credentials: true,
    methods: ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'OPTIONS'],
    allowedHeaders: ['Content-Type', 'Authorization'],
  });

  return { allowedOrigins };
}
