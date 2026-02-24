import { Injectable, OnModuleInit } from '@nestjs/common';
import { createClient, SupabaseClient } from '@supabase/supabase-js';

@Injectable()
export class SupabaseService implements OnModuleInit {
  private client: SupabaseClient | null = null;

  onModuleInit() {
    const url = process.env.SUPABASE_URL?.trim();
    const key = process.env.SUPABASE_SERVICE_ROLE_KEY?.trim();
    if (!url || !key) throw new Error('SUPABASE_URL e SUPABASE_SERVICE_ROLE_KEY são obrigatórios no .env');
    this.client = createClient(url, key, { auth: { persistSession: false } });
  }

  getClient(): SupabaseClient {
    if (!this.client) throw new Error('Supabase client não inicializado');
    return this.client;
  }
}
