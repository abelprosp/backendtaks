import { Injectable, UnauthorizedException } from '@nestjs/common';
import { JwtService } from '@nestjs/jwt';
import * as bcrypt from 'bcryptjs';
import { UsersService } from '../users/users.service';
import { SetoresService } from '../setores/setores.service';
import { ClientesService } from '../clientes/clientes.service';

export interface JwtPayload {
  sub: string;
  email: string;
  name?: string;
  roles?: { role: { id: string; name: string; slug: string } }[];
  type?: 'access' | 'refresh';
}

export interface TokenResponse {
  accessToken: string;
  refreshToken: string;
  expiresIn: string;
  refreshExpiresIn: string;
  user: {
    id: string;
    email: string;
    name: string;
    roles?: { role: { id: string; name: string; slug: string } }[];
  };
}

export interface BootstrapOptions {
  includeSetores?: boolean;
  includeClientes?: boolean;
  allClientes?: boolean;
  includeUsers?: boolean;
  fullUsers?: boolean;
  includeRoles?: boolean;
}

@Injectable()
export class AuthService {
  constructor(
    private usersService: UsersService,
    private setoresService: SetoresService,
    private clientesService: ClientesService,
    private jwtService: JwtService,
  ) {}

  private getAccessExpiresIn(): string {
    return process.env.JWT_EXPIRES_IN || '15m';
  }

  private getRefreshExpiresIn(): string {
    return process.env.REFRESH_EXPIRES_IN || '7d';
  }

  private getRefreshSecret(): string {
    return process.env.JWT_REFRESH_SECRET || process.env.JWT_SECRET || 'luxus-secret-change-me';
  }

  private buildTokenResponse(user: {
    id: string;
    email: string;
    name: string;
    roles?: { role: { id: string; name: string; slug: string } }[];
  }): TokenResponse {
    const accessExpiresIn = this.getAccessExpiresIn();
    const refreshExpiresIn = this.getRefreshExpiresIn();
    const roles = (user as { roles?: { role: { id: string; name: string; slug: string } }[] }).roles ?? [];
    const accessPayload: JwtPayload = { sub: user.id, email: user.email, name: user.name, roles, type: 'access' };
    const refreshPayload: JwtPayload = { sub: user.id, email: user.email, name: user.name, roles, type: 'refresh' };
    const accessToken = this.jwtService.sign(accessPayload, { expiresIn: accessExpiresIn });
    const refreshToken = this.jwtService.sign(refreshPayload, {
      expiresIn: refreshExpiresIn,
      secret: this.getRefreshSecret(),
    });
    return {
      accessToken,
      refreshToken,
      expiresIn: accessExpiresIn,
      refreshExpiresIn,
      user: {
        id: user.id,
        email: user.email,
        name: user.name,
        roles: roles.map((r) => ({ role: r.role })),
      },
    };
  }

  async validateUser(email: string, password: string) {
    const user = await this.usersService.findByEmail(email);
    if (!user || !user.active) return null;
    const ok = await bcrypt.compare(password, user.passwordHash);
    if (!ok) return null;
    const { passwordHash: _, ...result } = user;
    return result;
  }

  async login(email: string, password: string): Promise<TokenResponse> {
    const user = await this.validateUser(email, password);
    if (!user) throw new UnauthorizedException('Credenciais inválidas');
    return this.buildTokenResponse(user);
  }

  async refresh(refreshToken: string): Promise<TokenResponse> {
    if (!refreshToken?.trim()) {
      throw new UnauthorizedException('Refresh token ausente');
    }

    let payload: JwtPayload;
    try {
      payload = await this.jwtService.verifyAsync<JwtPayload>(refreshToken, {
        secret: this.getRefreshSecret(),
      });
    } catch {
      throw new UnauthorizedException('Refresh token inválido ou expirado');
    }

    if (payload.type !== 'refresh') {
      throw new UnauthorizedException('Token inválido para renovação');
    }

    const user = await this.usersService.findTokenUserById(payload.sub);
    if (!user || !user.active) {
      throw new UnauthorizedException('Usuário inválido');
    }

    return this.buildTokenResponse(user);
  }

  async bootstrap(
    user: {
      id: string;
      email: string;
      name: string;
      roles?: { role: { id: string; name: string; slug: string } }[];
    },
    options: BootstrapOptions = {},
  ) {
    const {
      includeSetores = false,
      includeClientes = false,
      allClientes = false,
      includeUsers = false,
      fullUsers = false,
      includeRoles = false,
    } = options;

    const [setores, clientes, users, roles] = await Promise.all([
      includeSetores ? this.setoresService.findAll() : Promise.resolve(undefined),
      includeClientes ? this.clientesService.findAll(!allClientes) : Promise.resolve(undefined),
      includeUsers
        ? fullUsers
          ? this.usersService.listAll()
          : this.usersService.listForDropdown()
        : Promise.resolve(undefined),
      includeRoles ? this.usersService.listRoles() : Promise.resolve(undefined),
    ]);

    return {
      user,
      ...(includeSetores ? { setores: setores ?? [] } : {}),
      ...(includeClientes ? { clientes: clientes ?? [] } : {}),
      ...(includeUsers ? { users: users ?? [] } : {}),
      ...(includeRoles ? { roles: roles ?? [] } : {}),
    };
  }
}
