-- ============================================================
-- Luxus Tasks - Criar usuário master
-- Execute no SQL Editor do Supabase (após schema.sql e seed.sql).
-- Login: redobrai@gmail.com / Amocarro4587@
-- ============================================================

-- Habilita extensão para bcrypt (Supabase já costuma ter)
CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- Insere o usuário master (senha hasheada com bcrypt)
INSERT INTO "User" ("id", "email", "password_hash", "name", "active", "created_at", "updated_at")
VALUES (
  gen_random_uuid(),
  'redobrai@gmail.com',
  crypt('Amocarro4587@', gen_salt('bf')),
  'Master',
  true,
  CURRENT_TIMESTAMP,
  CURRENT_TIMESTAMP
)
ON CONFLICT ("email") DO UPDATE SET
  "password_hash" = EXCLUDED."password_hash",
  "name" = EXCLUDED."name",
  "active" = EXCLUDED."active",
  "updated_at" = CURRENT_TIMESTAMP;

-- Atribui o perfil Administrador ao usuário master
INSERT INTO "user_role" ("user_id", "role_id")
SELECT u."id", r."id"
FROM "User" u
CROSS JOIN "Role" r
WHERE u."email" = 'redobrai@gmail.com'
  AND r."slug" = 'admin'::"RoleSlug"
ON CONFLICT ("user_id", "role_id") DO NOTHING;
