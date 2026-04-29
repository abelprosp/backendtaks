import { BadRequestException, Injectable } from '@nestjs/common';
import * as crypto from 'crypto';
import * as fs from 'fs';
import OpenAI from 'openai';
import * as path from 'path';
import mammoth from 'mammoth';
import { PDFParse } from 'pdf-parse';

export type IaContextScope = 'geral' | 'filtros_demandas' | 'conferencia_mensagens';

type IaContextRecord = {
  id: string;
  title: string;
  scope: IaContextScope;
  originalFilename: string;
  mimeType: string;
  size: number;
  extractedChars: number;
  extractedText: string;
  uploadedByUserId: string;
  uploadedAt: string;
};

@Injectable()
export class IaContextService {
  private readonly maxFileSizeBytes = 10 * 1024 * 1024;
  private readonly baseDir = path.join(process.cwd(), 'uploads', 'ia-context');
  private readonly storageFile = path.join(this.baseDir, 'knowledge.json');
  private readonly allowedExtensions = ['.pdf', '.txt', '.docx', '.png', '.jpg', '.jpeg', '.webp'];

  async ingestFile(
    userId: string,
    file: Express.Multer.File,
    payload?: { title?: string; scope?: IaContextScope },
  ): Promise<{ id: string; message: string; scope: IaContextScope; extractedChars: number }> {
    if (!file?.buffer?.length) {
      throw new BadRequestException('nao foi possivel processar o arquivo, tente novamente');
    }

    if (file.size > this.maxFileSizeBytes) {
      throw new BadRequestException('Arquivo excede o limite de 10MB.');
    }

    const extension = this.extensionOf(file.originalname);
    if (!this.allowedExtensions.includes(extension)) {
      throw new BadRequestException('Tipo de arquivo invalido. Envie PDF, TXT, DOCX ou imagem.');
    }

    const text = (await this.extractText(file.buffer, extension)).replace(/\s+/g, ' ').trim();
    if (!text || text.length < 20) {
      throw new BadRequestException('nao foi possivel processar o arquivo, tente novamente');
    }

    const records = this.readKnowledge();
    const id = crypto.randomUUID();
    const scope: IaContextScope = payload?.scope ?? 'geral';
    const title = (payload?.title || this.basenameWithoutExt(file.originalname) || 'Contexto IA').slice(0, 120);
    const record: IaContextRecord = {
      id,
      title,
      scope,
      originalFilename: file.originalname,
      mimeType: file.mimetype || this.mimeTypeFromExtension(extension),
      size: file.size,
      extractedChars: text.length,
      extractedText: text,
      uploadedByUserId: userId,
      uploadedAt: new Date().toISOString(),
    };

    records.unshift(record);
    this.writeKnowledge(records);

    return {
      id,
      scope,
      extractedChars: text.length,
      message: 'contexto recebido e aprendido',
    };
  }

  list(): Omit<IaContextRecord, 'extractedText'>[] {
    return this.readKnowledge().map(({ extractedText, ...rest }) => rest);
  }

  remove(id: string): { removed: boolean } {
    const records = this.readKnowledge();
    const next = records.filter((r) => r.id !== id);
    if (next.length === records.length) return { removed: false };
    this.writeKnowledge(next);
    return { removed: true };
  }

  buildRelevantContext(query: string, useCase: 'filtros_demandas' | 'conferencia_mensagens' | 'geral' = 'geral'): string {
    const q = String(query || '').toLowerCase().trim();
    const records = this.readKnowledge().filter((r) => r.scope === 'geral' || r.scope === useCase);
    if (!records.length) return '';

    const scored = records
      .map((record) => {
        const textLower = record.extractedText.toLowerCase();
        const terms = q.split(/\s+/).filter((t) => t.length >= 3);
        const score = terms.reduce((acc, term) => acc + (textLower.includes(term) ? 1 : 0), 0);
        return { record, score };
      })
      .sort((a, b) => b.score - a.score || Date.parse(b.record.uploadedAt) - Date.parse(a.record.uploadedAt))
      .slice(0, 3);

    return scored
      .map(({ record }) => `Fonte: ${record.title} (${record.scope})\n${record.extractedText.slice(0, 1800)}`)
      .join('\n\n---\n\n');
  }

  private ensureStorage() {
    if (!fs.existsSync(this.baseDir)) fs.mkdirSync(this.baseDir, { recursive: true });
    if (!fs.existsSync(this.storageFile)) fs.writeFileSync(this.storageFile, '[]', 'utf-8');
  }

  private readKnowledge(): IaContextRecord[] {
    this.ensureStorage();
    try {
      const raw = fs.readFileSync(this.storageFile, 'utf-8');
      const parsed = JSON.parse(raw);
      return Array.isArray(parsed) ? (parsed as IaContextRecord[]) : [];
    } catch {
      return [];
    }
  }

  private writeKnowledge(records: IaContextRecord[]) {
    this.ensureStorage();
    fs.writeFileSync(this.storageFile, JSON.stringify(records, null, 2), 'utf-8');
  }

  private extensionOf(filename: string): string {
    return path.extname(String(filename || '')).toLowerCase();
  }

  private basenameWithoutExt(filename: string): string {
    return path.basename(filename, path.extname(filename));
  }

  private mimeTypeFromExtension(extension: string): string {
    if (extension === '.pdf') return 'application/pdf';
    if (extension === '.docx') return 'application/vnd.openxmlformats-officedocument.wordprocessingml.document';
    if (extension === '.png') return 'image/png';
    if (extension === '.jpg' || extension === '.jpeg') return 'image/jpeg';
    if (extension === '.webp') return 'image/webp';
    return 'text/plain';
  }

  private async extractText(buffer: Buffer, extension: string): Promise<string> {
    if (extension === '.txt') return buffer.toString('utf-8');
    if (extension === '.pdf') {
      const parser = new PDFParse({ data: buffer });
      const result = await parser.getText();
      await parser.destroy();
      return result.text || '';
    }
    if (extension === '.docx') {
      const result = await mammoth.extractRawText({ buffer });
      return result.value || '';
    }
    if (['.png', '.jpg', '.jpeg', '.webp'].includes(extension)) {
      return this.extractTextFromImage(buffer, this.mimeTypeFromExtension(extension));
    }
    return '';
  }

  private async extractTextFromImage(buffer: Buffer, mimeType: string): Promise<string> {
    const apiKey = process.env.OPENAI_API_KEY;
    if (!apiKey?.trim()) {
      throw new BadRequestException('Nao foi possivel processar imagem sem OPENAI_API_KEY configurada.');
    }

    try {
      const openai = new OpenAI({ apiKey });
      const dataUrl = `data:${mimeType};base64,${buffer.toString('base64')}`;
      const completion = await openai.chat.completions.create({
        model: 'gpt-4o-mini',
        temperature: 0,
        messages: [
          {
            role: 'system',
            content: 'Extraia texto, instrucoes e referencias uteis da imagem. Se houver pouco texto, descreva objetivamente o conteudo visual para uso como contexto operacional interno. Responda apenas com o conteudo interpretado, sem markdown.',
          },
          {
            role: 'user',
            content: [
              { type: 'text', text: 'Leia esta imagem e transforme o conteudo em texto util para contexto interno do sistema.' },
              { type: 'image_url', image_url: { url: dataUrl } },
            ],
          },
        ],
      });

      return completion.choices?.[0]?.message?.content?.trim() || '';
    } catch {
      throw new BadRequestException('nao foi possivel processar o arquivo, tente novamente');
    }
  }
}
