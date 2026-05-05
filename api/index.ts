import { NestFactory } from '@nestjs/core';
import { ExpressAdapter } from '@nestjs/platform-express';
import { AppModule } from '../src/app.module';
import { configureApp } from '../src/app.factory';

let cachedExpressApp: any = null;

async function bootstrapExpressApp() {
  if (cachedExpressApp) return cachedExpressApp;

  const express = await import('express');
  const expressFactory = (express as any).default ?? (express as any);
  const expressApp = expressFactory();
  const nestApp = await NestFactory.create(AppModule, new ExpressAdapter(expressApp), {
    bufferLogs: true,
    bodyParser: false,
  });
  await configureApp(nestApp);

  await nestApp.init();
  cachedExpressApp = expressApp;
  return cachedExpressApp;
}

export default async function handler(req: any, res: any) {
  const expressApp = await bootstrapExpressApp();
  return expressApp(req, res);
}
