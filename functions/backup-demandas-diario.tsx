import { createClient } from "@supabase/supabase-js";
import mysql from "mysql2/promise";

process.on("unhandledRejection", (reason) => {
  console.error("unhandledRejection:", reason);
  process.exit(1);
});
process.on("uncaughtException", (error) => {
  console.error("uncaughtException:", error);
  process.exit(1);
});

function requiredEnv(name: string): string {
  const value = (Bun.env[name] || "").trim();
  if (!value) {
    throw new Error(`Variável ${name} não configurada no serviço.`);
  }
  return value;
}

const supabase = createClient(
  requiredEnv("SUPABASE_URL"),
  requiredEnv("SUPABASE_SERVICE_ROLE_KEY"),
  { auth: { persistSession: false, autoRefreshToken: false } },
);

const TABLES = [
  "Demanda",
  "demanda_setor",
  "demanda_cliente",
  "demanda_responsavel",
  "subtarefa",
  "observacao",
  "anexo",
  "recorrencia_config",
  "demanda_evento",
  "luxus_parceiros_demanda",
] as const;

async function fetchTable(table: string): Promise<unknown[]> {
  const pageSize = table === "observacao" || table === "demanda_evento" ? 200 : 1000;
  const rows: unknown[] = [];
  let from = 0;

  while (true) {
    console.log(`[${table}] buscando ${from}..${from + pageSize - 1}`);
    const { data, error } = await supabase
      .from(table)
      .select("*")
      .range(from, from + pageSize - 1);

    if (error) {
      if (error.code === "PGRST205" || /does not exist|schema cache/i.test(error.message)) {
        console.warn(`[${table}] tabela ausente, pulando: ${error.message}`);
        return [];
      }
      throw new Error(`[${table}] ${error.message}`);
    }

    const batch = data ?? [];
    rows.push(...batch);
    console.log(`[${table}] acumulado ${rows.length}`);
    if (batch.length < pageSize) break;
    from += pageSize;
  }

  return rows;
}

async function backupDemandas() {
  let connection: mysql.Connection | undefined;
  try {
    console.log("Iniciando backup de demandas...");

    const tables: Record<string, unknown[]> = {};
    for (const table of TABLES) {
      tables[table] = await fetchTable(table);
    }

    const totalDemandas = tables.Demanda.length;
    console.log(`Demandas carregadas: ${totalDemandas}. Conectando no MySQL...`);

    connection = await mysql.createConnection({
      host: requiredEnv("MYSQL_HOST"),
      port: Number.parseInt(requiredEnv("MYSQL_PORT"), 10),
      user: requiredEnv("MYSQL_USER"),
      password: requiredEnv("MYSQL_PASSWORD"),
      database: requiredEnv("MYSQL_DATABASE"),
      connectTimeout: 20_000,
      enableKeepAlive: false,
    });

    const backup = {
      timestamp: new Date().toISOString(),
      totalDemandas,
      tables,
    };
    const backupJson = JSON.stringify(backup);
    console.log(`JSON gerado: ${Math.round(backupJson.length / 1024)} KB`);

    await connection.query(
      "INSERT INTO backup_demandas (data_backup, total_demandas, arquivo_json, status) VALUES (NOW(), ?, ?, 'sucesso')",
      [totalDemandas, backupJson],
    );
    await connection.query(
      "INSERT INTO backup_log (data_backup, total_demandas, status) VALUES (NOW(), ?, 'sucesso')",
      [totalDemandas],
    );

    console.log(`Backup concluído: ${totalDemandas} demandas salvas.`);
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    console.error("Erro no backup:", message);

    if (connection) {
      try {
        await connection.query(
          "INSERT INTO backup_log (data_backup, total_demandas, status, erro_mensagem) VALUES (NOW(), 0, 'erro', ?)",
          [message.slice(0, 4000)],
        );
      } catch (logError) {
        console.warn("Aviso ao registrar erro no log:", logError);
      }
    }

    throw error;
  } finally {
    if (connection) {
      await connection.end();
    }
  }
}

try {
  await backupDemandas();
  process.exit(0);
} catch (error) {
  console.error("Backup falhou:", error);
  process.exit(1);
}
