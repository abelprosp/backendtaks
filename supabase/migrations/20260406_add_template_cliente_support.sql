CREATE TABLE IF NOT EXISTS public."template_cliente" (
  "id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  "template_id" uuid NOT NULL REFERENCES public."Template"("id") ON DELETE CASCADE,
  "cliente_id" uuid NOT NULL REFERENCES public."Cliente"("id") ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_template_cliente_template_id ON public.template_cliente (template_id);

CREATE OR REPLACE FUNCTION public.rpc_templates_list()
RETURNS TABLE (
  id uuid,
  name text,
  descricao text,
  assunto_template text,
  prioridade_default boolean,
  observacoes_gerais_template text,
  is_recorrente_default boolean,
  recorrencia_tipo text,
  recorrencia_data_base_default date,
  recorrencia_prazo_reabertura_dias integer,
  criador_id uuid,
  created_at timestamp,
  updated_at timestamp,
  criador jsonb,
  setores jsonb,
  clientes jsonb,
  responsaveis jsonb,
  subtarefas jsonb
)
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = public
AS $$
  SELECT
    t.id,
    t.name,
    t.descricao,
    t.assunto_template,
    t.prioridade_default,
    t.observacoes_gerais_template,
    t.is_recorrente_default,
    t.recorrencia_tipo::text,
    t.recorrencia_data_base_default,
    t.recorrencia_prazo_reabertura_dias,
    t.criador_id,
    t.created_at,
    t.updated_at,
    (
      SELECT jsonb_build_object(
        'id', u.id,
        'name', u.name,
        'email', u.email
      )
      FROM public."User" u
      WHERE u.id = t.criador_id
    ) AS criador,
    COALESCE((
      SELECT jsonb_agg(
        jsonb_build_object(
          'setor', jsonb_build_object(
            'id', s.id,
            'name', s.name,
            'slug', s.slug
          )
        )
        ORDER BY s.name ASC
      )
      FROM public.template_setor ts
      JOIN public."Setor" s ON s.id = ts.setor_id
      WHERE ts.template_id = t.id
    ), '[]'::jsonb) AS setores,
    COALESCE((
      SELECT jsonb_agg(
        jsonb_build_object(
          'cliente', jsonb_build_object(
            'id', c.id,
            'name', c.name,
            'active', c.active,
            'tipoPessoa', c.tipo_pessoa,
            'documento', c.documento
          )
        )
        ORDER BY c.name ASC
      )
      FROM public.template_cliente tc
      JOIN public."Cliente" c ON c.id = tc.cliente_id
      WHERE tc.template_id = t.id
    ), '[]'::jsonb) AS clientes,
    COALESCE((
      SELECT jsonb_agg(
        jsonb_build_object(
          'userId', tr.user_id,
          'isPrincipal', tr.is_principal,
          'user', jsonb_build_object(
            'id', u.id,
            'name', u.name,
            'email', u.email
          )
        )
        ORDER BY tr.is_principal DESC, u.name ASC
      )
      FROM public.template_responsavel tr
      JOIN public."User" u ON u.id = tr.user_id
      WHERE tr.template_id = t.id
    ), '[]'::jsonb) AS responsaveis,
    COALESCE((
      SELECT jsonb_agg(
        jsonb_build_object(
          'id', ts.id,
          'titulo', ts.titulo,
          'ordem', ts.ordem,
          'responsavelUserId', ts.responsavel_user_id,
          'responsavel',
            CASE
              WHEN u.id IS NULL THEN NULL
              ELSE jsonb_build_object(
                'id', u.id,
                'name', u.name,
                'email', u.email
              )
            END
        )
        ORDER BY ts.ordem ASC, ts.id ASC
      )
      FROM public.template_subtarefa ts
      LEFT JOIN public."User" u ON u.id = ts.responsavel_user_id
      WHERE ts.template_id = t.id
    ), '[]'::jsonb) AS subtarefas
  FROM public."Template" t
  ORDER BY t.updated_at DESC;
$$;

CREATE OR REPLACE FUNCTION public.rpc_template_detail(p_template_id uuid)
RETURNS TABLE (
  id uuid,
  name text,
  descricao text,
  assunto_template text,
  prioridade_default boolean,
  observacoes_gerais_template text,
  is_recorrente_default boolean,
  recorrencia_tipo text,
  recorrencia_data_base_default date,
  recorrencia_prazo_reabertura_dias integer,
  criador_id uuid,
  created_at timestamp,
  updated_at timestamp,
  criador jsonb,
  setores jsonb,
  clientes jsonb,
  responsaveis jsonb,
  subtarefas jsonb
)
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = public
AS $$
  SELECT *
  FROM public.rpc_templates_list()
  WHERE id = p_template_id
  LIMIT 1;
$$;
