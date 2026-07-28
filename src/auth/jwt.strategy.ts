import { Injectable, UnauthorizedException } from '@nestjs/common';
import { PassportStrategy } from '@nestjs/passport';
import { ExtractJwt, Strategy } from 'passport-jwt';
import { ConfigService } from '@nestjs/config';
import { AuthService, JwtPayload } from './auth.service';
import { UsersService } from '../users/users.service';

@Injectable()
export class JwtStrategy extends PassportStrategy(Strategy, 'jwt') {
  constructor(
    private config: ConfigService,
    private usersService: UsersService,
  ) {
    super({
      jwtFromRequest: ExtractJwt.fromAuthHeaderAsBearerToken(),
      ignoreExpiration: false,
      secretOrKey: config.get<string>('JWT_SECRET', 'luxus-secret-change-me'),
    });
  }

  async validate(payload: JwtPayload) {
    if (payload.type && payload.type !== 'access') throw new UnauthorizedException();
    const user = await this.usersService.findAuthSnapshot(payload.sub);
    if (!user || !user.active) throw new UnauthorizedException();
    return {
      id: user.id,
      email: user.email,
      name: payload.name ?? user.name,
      roles: payload.roles ?? [],
    };
  }
}
