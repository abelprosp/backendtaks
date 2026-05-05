import { Injectable, ServiceUnavailableException } from '@nestjs/common';
import OpenAI from 'openai';
import { GerarMensagemDto } from './dto/gerar-mensagem.dto';
import { RevisarMensagemDto } from './dto/revisar-mensagem.dto';

type AssistenteCanal = 'whatsapp' | 'email' | 'mensagem_geral';

@Injectable()
export class AssistenteService {
  private normalizeCanal(canal?: string): AssistenteCanal {
    const normalized = String(canal || '').trim().toLowerCase();
    if (normalized === 'email') return 'email';
    if (['whatsapp', 'whats', 'zap'].includes(normalized)) return 'whatsapp';
    return 'mensagem_geral';
  }

  async gerarMensagem(dto: GerarMensagemDto) {
    const canal = this.normalizeCanal(dto.canal);
    const cleaned = await this.executeJsonPrompt({
      systemInstruction:
        'Voce e um redator do Grupo Luxus. Transforme um briefing bruto em uma mensagem pronta para enviar ao cliente. ' +
        'Adapte a escrita ao canal informado. Para WhatsApp, escreva de forma humana, cordial, objetiva e facil de ler. ' +
        'Para e-mail, escreva com estrutura profissional, clara e organizada. Nao invente precos, prazos, politicas, promessas ou fatos. ' +
        'Se faltarem dados objetivos, escreva de forma segura e neutra. Use portugues do Brasil. ' +
        'Retorne apenas JSON valido no formato {"textoGerado":"...","resumo":"...","assuntoSugerido":"... ou null","observacoes":["..."]}.',
      payload: {
        canal,
        objetivo: dto.objetivo,
        tom: dto.tom,
        instrucoesAdicionais: dto.instrucoesAdicionais,
        descricaoBruta: dto.descricaoBruta.trim(),
      },
      temperature: 0.35,
      fallbackMessage: 'A IA de geracao nao respondeu corretamente no momento.',
    });

    const textoGerado = this.getString(cleaned, 'textoGerado') || this.getString(cleaned, 'texto') || '';
    if (!textoGerado.trim()) {
      throw new ServiceUnavailableException('A IA nao devolveu uma mensagem valida.');
    }

    return {
      canal,
      descricaoBruta: dto.descricaoBruta.trim(),
      textoGerado: textoGerado.trim(),
      resumo: this.getString(cleaned, 'resumo') || 'Mensagem gerada e ajustada para o canal informado.',
      assuntoSugerido: this.getNullableString(cleaned, 'assuntoSugerido'),
      observacoes: this.getStringArray(cleaned, 'observacoes'),
    };
  }

  async revisarMensagem(dto: RevisarMensagemDto) {
    const canal = this.normalizeCanal(dto.canal);
    const cleaned = await this.executeJsonPrompt({
      systemInstruction:
        'Voce e um revisor e redator do Grupo Luxus. Revise mensagens para clientes, corrigindo gramatica, ortografia, concordancia, pontuacao e clareza sem mudar a intencao principal. ' +
        'Adapte o texto ao canal informado. Para WhatsApp, mantenha tom humano, cordial, direto e facil de ler. Para e-mail, mantenha tom profissional, organizado e claro. ' +
        'Nao invente precos, prazos, politicas, promessas ou fatos que nao estejam no texto. Use portugues do Brasil. ' +
        'Retorne apenas JSON valido no formato {"textoRevisado":"...","resumo":"...","assuntoSugerido":"... ou null","observacoes":["..."]}.',
      payload: {
        canal,
        objetivo: dto.objetivo,
        instrucoesAdicionais: dto.instrucoesAdicionais,
        manterTomOriginal: dto.manterTomOriginal === true,
        texto: dto.texto.trim(),
      },
      temperature: 0.2,
      fallbackMessage: 'A IA de revisao nao respondeu corretamente no momento.',
    });

    const textoRevisado = this.getString(cleaned, 'textoRevisado') || this.getString(cleaned, 'texto') || '';
    if (!textoRevisado.trim()) {
      throw new ServiceUnavailableException('A IA nao devolveu um texto revisado valido.');
    }

    return {
      canal,
      textoOriginal: dto.texto.trim(),
      textoRevisado: textoRevisado.trim(),
      resumo: this.getString(cleaned, 'resumo') || 'Texto corrigido e ajustado para o canal informado.',
      assuntoSugerido: this.getNullableString(cleaned, 'assuntoSugerido'),
      observacoes: this.getStringArray(cleaned, 'observacoes'),
    };
  }

  private async executeJsonPrompt(args: {
    systemInstruction: string;
    payload: Record<string, unknown>;
    temperature: number;
    fallbackMessage: string;
  }): Promise<Record<string, unknown>> {
    const apiKey = process.env.OPENAI_API_KEY;
    if (!apiKey?.trim()) {
      throw new ServiceUnavailableException('IA nao configurada. Defina OPENAI_API_KEY no servidor.');
    }

    try {
      const openai = new OpenAI({ apiKey });
      const completion = await openai.chat.completions.create({
        model: process.env.OPENAI_MODEL || 'gpt-4.1-mini',
        temperature: args.temperature,
        messages: [
          { role: 'system', content: args.systemInstruction },
          { role: 'user', content: JSON.stringify(args.payload) },
        ],
      });

      const content = completion.choices?.[0]?.message?.content?.trim() || '{}';
      return this.parseJsonObject(content);
    } catch (error) {
      if (error instanceof ServiceUnavailableException) throw error;
      throw new ServiceUnavailableException(args.fallbackMessage);
    }
  }

  private parseJsonObject(content: string): Record<string, unknown> {
    const cleaned = content.replace(/^```json\s*/i, '').replace(/\s*```$/i, '').trim();
    try {
      const parsed = JSON.parse(cleaned);
      if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
        return parsed as Record<string, unknown>;
      }
    } catch {
      return { texto: cleaned };
    }
    return {};
  }

  private getString(payload: Record<string, unknown>, key: string): string | null {
    const value = payload[key];
    return typeof value === 'string' && value.trim() ? value.trim() : null;
  }

  private getNullableString(payload: Record<string, unknown>, key: string): string | null {
    const value = payload[key];
    return typeof value === 'string' && value.trim() ? value.trim() : null;
  }

  private getStringArray(payload: Record<string, unknown>, key: string): string[] {
    const value = payload[key];
    if (!Array.isArray(value)) return [];
    return value.map((item) => String(item || '').trim()).filter(Boolean);
  }
}
