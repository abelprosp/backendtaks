import { ValidationPipe } from '@nestjs/common';
import { NestFactory } from '@nestjs/core';
import { ExpressAdapter } from '@nestjs/platform-express';
import { AppModule } from '../src/app.module';

let cachedExpressApp: any = null;

function buildAllowedOrigins(): string[] {
  return (process.env.FRONTEND_ORIGIN || 'http://localhost:3000')
    .split(',')
    .map((o) => o.trim().replace(/\/$/, ''))
    .filter(Boolean);
}

function resolveAllowedOrigin(
  origin: string | undefined,
  allowedOrigins: string[],
): string | false {
  if (!origin) return false;
  const normalized = origin.replace(/\/$/, '');
  if (allowedOrigins.includes(normalized)) return origin;
  if (allowedOrigins.some((o) => normalized === o.replace(/\/$/, ''))) return origin;
  if (normalized.includes('luxustasks') && normalized.endsWith('vercel.app')) return origin;
  return false;
}

async function bootstrapExpressApp() {
  if (cachedExpressApp) return cachedExpressApp;

  const express = await import('express');
  const expressFactory = (express as any).default ?? (express as any);
  const expressApp = expressFactory();
  const nestApp = await NestFactory.create(AppModule, new ExpressAdapter(expressApp));

  nestApp.useGlobalPipes(
    new ValidationPipe({
      whitelist: true,
      transform: true,
      transformOptions: { enableImplicitConversion: true },
    }),
  );

  const allowedOrigins = buildAllowedOrigins();
  nestApp.enableCors({
    origin: (origin, cb) => {
      const allowed = resolveAllowedOrigin(origin, allowedOrigins);
      if (allowed) return cb(null, allowed);
      cb(null, false);
    },
    credentials: true,
    methods: ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'OPTIONS'],
    allowedHeaders: ['Content-Type', 'Authorization'],
  });

  await nestApp.init();
  cachedExpressApp = expressApp;
  return cachedExpressApp;
}

export default async function handler(req: any, res: any) {
  const expressApp = await bootstrapExpressApp();
  return expressApp(req, res);
}
