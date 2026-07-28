import {
  Injectable,
  CanActivate,
  ExecutionContext,
  ForbiddenException,
} from '@nestjs/common';

type UserWithRoles = {
  roles?: Array<{
    role?: { slug?: string };
    role_slug?: string;
  }>;
};

@Injectable()
export class AdminGuard implements CanActivate {
  canActivate(context: ExecutionContext): boolean {
    const req = context.switchToHttp().getRequest();
    const user = req.user as UserWithRoles | undefined;
    const isAdmin = user?.roles?.some((r) => r.role?.slug === 'admin' || r.role_slug === 'admin') ?? false;
    if (!isAdmin) {
      throw new ForbiddenException('Apenas usuarios ADM podem realizar esta acao.');
    }
    return true;
  }
}
