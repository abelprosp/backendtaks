-- ============================================================
-- Atribui perfil Administrador (master) ao usuário redobrai@gmail.com
-- Execute no SQL Editor do Supabase. O usuário precisa já existir na tabela "User".
-- Depois faça login novamente para o sistema reconhecer o perfil e exibir o Dashboard.
-- ============================================================

INSERT INTO "user_role" ("user_id", "role_id")
SELECT u."id", r."id"
FROM "User" u
CROSS JOIN "Role" r
WHERE u."email" = 'redobrai@gmail.com'
  AND r."slug" = 'admin'::"RoleSlug"
ON CONFLICT ("user_id", "role_id") DO NOTHING;
