import {
  ArgumentsHost,
  Catch,
  ExceptionFilter,
  HttpException,
  HttpStatus,
  Logger,
} from '@nestjs/common';
import { Request, Response } from 'express';

@Catch()
export class AllExceptionsFilter implements ExceptionFilter {
  private readonly logger = new Logger(AllExceptionsFilter.name);

  catch(exception: unknown, host: ArgumentsHost) {
    const ctx = host.switchToHttp();
    const request = ctx.getRequest<Request>();
    const response = ctx.getResponse<Response>();

    const status =
      exception instanceof HttpException
        ? exception.getStatus()
        : HttpStatus.INTERNAL_SERVER_ERROR;
    const payload = exception instanceof HttpException ? exception.getResponse() : null;
    const message = this.extractMessage(payload, exception);

    const logContext = `${request.method} ${request.originalUrl || request.url}`;
    if (status >= 500) {
      this.logger.error(message, exception instanceof Error ? exception.stack : undefined, logContext);
    } else {
      this.logger.warn(`${status} ${logContext} - ${message}`);
    }

    response.status(status).json({
      statusCode: status,
      message,
      path: request.originalUrl || request.url,
      timestamp: new Date().toISOString(),
    });
  }

  private extractMessage(payload: unknown, exception: unknown): string {
    if (typeof payload === 'string' && payload.trim()) return payload;
    if (payload && typeof payload === 'object') {
      const message = (payload as { message?: unknown }).message;
      if (Array.isArray(message)) return message.map(String).join(', ');
      if (typeof message === 'string' && message.trim()) return message;
    }
    if (exception instanceof Error && exception.message.trim()) return exception.message;
    return 'Erro interno do servidor';
  }
}
