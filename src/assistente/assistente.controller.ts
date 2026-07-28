import { Body, Controller, Post, UseGuards } from '@nestjs/common';
import { JwtAuthGuard } from '../auth/jwt-auth.guard';
import { AssistenteService } from './assistente.service';
import { GerarMensagemDto } from './dto/gerar-mensagem.dto';
import { RevisarMensagemDto } from './dto/revisar-mensagem.dto';

@Controller('assistente')
@UseGuards(JwtAuthGuard)
export class AssistenteController {
  constructor(private readonly assistenteService: AssistenteService) {}

  @Post('gerar-mensagem')
  gerarMensagem(@Body() dto: GerarMensagemDto) {
    return this.assistenteService.gerarMensagem(dto);
  }

  @Post('revisar-mensagem')
  revisarMensagem(@Body() dto: RevisarMensagemDto) {
    return this.assistenteService.revisarMensagem(dto);
  }
}
