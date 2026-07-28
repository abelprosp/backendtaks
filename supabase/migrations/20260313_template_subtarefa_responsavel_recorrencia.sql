-- Template: responsável por subtarefa e data base padrão de recorrência.
-- Demanda: responsável opcional por subtarefa para preservar o vínculo ao gerar a partir do template.

alter table "Template"
  add column if not exists "recorrencia_data_base_default" date;

alter table "template_subtarefa"
  add column if not exists "responsavel_user_id" uuid references "User"("id") on delete set null;

alter table "subtarefa"
  add column if not exists "responsavel_user_id" uuid references "User"("id") on delete set null;

create index if not exists idx_template_subtarefa_responsavel_user_id
  on "template_subtarefa" ("responsavel_user_id");

create index if not exists idx_subtarefa_responsavel_user_id
  on "subtarefa" ("responsavel_user_id");
