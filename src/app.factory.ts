import { INestApplication, ValidationPipe } from '@nestjs/common';
import { AllExceptionsFilter } from './common/all-exceptions.filter';
import { getAllowedOrigins, resolveAllowedOrigin } from './common/runtime-config';

export async function configureApp(app: INestApplication) {
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
