import { Controller, Get } from '@nestjs/common';
import { APP_NAME, APP_SLUG, getNodeEnv } from '../common/runtime-config';

@Controller('health')
export class HealthController {
  @Get()
  getHealth() {
    return {
      status: 'ok',
      service: APP_SLUG,
      name: APP_NAME,
      environment: getNodeEnv(),
      uptimeSeconds: Math.round(process.uptime()),
      timestamp: new Date().toISOString(),
    };
  }
}
