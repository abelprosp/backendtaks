using System.Text.Json;
using System.Globalization;
using LuxusDemandas.Api.Models;
using LuxusDemandas.Api.Support;

namespace LuxusDemandas.Api.Services;

public sealed class TemplatesService
{
    private readonly SupabaseRestService _supabase;
    private readonly AuditTrailService _audit;

    public TemplatesService(SupabaseRestService supabase, AuditTrailService audit)
    {
        _supabase = supabase;
        _audit = audit;
    }

    public async Task<object> CreateAsync(string userId, CreateTemplateRequest request, CancellationToken cancellationToken)
    {
        ValidateTemplateRecorrencia(
            request.IsRecorrenteDefault ?? false,
            request.RecorrenciaTipo,
            request.RecorrenciaDataBaseDefault);

        var created = await _supabase.InsertSingleAsync("Template", new
        {
            name = request.Name.Trim(),
            descricao = request.Descricao,
            assunto_template = request.AssuntoTemplate,
            prioridade_default = request.PrioridadeDefault ?? false,
            observacoes_gerais_template = request.ObservacoesGeraisTemplate,
            is_recorrente_default = request.IsRecorrenteDefault ?? false,
            recorrencia_tipo = request.RecorrenciaTipo,
            recorrencia_data_base_default = request.IsRecorrenteDefault == true ? request.RecorrenciaDataBaseDefault : null,
            recorrencia_prazo_reabertura_dias = request.RecorrenciaPrazoReaberturaDias,
            criador_id = userId,
        }, cancellationToken);

        var templateId = created.GetStringOrEmpty("id");
        await ReplaceTemplateRelationsAsync(
            templateId,
            request.SetorIds,
            request.ClienteIds,
            request.Responsaveis,
            request.Subtarefas,
            cancellationToken);

        await RegistrarCriacaoTemplateAsync(userId, templateId, request, cancellationToken);

        return await FindOneAsync(templateId, cancellationToken);
    }

    public async Task<IReadOnlyList<object>> FindAllAsync(CancellationToken cancellationToken)
    {
        var rpcRows = await FindAllViaRpcAsync(cancellationToken);
        if (rpcRows is not null)
        {
            return rpcRows;
        }

        var rows = await _supabase.QueryRowsAsync("Template?select=*&order=updated_at.desc", cancellationToken);
        var result = new List<object>(rows.Length);
        foreach (var row in rows)
        {
            result.Add(await MapTemplateFromDirectRowAsync(row, cancellationToken, includeHistory: false));
        }

        return result;
    }

    public async Task<object> FindOneAsync(string id, CancellationToken cancellationToken)
    {
        var rpcRow = await FindOneViaRpcAsync(id, cancellationToken);
        if (rpcRow is not null)
        {
            var historico = await _audit.LoadTemplateEventsAsync(id, cancellationToken);
            return MapTemplateFromRpcRow(rpcRow.Value, historico);
        }

        var row = await GetTemplateRowAsync(id, cancellationToken);
        if (row is null)
        {
            throw new KeyNotFoundException("Template não encontrado");
        }

        return await MapTemplateFromDirectRowAsync(row.Value, cancellationToken, includeHistory: true);
    }

