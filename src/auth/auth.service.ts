import { Injectable, UnauthorizedException } from '@nestjs/common';
import { JwtService } from '@nestjs/jwt';
import * as bcrypt from 'bcrypt';
import { UsersService } from '../users/users.service';

export interface JwtPayload {
  sub: string;
  email: string;
}

export interface TokenResponse {
  accessToken: string;
  expiresIn: string;
  user: {
    id: string;
    email: string;
    name: string;
    roles?: { role: { id: string; name: string; slug: string } }[];
  };
}

@Injectable()
export class AuthService {
  constructor(
    private usersService: UsersService,
    private jwtService: JwtService,
  ) {}

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
    const payload: JwtPayload = { sub: user.id, email: user.email };
    const accessToken = this.jwtService.sign(payload);
    const expiresIn = process.env.JWT_EXPIRES_IN || '15m';
    const roles = (user as { roles?: { role: { id: string; name: string; slug: string } }[] }).roles ?? [];
    return {
      accessToken,
      expiresIn,
      user: {
        id: user.id,
        email: user.email,
        name: user.name,
        roles: roles.map((r) => ({ role: r.role })),
      },
    };
  }
}
