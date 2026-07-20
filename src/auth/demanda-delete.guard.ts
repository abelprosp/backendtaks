import {
  CanActivate,
  ExecutionContext,
  ForbiddenException,
  Injectable,
} from '@nestjs/common';

const AUTHORIZED_DEMANDA_DELETE_EMAILS = new Set([
  'adriane@luxustelefonia.com.br',
]);

type UserWithDeletePermission = {
  email?: string | null;
  roles?: Array<{
    role?: { slug?: string | null };
    role_slug?: string | null;
  }>;
};

function hasAdminRole(user: UserWithDeletePermission | undefined): boolean {
  return user?.roles?.some((item) => item.role?.slug === 'admin' || item.role_slug === 'admin') ?? false;
}

@Injectable()
export class DemandaDeleteGuard implements CanActivate {
  canActivate(context: ExecutionContext): boolean {
    const req = context.switchToHttp().getRequest();
    const user = req.user as UserWithDeletePermission | undefined;
    const email = String(user?.email ?? '').trim().toLowerCase();

    if (hasAdminRole(user) || AUTHORIZED_DEMANDA_DELETE_EMAILS.has(email)) {
      return true;
    }

    throw new ForbiddenException(
      'Voce nao tem permissao para excluir demandas. Fale com um administrador para liberar este acesso.',
    );
  }
}