    public async Task<object> UpdateAsync(string userId, string id, UpdateTemplateRequest request, CancellationToken cancellationToken)
    {
        _ = userId;
        var current = await GetTemplateRowAsync(id, cancellationToken);
        if (current is null)
        {
            throw new KeyNotFoundException("Template não encontrado");
        }

        ValidateTemplateRecorrencia(
            request.IsRecorrenteDefault ?? current.Value.GetBooleanOrDefault("is_recorrente_default"),
            request.RecorrenciaTipo ?? current.Value.GetNullableString("recorrencia_tipo"),
            request.RecorrenciaDataBaseDefault ?? current.Value.GetNullableString("recorrencia_data_base_default"));

        var updates = new Dictionary<string, object?>();
        if (request.Name is not null) updates["name"] = request.Name.Trim();
        if (request.Descricao is not null) updates["descricao"] = request.Descricao;
        if (request.AssuntoTemplate is not null) updates["assunto_template"] = request.AssuntoTemplate;
        if (request.PrioridadeDefault.HasValue) updates["prioridade_default"] = request.PrioridadeDefault.Value;
        if (request.ObservacoesGeraisTemplate is not null) updates["observacoes_gerais_template"] = request.ObservacoesGeraisTemplate;
        if (request.IsRecorrenteDefault.HasValue) updates["is_recorrente_default"] = request.IsRecorrenteDefault.Value;

        if (request.IsRecorrenteDefault == false)
        {
            updates["recorrencia_tipo"] = null;
            updates["recorrencia_data_base_default"] = null;
            updates["recorrencia_prazo_reabertura_dias"] = null;
        }
        else
        {
            if (request.RecorrenciaTipo is not null) updates["recorrencia_tipo"] = request.RecorrenciaTipo;
            if (request.RecorrenciaDataBaseDefault is not null) updates["recorrencia_data_base_default"] = request.RecorrenciaDataBaseDefault;
            if (request.RecorrenciaPrazoReaberturaDias.HasValue) updates["recorrencia_prazo_reabertura_dias"] = request.RecorrenciaPrazoReaberturaDias.Value;
        }

        if (updates.Count > 0)
        {
            await _supabase.UpdateSingleAsync(
                "Template",
                $"id=eq.{Uri.EscapeDataString(id)}",
                updates,
                cancellationToken);
        }

        if (request.SetorIds is not null || request.ClienteIds is not null || request.Responsaveis is not null || request.Subtarefas is not null)
        {
            await ReplaceTemplateRelationsAsync(id, request.SetorIds, request.ClienteIds, request.Responsaveis, request.Subtarefas, cancellationToken);
        }

        await RegistrarAlteracoesTemplateAsync(userId, id, request, cancellationToken);

        return await FindOneAsync(id, cancellationToken);
    }

    public async Task<object> RemoveAsync(string id, CancellationToken cancellationToken)
    {
        var current = await GetTemplateRowAsync(id, cancellationToken);
        if (current is null)
        {
            throw new KeyNotFoundException("Template não encontrado");
        }

        await _supabase.DeleteAsync("Template", $"id=eq.{Uri.EscapeDataString(id)}", cancellationToken);
        return new { id };
    }

    public async Task<TemplateDemandaSource> LoadForDemandaAsync(string id, CancellationToken cancellationToken)
    {
        var row = await GetTemplateRowAsync(id, cancellationToken);
        if (row is null)
        {
            throw new KeyNotFoundException("Template não encontrado");
        }

        var setoresTask = LoadTemplateSetorIdsAsync(id, cancellationToken);
        var clientesTask = LoadTemplateClienteIdsAsync(id, cancellationToken);
        var responsaveisTask = LoadTemplateResponsavelInputsAsync(id, cancellationToken);
        var subtarefasTask = LoadTemplateSubtarefasForDemandaAsync(id, cancellationToken);
        await Task.WhenAll(setoresTask, clientesTask, responsaveisTask, subtarefasTask);

        return new TemplateDemandaSource(
            row.Value.GetStringOrEmpty("id"),
            row.Value.GetStringOrEmpty("name"),
            row.Value.GetBooleanOrDefault("prioridade_default"),
            row.Value.GetNullableString("observacoes_gerais_template"),
            row.Value.GetBooleanOrDefault("is_recorrente_default"),
            row.Value.GetNullableString("recorrencia_tipo"),
            row.Value.GetNullableString("recorrencia_data_base_default"),
            row.Value.GetNullableInt32("recorrencia_prazo_reabertura_dias"),
            setoresTask.Result,
            clientesTask.Result,
            responsaveisTask.Result,
            subtarefasTask.Result);
    }

    private async Task<JsonElement?> GetTemplateRowAsync(string id, CancellationToken cancellationToken) =>
        await _supabase.QuerySingleAsync(
            $"Template?select=*&id=eq.{Uri.EscapeDataString(id)}&limit=1",
            cancellationToken);

