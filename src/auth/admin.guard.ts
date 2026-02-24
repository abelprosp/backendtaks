import {
  Injectable,
  CanActivate,
  ExecutionContext,
  ForbiddenException,
} from '@nestjs/common';

type UserWithRoles = {
  roles?: { role?: { slug?: string } }[];
};

@Injectable()
export class AdminGuard implements CanActivate {
  canActivate(context: ExecutionContext): boolean {
    const req = context.switchToHttp().getRequest();
    const user = req.user as UserWithRoles | undefined;
    const isAdmin =
      user?.roles?.some((r) => r.role?.slug === 'admin') ?? false;
    if (!isAdmin) {
      throw new ForbiddenException(
        'Apenas usuário master (administrador) pode realizar esta ação.',
      );
    }
    return true;
  }
}
