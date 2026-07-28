param(
  [string]$LegacyEmail = 'thomas@luxustelefonia.com.br',
  [string]$LegacyPassword = 'thomas26',
  [int]$Concurrency = 2,
  [int]$DemandBatchSize = 10
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location (Split-Path -Parent $scriptDir)

$env:LEGACY_EMAIL = $LegacyEmail
$env:LEGACY_PASSWORD = $LegacyPassword

Write-Host 'Fase 1/2: usuarios, clientes e templates (modo leve, sem anexos)...'
node scripts/legacy-migrate.mjs `
  --apply `
  --phases=users,clients,templates `
  --concurrency=$Concurrency `
  --no-snapshot

Write-Host ''
Write-Host 'Fase 2/2: demandas sem anexos (modo leve, incremental e sem duplicar)...'
node scripts/legacy-migrate.mjs `
  --apply `
  --phases=demandas `
  --concurrency=$Concurrency `
  --demand-batch-size=$DemandBatchSize `
  --skip-anexos `
  --no-snapshot