    private async Task<IReadOnlyList<object>?> FindAllViaRpcAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _supabase.RpcAsync<JsonElement[]>("rpc_templates_list", new { }, cancellationToken);
            return rows.Select(row => MapTemplateFromRpcRow(row)).ToList();
        }
        catch
        {
            return null;
        }
    }

    private async Task<JsonElement?> FindOneViaRpcAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _supabase.RpcAsync<JsonElement[]>("rpc_template_detail", new
            {
                p_template_id = id,
            }, cancellationToken);
            var row = rows.FirstOrDefault();
            return row.ValueKind == JsonValueKind.Undefined ? null : row;
        }
        catch
        {
            return null;
        }
    }

    private async Task<object> MapTemplateFromDirectRowAsync(JsonElement row, CancellationToken cancellationToken, bool includeHistory)
    {
        var templateId = row.GetStringOrEmpty("id");
        var criadorTask = LoadCriadorAsync(row.GetNullableString("criador_id"), cancellationToken);
        var setoresTask = LoadSetoresAsync(templateId, cancellationToken);
        var clientesTask = LoadClientesAsync(templateId, cancellationToken);
        var responsaveisTask = LoadResponsaveisAsync(templateId, cancellationToken);
        var subtarefasTask = LoadSubtarefasAsync(templateId, cancellationToken);
        Task<IReadOnlyList<object>> historicoTask = includeHistory
            ? _audit.LoadTemplateEventsAsync(templateId, cancellationToken)
            : Task.FromResult<IReadOnlyList<object>>(Array.Empty<object>());
        await Task.WhenAll(criadorTask, setoresTask, clientesTask, responsaveisTask, subtarefasTask, historicoTask);

        return MapTemplate(
            row,
            criadorTask.Result,
            setoresTask.Result,
            clientesTask.Result,
            responsaveisTask.Result,
            subtarefasTask.Result,
            historicoTask.Result);
    }

    private object MapTemplateFromRpcRow(JsonElement row, IReadOnlyList<object>? historico = null)
    {
        var criador = row.GetOptionalProperty("criador");
        var setores = row.GetArrayOrEmpty("setores")
            .Select(setor => new
            {
                setor = new
                {
                    id = setor.GetStringOrEmpty("id"),
                    name = setor.GetStringOrEmpty("name"),
                    slug = setor.GetStringOrEmpty("slug"),
                },
            })
            .ToList();

        var clientes = row.GetArrayOrEmpty("clientes")
            .Select(link =>
            {
                var cliente = link.GetOptionalProperty("cliente");
                return (object)new
                {
                    cliente = cliente is JsonElement clienteValue && clienteValue.ValueKind == JsonValueKind.Object
                        ? new
                        {
                            id = clienteValue.GetStringOrEmpty("id"),
                            name = clienteValue.GetStringOrEmpty("name"),
                            active = clienteValue.GetBooleanOrDefault("active", true),
                            tipoPessoa = clienteValue.GetNullableString("tipoPessoa"),
                            documento = clienteValue.GetNullableString("documento"),
                        }
                        : null,
                };
            })
            .ToList();

        var responsaveis = row.GetArrayOrEmpty("responsaveis")
            .Select(responsavel => new
            {
                userId = responsavel.GetStringOrEmpty("userId"),
                isPrincipal = responsavel.GetBooleanOrDefault("isPrincipal"),
                user = responsavel.GetOptionalProperty("user") is JsonElement user && user.ValueKind == JsonValueKind.Object
                    ? new
                    {
                        id = user.GetStringOrEmpty("id"),
                        name = user.GetStringOrEmpty("name"),
                        email = user.GetStringOrEmpty("email"),
                    }
                    : null,
            })
            .ToList();

        var subtarefas = row.GetArrayOrEmpty("subtarefas")
            .Select(subtarefa =>
            {
                var ordem = subtarefa.GetNullableInt32("ordem") ?? 0;
                return new
                {
                    ordem,
                    payload = (object)new
                    {
                        id = subtarefa.GetNullableString("id"),
                        titulo = subtarefa.GetStringOrEmpty("titulo"),
                        ordem,
                        responsavelUserId = subtarefa.GetNullableString("responsavelUserId"),
                        responsavel = subtarefa.GetOptionalProperty("responsavel") is JsonElement user && user.ValueKind == JsonValueKind.Object
                            ? new
                            {
                                id = user.GetStringOrEmpty("id"),
                                name = user.GetStringOrEmpty("name"),
                                email = user.GetStringOrEmpty("email"),
                            }
                            : null,
                    },
                };
            })
            .OrderBy(subtarefa => subtarefa.ordem)
            .Select(subtarefa => subtarefa.payload)
            .ToList();

        return MapTemplate(row, criador, setores, clientes, responsaveis, subtarefas, historico);
    }

    private object MapTemplate(
        JsonElement row,
        JsonElement? criador,
        IReadOnlyList<object> setores,
        IReadOnlyList<object> clientes,
        IReadOnlyList<object> responsaveis,
        IReadOnlyList<object> subtarefas,
        IReadOnlyList<object>? historico = null)
    {
        object? criadorObject = null;
        if (criador is JsonElement criadorValue && criadorValue.ValueKind == JsonValueKind.Object)
        {
            criadorObject = new
            {
                id = criadorValue.GetStringOrEmpty("id"),
                name = criadorValue.GetStringOrEmpty("name"),
                email = criadorValue.GetStringOrEmpty("email"),
            };
        }

        return new
        {
            id = row.GetStringOrEmpty("id"),
            name = row.GetStringOrEmpty("name"),
            descricao = row.GetNullableString("descricao"),
            assuntoTemplate = row.GetNullableString("assunto_template"),
            prioridadeDefault = row.GetBooleanOrDefault("prioridade_default"),
            observacoesGeraisTemplate = row.GetNullableString("observacoes_gerais_template"),
            isRecorrenteDefault = row.GetBooleanOrDefault("is_recorrente_default"),
            recorrenciaTipo = row.GetNullableString("recorrencia_tipo"),
            recorrenciaDataBaseDefault = row.GetNullableString("recorrencia_data_base_default"),
            recorrenciaPrazoReaberturaDias = row.GetNullableInt32("recorrencia_prazo_reabertura_dias"),
            criadorId = row.GetNullableString("criador_id"),
            createdAt = row.GetNullableString("created_at"),
            updatedAt = row.GetNullableString("updated_at"),
            criador = criadorObject,
            setores,
            clientes,
            responsaveis,
            subtarefas,
            historico = historico ?? Array.Empty<object>(),
        };
    }

    private async Task RegistrarCriacaoTemplateAsync(
        string userId,
        string templateId,
        CreateTemplateRequest request,
        CancellationToken cancellationToken)
    {
        var descricao = "Template criado.";
        var detalhes = BuildTemplateResumoInicial(
            request.SetorIds?.Count ?? 0,
            request.ClienteIds?.Count ?? 0,
            request.Responsaveis?.Count ?? 0,
            request.Subtarefas?.Count ?? 0,
            request.IsRecorrenteDefault == true);

        if (detalhes.Count > 0)
        {
            descricao += $" Configuracao inicial: {string.Join(", ", detalhes)}.";
        }

        await _audit.AddTemplateEventAsync(templateId, userId, "template_criado", descricao, new
        {
            setores = request.SetorIds?.Count ?? 0,
            clientes = request.ClienteIds?.Count ?? 0,
            responsaveis = request.Responsaveis?.Count ?? 0,
            subtarefas = request.Subtarefas?.Count ?? 0,
            recorrente = request.IsRecorrenteDefault ?? false,
        }, cancellationToken);
    }

    private async Task RegistrarAlteracoesTemplateAsync(
        string userId,
        string templateId,
        UpdateTemplateRequest request,
        CancellationToken cancellationToken)
    {
        var camposBasicos = new List<string>();
        if (request.Name is not null) camposBasicos.Add("nome");
        if (request.Descricao is not null) camposBasicos.Add("descricao");
        if (request.AssuntoTemplate is not null) camposBasicos.Add("assunto padrao");
        if (request.PrioridadeDefault.HasValue) camposBasicos.Add("prioridade padrao");
        if (request.ObservacoesGeraisTemplate is not null) camposBasicos.Add("observacoes padrao");
        if (request.IsRecorrenteDefault.HasValue) camposBasicos.Add("recorrencia");
        if (request.RecorrenciaTipo is not null) camposBasicos.Add("tipo de recorrencia");
        if (request.RecorrenciaDataBaseDefault is not null) camposBasicos.Add("data base");
        if (request.RecorrenciaPrazoReaberturaDias.HasValue) camposBasicos.Add("prazo de reabertura");

        if (camposBasicos.Count > 0)
        {
            await _audit.AddTemplateEventAsync(
                templateId,
                userId,
                "template_atualizado",
                $"Campos basicos atualizados: {JoinLabels(camposBasicos)}.",
                new { campos = camposBasicos },
                cancellationToken);
        }

        if (request.SetorIds is not null)
        {
            await _audit.AddTemplateEventAsync(
                templateId,
                userId,
                "template_setores_atualizados",
                $"Setores atualizados ({CountLabel(request.SetorIds.Count, "setor", "setores")}).",
                new { total = request.SetorIds.Count },
                cancellationToken);
        }

        if (request.ClienteIds is not null)
        {
            await _audit.AddTemplateEventAsync(
                templateId,
                userId,
                "template_clientes_atualizados",
                $"Clientes atualizados ({CountLabel(request.ClienteIds.Count, "cliente", "clientes")}).",
                new { total = request.ClienteIds.Count },
                cancellationToken);
        }

        if (request.Responsaveis is not null)
        {
            var principals = request.Responsaveis.Count(item => item.IsPrincipal == true);
            await _audit.AddTemplateEventAsync(
                templateId,
                userId,
                "template_responsaveis_atualizados",
                $"Responsaveis atualizados ({CountLabel(request.Responsaveis.Count, "responsavel", "responsaveis")}, {CountLabel(principals, "principal", "principais")}).",
                new
                {
                    total = request.Responsaveis.Count,
                    principais = principals,
                },
                cancellationToken);
        }

        if (request.Subtarefas is not null)
        {
            await _audit.AddTemplateEventAsync(
                templateId,
                userId,
                "template_subtarefas_atualizadas",
                $"Subtarefas atualizadas ({CountLabel(request.Subtarefas.Count, "item", "itens")}).",
                new { total = request.Subtarefas.Count },
                cancellationToken);
        }

        if (request.IsRecorrenteDefault == false)
        {
            await _audit.AddTemplateEventAsync(
                templateId,
                userId,
                "template_recorrencia_removida",
                "Recorrencia padrao removida.",
                null,
                cancellationToken);
        }
        else if (request.IsRecorrenteDefault == true || request.RecorrenciaTipo is not null || request.RecorrenciaDataBaseDefault is not null || request.RecorrenciaPrazoReaberturaDias.HasValue)
        {
            var recorrenciaDetalhes = new List<string>();
            if (!string.IsNullOrWhiteSpace(request.RecorrenciaTipo))
            {
                recorrenciaDetalhes.Add($"tipo {request.RecorrenciaTipo}");
            }
            if (!string.IsNullOrWhiteSpace(request.RecorrenciaDataBaseDefault))
            {
                recorrenciaDetalhes.Add($"data base {FormatDateForDescription(request.RecorrenciaDataBaseDefault)}");
            }
            if (request.RecorrenciaPrazoReaberturaDias.HasValue)
            {
                recorrenciaDetalhes.Add($"reabertura em {request.RecorrenciaPrazoReaberturaDias.Value} dia(s)");
            }

            await _audit.AddTemplateEventAsync(
                templateId,
                userId,
                "template_recorrencia_configurada",
                recorrenciaDetalhes.Count > 0
                    ? $"Recorrencia padrao configurada: {string.Join(", ", recorrenciaDetalhes)}."
                    : "Recorrencia padrao atualizada.",
                new
                {
                    tipo = request.RecorrenciaTipo,
                    dataBase = request.RecorrenciaDataBaseDefault,
                    prazoReaberturaDias = request.RecorrenciaPrazoReaberturaDias,
                },
                cancellationToken);
        }
    }

    private static List<string> BuildTemplateResumoInicial(int setores, int clientes, int responsaveis, int subtarefas, bool recorrente)
    {
        var detalhes = new List<string>();
        if (setores > 0) detalhes.Add(CountLabel(setores, "setor", "setores"));
        if (clientes > 0) detalhes.Add(CountLabel(clientes, "cliente", "clientes"));
        if (responsaveis > 0) detalhes.Add(CountLabel(responsaveis, "responsavel", "responsaveis"));
        if (subtarefas > 0) detalhes.Add(CountLabel(subtarefas, "subtarefa", "subtarefas"));
        if (recorrente) detalhes.Add("recorrencia padrao");
        return detalhes;
    }

    private static string JoinLabels(IReadOnlyList<string> labels)
    {
        if (labels.Count == 0) return string.Empty;
        if (labels.Count == 1) return labels[0];
        if (labels.Count == 2) return $"{labels[0]} e {labels[1]}";
        return $"{string.Join(", ", labels.Take(labels.Count - 1))} e {labels[^1]}";
    }

    private static string CountLabel(int total, string singular, string plural) =>
        total == 1 ? $"1 {singular}" : $"{total} {plural}";

    private static string FormatDateForDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "sem data";
        }

        return DateTime.TryParse(value, out var parsed)
            ? parsed.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR"))
            : value;
    }

    private async Task<JsonElement?> LoadCriadorAsync(string? criadorId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(criadorId))
        {
            return null;
        }

        return await _supabase.QuerySingleAsync(
            $"User?select=id,name,email&id=eq.{Uri.EscapeDataString(criadorId)}&limit=1",
            cancellationToken);
    }

    private async Task<IReadOnlyList<object>> LoadSetoresAsync(string templateId, CancellationToken cancellationToken)
    {
        var links = await _supabase.QueryRowsAsync(
            $"template_setor?select=setor_id&template_id=eq.{Uri.EscapeDataString(templateId)}",
            cancellationToken);
        var setorIds = links
            .Select(link => link.GetStringOrEmpty("setor_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToArray();
        if (setorIds.Length == 0)
        {
            return Array.Empty<object>();
        }

        var setores = await _supabase.QueryRowsAsync(
            $"Setor?select=id,name,slug&id=in.({string.Join(",", setorIds.Select(Uri.EscapeDataString))})",
            cancellationToken);
        return setores
            .Select(setor => (object)new
            {
                setor = new
                {
                    id = setor.GetStringOrEmpty("id"),
                    name = setor.GetStringOrEmpty("name"),
                    slug = setor.GetStringOrEmpty("slug"),
                },
            })
            .ToList();
    }

    private async Task<IReadOnlyList<string>> LoadTemplateSetorIdsAsync(string templateId, CancellationToken cancellationToken)
    {
        var links = await _supabase.QueryRowsAsync(
            $"template_setor?select=setor_id&template_id=eq.{Uri.EscapeDataString(templateId)}",
            cancellationToken);
        return links
            .Select(link => link.GetStringOrEmpty("setor_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToArray();
    }

    private async Task<IReadOnlyList<string>> LoadTemplateClienteIdsAsync(string templateId, CancellationToken cancellationToken)
    {
        var links = await _supabase.QueryRowsAsync(
            $"template_cliente?select=cliente_id&template_id=eq.{Uri.EscapeDataString(templateId)}",
            cancellationToken);
        return links
            .Select(link => link.GetStringOrEmpty("cliente_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToArray();
    }

    private async Task<IReadOnlyList<object>> LoadClientesAsync(string templateId, CancellationToken cancellationToken)
    {
        var links = await _supabase.QueryRowsAsync(
            $"template_cliente?select=cliente_id&template_id=eq.{Uri.EscapeDataString(templateId)}",
            cancellationToken);
        var clienteIds = links
            .Select(link => link.GetStringOrEmpty("cliente_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToArray();

        if (clienteIds.Length == 0)
        {
            return Array.Empty<object>();
        }

        var clientes = await _supabase.QueryRowsAsync(
            $"Cliente?select=id,name,active,tipo_pessoa,documento&id=in.({string.Join(",", clienteIds.Select(Uri.EscapeDataString))})",
            cancellationToken);
        return clientes
            .Select(cliente => (object)new
            {
                cliente = new
                {
                    id = cliente.GetStringOrEmpty("id"),
                    name = cliente.GetStringOrEmpty("name"),
                    active = cliente.GetBooleanOrDefault("active", true),
                    tipoPessoa = cliente.GetNullableString("tipo_pessoa"),
                    documento = cliente.GetNullableString("documento"),
                },
            })
            .ToList();
    }

    private async Task<IReadOnlyList<object>> LoadResponsaveisAsync(string templateId, CancellationToken cancellationToken)
    {
        var links = await _supabase.QueryRowsAsync(
            $"template_responsavel?select=user_id,is_principal&template_id=eq.{Uri.EscapeDataString(templateId)}",
            cancellationToken);
        var userIds = links
            .Select(link => link.GetStringOrEmpty("user_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToArray();

        Dictionary<string, JsonElement> usersById = [];
        if (userIds.Length > 0)
        {
            var users = await _supabase.QueryRowsAsync(
                $"User?select=id,name,email&id=in.({string.Join(",", userIds.Select(id => Uri.EscapeDataString(id!)))})",
                cancellationToken);
            usersById = users.ToDictionary(user => user.GetStringOrEmpty("id"));
        }

        return links.Select(link =>
        {
            var userId = link.GetStringOrEmpty("user_id");
            usersById.TryGetValue(userId, out var user);
            return (object)new
            {
                userId,
                isPrincipal = link.GetBooleanOrDefault("is_principal"),
                user = user.ValueKind == JsonValueKind.Object
                    ? new
                    {
                        id = user.GetStringOrEmpty("id"),
                        name = user.GetStringOrEmpty("name"),
                        email = user.GetStringOrEmpty("email"),
                    }
                    : null,
            };
        }).ToList();
    }

    private async Task<IReadOnlyList<TemplateDemandaResponsavel>> LoadTemplateResponsavelInputsAsync(string templateId, CancellationToken cancellationToken)
    {
        var links = await _supabase.QueryRowsAsync(
            $"template_responsavel?select=user_id,is_principal&template_id=eq.{Uri.EscapeDataString(templateId)}",
            cancellationToken);
        return links
            .Select(link => new TemplateDemandaResponsavel(
                link.GetStringOrEmpty("user_id"),
                link.GetBooleanOrDefault("is_principal")))
            .ToList();
    }

    private async Task<IReadOnlyList<object>> LoadSubtarefasAsync(string templateId, CancellationToken cancellationToken)
    {
        var rows = await _supabase.QueryRowsAsync(
            $"template_subtarefa?select=id,template_id,titulo,ordem,responsavel_user_id&template_id=eq.{Uri.EscapeDataString(templateId)}&order=ordem.asc",
            cancellationToken);
        var userIds = rows
            .Select(row => row.GetNullableString("responsavel_user_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToArray();

        Dictionary<string, JsonElement> usersById = [];
        if (userIds.Length > 0)
        {
            var escapedUserIds = userIds.Select(id => Uri.EscapeDataString(id ?? string.Empty));
            var users = await _supabase.QueryRowsAsync(
                $"User?select=id,name,email&id=in.({string.Join(",", escapedUserIds)})",
                cancellationToken);
            usersById = users.ToDictionary(user => user.GetStringOrEmpty("id"));
        }

        return rows.Select(row =>
        {
            var ordem = row.GetNullableInt32("ordem") ?? 0;
            var responsavelUserId = row.GetNullableString("responsavel_user_id");
            usersById.TryGetValue(responsavelUserId ?? string.Empty, out var user);
            return new
            {
                ordem,
                payload = (object)new
                {
                    id = row.GetNullableString("id"),
                    titulo = row.GetStringOrEmpty("titulo"),
                    ordem,
                    responsavelUserId,
                    responsavel = user.ValueKind == JsonValueKind.Object
                        ? new
                        {
                            id = user.GetStringOrEmpty("id"),
                            name = user.GetStringOrEmpty("name"),
                            email = user.GetStringOrEmpty("email"),
                        }
                        : null,
                },
            };
        }).OrderBy(subtarefa => subtarefa.ordem)
          .Select(subtarefa => subtarefa.payload)
          .ToList();
    }

    private async Task<IReadOnlyList<TemplateDemandaSubtarefa>> LoadTemplateSubtarefasForDemandaAsync(string templateId, CancellationToken cancellationToken)
    {
        var rows = await _supabase.QueryRowsAsync(
            $"template_subtarefa?select=titulo,ordem,responsavel_user_id&template_id=eq.{Uri.EscapeDataString(templateId)}&order=ordem.asc",
            cancellationToken);
        return rows
            .Select(row => new
            {
                ordem = row.GetNullableInt32("ordem") ?? 0,
                payload = new TemplateDemandaSubtarefa(
                    row.GetStringOrEmpty("titulo"),
                    row.GetNullableString("responsavel_user_id")),
            })
            .OrderBy(item => item.ordem)
            .Select(item => item.payload)
            .ToList();
    }

    private async Task ReplaceTemplateRelationsAsync(
        string templateId,
        IReadOnlyList<string>? setorIds,
        IReadOnlyList<string>? clienteIds,
        IReadOnlyList<TemplateResponsavelInput>? responsaveis,
        IReadOnlyList<TemplateSubtarefaInput>? subtarefas,
        CancellationToken cancellationToken)
    {
        if (setorIds is not null)
        {
            await _supabase.DeleteAsync("template_setor", $"template_id=eq.{Uri.EscapeDataString(templateId)}", cancellationToken);
            if (setorIds.Count > 0)
            {
                await _supabase.InsertManyAsync("template_setor",
                    setorIds.Select(setorId => new { template_id = templateId, setor_id = setorId }),
                    cancellationToken);
            }
        }

        if (clienteIds is not null)
        {
            await _supabase.DeleteAsync("template_cliente", $"template_id=eq.{Uri.EscapeDataString(templateId)}", cancellationToken);
            if (clienteIds.Count > 0)
            {
                await _supabase.InsertManyAsync("template_cliente",
                    clienteIds.Select(clienteId => new { template_id = templateId, cliente_id = clienteId }),
                    cancellationToken);
            }
        }

        if (responsaveis is not null)
        {
            await _supabase.DeleteAsync("template_responsavel", $"template_id=eq.{Uri.EscapeDataString(templateId)}", cancellationToken);
            if (responsaveis.Count > 0)
            {
                await _supabase.InsertManyAsync("template_responsavel",
                    responsaveis.Select(responsavel => new
                    {
                        template_id = templateId,
                        user_id = responsavel.UserId,
                        is_principal = responsavel.IsPrincipal ?? false,
                    }),
                    cancellationToken);
            }
        }

        if (subtarefas is not null)
        {
            await _supabase.DeleteAsync("template_subtarefa", $"template_id=eq.{Uri.EscapeDataString(templateId)}", cancellationToken);
            if (subtarefas.Count > 0)
            {
                await _supabase.InsertManyAsync("template_subtarefa",
                    subtarefas.Select((subtarefa, index) => new
                    {
                        template_id = templateId,
                        titulo = subtarefa.Titulo,
                        ordem = subtarefa.Ordem ?? index,
                        responsavel_user_id = subtarefa.ResponsavelUserId,
                    }),
                    cancellationToken);
            }
        }
    }

    private static void ValidateTemplateRecorrencia(bool isRecorrenteDefault, string? recorrenciaTipo, string? recorrenciaDataBaseDefault)
    {
        if (!isRecorrenteDefault)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(recorrenciaTipo))
        {
            throw new InvalidOperationException("Informe o tipo da recorrência padrão do template.");
        }

        if (string.IsNullOrWhiteSpace(recorrenciaDataBaseDefault))
        {
            throw new InvalidOperationException("Informe a data base da recorrência padrão do template.");
        }
    }
}
