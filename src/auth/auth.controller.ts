import { Body, Controller, Get, Post, Query, Req, UseGuards } from '@nestjs/common';
import { AuthService } from './auth.service';
import { JwtAuthGuard } from './jwt-auth.guard';
import { LoginDto } from './dto/login.dto';
import { RefreshTokenDto } from './dto/refresh-token.dto';

@Controller('auth')
export class AuthController {
  constructor(private authService: AuthService) {}

  @Post('login')
  async login(@Body() dto: LoginDto) {
    return this.authService.login(dto.email, dto.password);
  }

  @Post('refresh')
  async refresh(@Body() dto: RefreshTokenDto) {
    return this.authService.refresh(dto.refreshToken);
  }

  @UseGuards(JwtAuthGuard)
  @Post('me')
  async me(@Req() req: { user: unknown }) {
    return req.user;
  }

  @UseGuards(JwtAuthGuard)
  @Get('bootstrap')
  async bootstrap(
    @Req()
    req: {
      user: {
        id: string;
        email: string;
        name: string;
        roles?: { role: { id: string; name: string; slug: string } }[];
      };
    },
    @Query('includeSetores') includeSetores?: string,
    @Query('includeClientes') includeClientes?: string,
    @Query('allClientes') allClientes?: string,
    @Query('includeUsers') includeUsers?: string,
    @Query('fullUsers') fullUsers?: string,
    @Query('includeRoles') includeRoles?: string,
  ) {
    const isTrue = (value?: string) => value === 'true' || value === '1';
    return this.authService.bootstrap(req.user, {
      includeSetores: isTrue(includeSetores),
      includeClientes: isTrue(includeClientes),
      allClientes: isTrue(allClientes),
      includeUsers: isTrue(includeUsers),
      fullUsers: isTrue(fullUsers),
      includeRoles: isTrue(includeRoles),
    });
  }
}
