using System.Text.Json;
using LuxusDemandas.Api.Models;
using LuxusDemandas.Api.Support;

namespace LuxusDemandas.Api.Services;

public sealed class TemplatesService
{
    private readonly SupabaseRestService _supabase;

    public TemplatesService(SupabaseRestService supabase)
    {
        _supabase = supabase;
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
            request.Responsaveis,
            request.Subtarefas,
            cancellationToken);

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
            result.Add(await MapTemplateFromDirectRowAsync(row, cancellationToken));
        }

        return result;
    }

    public async Task<object> FindOneAsync(string id, CancellationToken cancellationToken)
    {
        var rpcRow = await FindOneViaRpcAsync(id, cancellationToken);
        if (rpcRow is not null)
        {
            return rpcRow;
        }

        var row = await GetTemplateRowAsync(id, cancellationToken);
        if (row is null)
        {
            throw new KeyNotFoundException("Template não encontrado");
        }

        return await MapTemplateFromDirectRowAsync(row.Value, cancellationToken);
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

        if (request.SetorIds is not null || request.Responsaveis is not null || request.Subtarefas is not null)
        {
            await ReplaceTemplateRelationsAsync(id, request.SetorIds, request.Responsaveis, request.Subtarefas, cancellationToken);
        }

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
        var responsaveisTask = LoadTemplateResponsavelInputsAsync(id, cancellationToken);
        var subtarefasTask = LoadTemplateSubtarefasForDemandaAsync(id, cancellationToken);
        await Task.WhenAll(setoresTask, responsaveisTask, subtarefasTask);

        return new TemplateDemandaSource(
            row.Value.GetStringOrEmpty("id"),
            row.Value.GetBooleanOrDefault("prioridade_default"),
            row.Value.GetNullableString("observacoes_gerais_template"),
            row.Value.GetBooleanOrDefault("is_recorrente_default"),
            row.Value.GetNullableString("recorrencia_tipo"),
            row.Value.GetNullableString("recorrencia_data_base_default"),
            row.Value.GetNullableInt32("recorrencia_prazo_reabertura_dias"),
            setoresTask.Result,
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
            return rows.Select(MapTemplateFromRpcRow).ToList();
        }
        catch
        {
            return null;
        }
    }

    private async Task<object?> FindOneViaRpcAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _supabase.RpcAsync<JsonElement[]>("rpc_template_detail", new
            {
                p_template_id = id,
            }, cancellationToken);
            var row = rows.FirstOrDefault();
            return row.ValueKind == JsonValueKind.Undefined ? null : MapTemplateFromRpcRow(row);
        }
        catch
        {
            return null;
        }
    }

    private async Task<object> MapTemplateFromDirectRowAsync(JsonElement row, CancellationToken cancellationToken)
    {
        var templateId = row.GetStringOrEmpty("id");
        var criadorTask = LoadCriadorAsync(row.GetNullableString("criador_id"), cancellationToken);
        var setoresTask = LoadSetoresAsync(templateId, cancellationToken);
        var responsaveisTask = LoadResponsaveisAsync(templateId, cancellationToken);
        var subtarefasTask = LoadSubtarefasAsync(templateId, cancellationToken);
        await Task.WhenAll(criadorTask, setoresTask, responsaveisTask, subtarefasTask);

        return MapTemplate(
            row,
            criadorTask.Result,
            setoresTask.Result,
            responsaveisTask.Result,
            subtarefasTask.Result);
    }

    private object MapTemplateFromRpcRow(JsonElement row)
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

        return MapTemplate(row, criador, setores, responsaveis, subtarefas);
    }

    private object MapTemplate(
        JsonElement row,
        JsonElement? criador,
        IReadOnlyList<object> setores,
        IReadOnlyList<object> responsaveis,
        IReadOnlyList<object> subtarefas)
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
            responsaveis,
            subtarefas,
        };
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
