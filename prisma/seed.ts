import { PrismaClient } from '@prisma/client';

const prisma = new PrismaClient();

const SETORES = [
  { name: 'Assessoria Fixa', slug: 'assessoria_fixa' },
  { name: 'Assessoria Móvel', slug: 'assessoria_movel' },
  { name: 'Comercial', slug: 'comercial' },
  { name: 'Corretora', slug: 'corretora' },
  { name: 'Financeiro', slug: 'financeiro' },
  { name: 'Gestão', slug: 'gestao' },
  { name: 'Jurídico', slug: 'juridico' },
  { name: 'Marketing', slug: 'marketing' },
  { name: 'TI', slug: 'ti' },
  { name: 'Outro', slug: 'outro' },
];

const ROLES = [
  { name: 'Administrador', slug: 'admin' },
  { name: 'Gestor', slug: 'gestor' },
  { name: 'Colaborador', slug: 'colaborador' },
  { name: 'Cliente', slug: 'cliente' },
];

async function main() {
  for (const s of SETORES) {
    await prisma.setor.upsert({
      where: { slug: s.slug },
      create: s,
      update: { name: s.name },
    });
  }
  for (const r of ROLES) {
    await prisma.role.upsert({
      where: { slug: r.slug as any },
      create: r as any,
      update: { name: r.name },
    });
  }
  console.log('Seed: setores e roles criados.');
}

main()
  .catch((e) => {
    console.error(e);
    process.exit(1);
  })
  .finally(() => prisma.$disconnect());
