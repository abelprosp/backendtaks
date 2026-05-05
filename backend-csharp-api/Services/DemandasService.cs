using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LuxusDemandas.Api.Configuration;
using LuxusDemandas.Api.Models;
using LuxusDemandas.Api.Support;
using Microsoft.Extensions.Options;

namespace LuxusDemandas.Api.Services;

public sealed class DemandasService
{
    private readonly SupabaseRestService _supabase;
    private readonly DemandaVisibilityService _visibility;
    private readonly TemplatesService _templates;
    private readonly AuditTrailService _audit;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LegacyAttachmentService _legacyAttachments;
    private readonly AppOptions _options;
    private bool _anexosBucketReady;

    public DemandasService(
        SupabaseRestService supabase,
        DemandaVisibilityService visibility,
        TemplatesService templates,
        AuditTrailService audit,
        IHttpClientFactory httpClientFactory,
        LegacyAttachmentService legacyAttachments,
        IOptions<AppOptions> options)
    {
        _supabase = supabase;
        _visibility = visibility;
        _templates = templates;
        _audit = audit;
        _httpClientFactory = httpClientFactory;
        _legacyAttachments = legacyAttachments;
        _options = options.Value;
    }

    public async Task<object> CreateAsync(string userId, CreateDemandaRequest request, CancellationToken cancellationToken)
    {
        var protocolo = await GerarProtocoloAsync(cancellationToken);
        var status = string.IsNullOrWhiteSpace(request.Status) ? "em_aberto" : request.Status;
        var isPrivada = request.IsPrivada == true;
        if (isPrivada && !await _visibility.CanManagePrivateDemandasAsync(userId, cancellationToken))
        {
            throw new InvalidOperationException("Apenas usuários ADM podem criar demandas privadas.");
        }
        var setorIds = NormalizeUuidList(request.Setores);
        var clienteIds = NormalizeUuidList(request.ClienteIds);
        var responsaveis = NormalizeResponsaveis(request.Responsaveis);
        var subtarefas = NormalizeCreateSubtarefas(request.Subtarefas);

        var created = await _supabase.InsertSingleAsync("Demanda", new
        {
            protocolo,
            assunto = request.Assunto,
            prioridade = request.Prioridade ?? false,
            prazo = request.Prazo,
            status,
            criador_id = userId,
            observacoes_gerais = request.ObservacoesGerais,
            is_recorrente = request.IsRecorrente ?? false,
            is_privada = isPrivada,
            private_owner_user_id = isPrivada ? userId : null,
        }, cancellationToken);

        var demandaId = created.GetStringOrEmpty("id");
        await ReplaceDemandaRelationsAsync(
            demandaId,
            setorIds,
            clienteIds,
            responsaveis,
            subtarefas?.Select(item => new DemandaSubtarefaUpdateInput
            {
                Titulo = item.Titulo,
                Ordem = item.Ordem,
                ResponsavelUserId = item.ResponsavelUserId,
                Concluida = false,
            }).ToList(),
            cancellationToken);
        await ReplacePrivateViewersAsync(demandaId, NormalizeUuidList(request.PrivateViewerIds), cancellationToken);

        if (request.IsRecorrente == true && request.Recorrencia is not null)
        {
            await UpsertRecorrenciaAsync(demandaId, request.Recorrencia, cancellationToken);
        }

        await RegistrarCriacaoDemandaAsync(
            userId,
            demandaId,
            setorIds?.Count ?? 0,
            clienteIds?.Count ?? 0,
            responsaveis?.Count ?? 0,
            subtarefas?.Count ?? 0,
            request.IsRecorrente == true && request.Recorrencia is not null,
            null,
            cancellationToken);

        return await FindOneAsync(userId, demandaId, cancellationToken);
    }

    public async Task<object> CreateFromTemplateAsync(string userId, string templateId, CreateDemandaFromTemplateRequest request, CancellationToken cancellationToken)
    {
        var template = await _templates.LoadForDemandaAsync(templateId, cancellationToken);
        var protocolo = await GerarProtocoloAsync(cancellationToken);
        var isPrivada = request.IsPrivada == true;
        if (isPrivada && !await _visibility.CanManagePrivateDemandasAsync(userId, cancellationToken))
        {
            throw new InvalidOperationException("Apenas usuários ADM podem criar demandas privadas.");
        }
        var prioridade = request.Prioridade ?? template.PrioridadeDefault;
        var observacoesGerais = request.ObservacoesGerais ?? template.ObservacoesGeraisTemplate;
        var recorrenciaDataBase = request.RecorrenciaDataBase ?? template.RecorrenciaDataBaseDefault;
        var isRecorrente = !string.IsNullOrWhiteSpace(recorrenciaDataBase)
                           && template.IsRecorrenteDefault
                           && !string.IsNullOrWhiteSpace(template.RecorrenciaTipo);
        var requestSetorIds = NormalizeUuidList(request.SetorIds);
        var requestClienteIds = NormalizeUuidList(request.ClienteIds);
        var setorIds = requestSetorIds?.Count > 0 ? requestSetorIds : NormalizeUuidList(template.SetorIds);
        var clienteIds = requestClienteIds?.Count > 0 ? requestClienteIds : NormalizeUuidList(template.ClienteIds);
        var requestResponsaveis = NormalizeResponsaveis(request.Responsaveis);
        var responsaveis = requestResponsaveis?.Count > 0
            ? requestResponsaveis
            : template.Responsaveis.Select(item => new DemandaResponsavelInput
            {
                UserId = item.UserId,
                IsPrincipal = item.IsPrincipal,
            }).Where(item => IsUuid(item.UserId)).ToList();
        var requestSubtarefas = NormalizeCreateSubtarefas(request.Subtarefas);
        var subtarefas = requestSubtarefas?.Count > 0
            ? requestSubtarefas
            : template.Subtarefas.Select(item => new DemandaSubtarefaCreateInput
            {
                Titulo = item.Titulo,
                ResponsavelUserId = IsUuid(item.ResponsavelUserId) ? item.ResponsavelUserId : null,
            }).ToList();

        var created = await _supabase.InsertSingleAsync("Demanda", new
        {
            protocolo,
            assunto = request.Assunto,
            prioridade,
            prazo = request.Prazo,
            status = "em_aberto",
            criador_id = userId,
            observacoes_gerais = observacoesGerais,
            is_recorrente = isRecorrente,
            is_privada = isPrivada,
            private_owner_user_id = isPrivada ? userId : null,
        }, cancellationToken);

        var demandaId = created.GetStringOrEmpty("id");
        await ReplaceDemandaRelationsAsync(
            demandaId,
            setorIds,
            clienteIds,
            responsaveis,
            subtarefas.Select((item, index) => new DemandaSubtarefaUpdateInput
            {
                Titulo = item.Titulo,
                Ordem = item.Ordem ?? index,
                ResponsavelUserId = item.ResponsavelUserId,
                Concluida = false,
            }).ToList(),
            cancellationToken);
        await ReplacePrivateViewersAsync(demandaId, NormalizeUuidList(request.PrivateViewerIds), cancellationToken);

        if (isRecorrente && !string.IsNullOrWhiteSpace(recorrenciaDataBase) && !string.IsNullOrWhiteSpace(template.RecorrenciaTipo))
        {
            await UpsertRecorrenciaAsync(demandaId, new RecorrenciaInput
            {
                DataBase = recorrenciaDataBase!,
                Tipo = template.RecorrenciaTipo!,
                PrazoReaberturaDias = template.RecorrenciaPrazoReaberturaDias,
            }, cancellationToken);
        }

        await RegistrarCriacaoDemandaAsync(
            userId,
            demandaId,
            setorIds?.Count ?? 0,
            clienteIds?.Count ?? 0,
            responsaveis.Count,
            subtarefas.Count,
            isRecorrente,
            template.Name,
            cancellationToken);

        return await FindOneAsync(userId, demandaId, cancellationToken);
    }

    public async Task<object> ListAsync(string userId, ListDemandasFiltersQuery filters, CancellationToken cancellationToken)
    {
        var pagination = GetPagination(filters);
        if (await _visibility.IsAdminAsync(userId, cancellationToken))
        {
            return await ListAllDemandasForAdminAsync(userId, filters, pagination, cancellationToken);
        }

        var visibleIds = await _visibility.VisibleDemandaIdsAsync(userId, cancellationToken);
        if (visibleIds.Count == 0)
        {
            return new { data = Array.Empty<object>(), total = 0 };
        }

        if (filters.OcultarStandby == true && !string.Equals(filters.Status, "standby", StringComparison.Ordinal))
        {
            var activeRows = await _supabase.QueryAllRowsAsync(
                "Demanda?select=id&status=neq.standby",
                cancellationToken);
            var activeIds = activeRows.Select(row => row.GetStringOrEmpty("id")).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
            visibleIds = visibleIds.Where(activeIds.Contains).ToList();
            if (visibleIds.Count == 0)
            {
                return new { data = Array.Empty<object>(), total = 0 };
            }
        }
        if (filters.OcultarConcluidas == true && string.IsNullOrWhiteSpace(filters.Status))
        {
            var unfinishedRows = await _supabase.QueryAllRowsAsync(
                "Demanda?select=id&status=not.in.(concluido,cancelado)",
                cancellationToken);
            var unfinishedIds = unfinishedRows.Select(row => row.GetStringOrEmpty("id")).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
            visibleIds = visibleIds.Where(unfinishedIds.Contains).ToList();
            if (visibleIds.Count == 0)
            {
                return new { data = Array.Empty<object>(), total = 0 };
            }
        }

        if (!string.IsNullOrWhiteSpace(filters.ResponsavelPrincipalId))
        {
            var principalFilter = filters.ResponsavelApenasPrincipal == true ? "&is_principal=eq.true" : string.Empty;
            var responsavelIds = await LoadIdsAsync(
                $"demanda_responsavel?select=demanda_id&user_id=eq.{Uri.EscapeDataString(filters.ResponsavelPrincipalId)}{principalFilter}&limit=100000",
                "demanda_id",
                cancellationToken);
            visibleIds = visibleIds.Where(responsavelIds.Contains).ToList();
            if (visibleIds.Count == 0)
            {
                return new { data = Array.Empty<object>(), total = 0 };
            }
        }

        visibleIds = await ApplyAnexosFilterAsync(visibleIds, filters.Anexos, cancellationToken);
        if (visibleIds.Count == 0)
        {
            return new { data = Array.Empty<object>(), total = 0 };
        }

        try
        {
            var rows = await _supabase.RpcAsync<JsonElement[]>("rpc_list_demandas_page", new
            {
                p_user_id = userId,
                p_limit = pagination.PageSize,
                p_offset = pagination.Offset,
                p_ids = visibleIds,
                p_cliente_id = filters.ClienteId,
                p_assunto = filters.Assunto,
                p_status = filters.Status,
                p_tipo_recorrencia = filters.TipoRecorrencia,
                p_protocolo = filters.Protocolo,
                p_prioridade = filters.Prioridade,
                p_criador_id = filters.CriadorId,
                p_responsavel_principal_id = null as string,
                p_setor_ids = filters.SetorIds?.Count > 0 ? filters.SetorIds : null,
                p_condicao_prazo = filters.CondicaoPrazo,
                p_pesquisa_tarefa_ou_observacao = filters.PesquisarTarefaOuObservacao,
                p_pesquisa_geral = filters.PesquisaGeral,
                p_data_criacao_de = filters.DataCriacaoDe,
                p_data_criacao_ate = filters.DataCriacaoAte,
                p_prazo_de = filters.PrazoDe,
                p_prazo_ate = filters.PrazoAte,
            }, cancellationToken);

            var total = rows.Length > 0 ? rows[0].GetNullableInt32("total_count") ?? 0 : 0;
            return new
            {
                data = rows.Select(MapDemandaListFromRpc).ToList(),
                total,
            };
        }
        catch
        {
            var idsClause = string.Join(",", visibleIds.Select(Uri.EscapeDataString));
            var rows = await _supabase.QueryRowsAsync(
                $"Demanda?select=*&id=in.({idsClause})&order=created_at.desc&limit={pagination.PageSize}&offset={pagination.Offset}",
                cancellationToken);

            var data = new List<object>(rows.Length);
            foreach (var row in rows)
            {
                data.Add(await BuildDetailFromDirectRowAsync(row, includeDetail: false, cancellationToken));
            }

            return new
            {
                data,
                total = visibleIds.Count,
            };
        }
    }

    public async Task<object> FindOneAsync(string userId, string id, CancellationToken cancellationToken)
    {
        var demanda = await _supabase.QuerySingleAsync(
            $"Demanda?select=*&id=eq.{Uri.EscapeDataString(id)}&limit=1",
            cancellationToken);
        if (demanda is null)
        {
            throw new KeyNotFoundException("Demanda nao encontrada");
        }
        var canView = await _visibility.CanViewDemandaAsync(userId, id, cancellationToken);
        if (!canView)
        {
            throw new UnauthorizedAccessException("Sem permissao para ver esta demanda");
        }
        await EnsureLegacyAnexosLinkedAsync(demanda.Value, id, cancellationToken);
        try
        {
            var rows = await _supabase.RpcAsync<JsonElement[]>("rpc_demanda_detail_for_user", new
            {
                p_user_id = userId,
                p_demanda_id = id,
            }, cancellationToken);
            var row = rows.FirstOrDefault();
            if (row.ValueKind != JsonValueKind.Undefined)
            {
                var historico = await _audit.LoadDemandaEventsAsync(id, cancellationToken);
                return MapDemandaDetailFromRpc(row, historico);
            }
        }
        catch
        {
        }
        return await BuildDetailFromDirectRowAsync(demanda.Value, includeDetail: true, cancellationToken);
    }

    private async Task<object> ListAllDemandasForAdminAsync(
        string userId,
        ListDemandasFiltersQuery filters,
        (int Page, int PageSize, int Offset) pagination,
        CancellationToken cancellationToken)
    {
        var canViewAllPrivateDemandas = await _visibility.CanManagePrivateDemandasAsync(userId, cancellationToken);
        var rows = await _supabase.QueryAllRowsAsync(
            "Demanda?select=*&order=created_at.desc",
            cancellationToken);

        HashSet<string>? constrainedIds = null;

        if (!string.IsNullOrWhiteSpace(filters.ClienteId))
        {
            constrainedIds = IntersectIds(
                constrainedIds,
                await LoadIdsAsync(
                    $"demanda_cliente?select=demanda_id&cliente_id=eq.{Uri.EscapeDataString(filters.ClienteId)}&limit=100000",
                    "demanda_id",
                    cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(filters.ResponsavelPrincipalId))
        {
            var principalFilter = filters.ResponsavelApenasPrincipal == true ? "&is_principal=eq.true" : string.Empty;
            constrainedIds = IntersectIds(
                constrainedIds,
                await LoadIdsAsync(
                    $"demanda_responsavel?select=demanda_id&user_id=eq.{Uri.EscapeDataString(filters.ResponsavelPrincipalId)}{principalFilter}&limit=100000",
                    "demanda_id",
                    cancellationToken));
        }

        if (filters.SetorIds?.Count > 0)
        {
            constrainedIds = IntersectIds(
                constrainedIds,
                await LoadIdsAsync(
                    $"demanda_setor?select=demanda_id&setor_id=in.({string.Join(",", filters.SetorIds.Select(Uri.EscapeDataString))})&limit=100000",
                    "demanda_id",
                    cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(filters.TipoRecorrencia))
        {
            constrainedIds = IntersectIds(
                constrainedIds,
                await LoadIdsAsync(
                    $"recorrencia_config?select=demanda_id&tipo=eq.{Uri.EscapeDataString(filters.TipoRecorrencia)}&limit=100000",
                    "demanda_id",
                    cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(filters.PesquisarTarefaOuObservacao))
        {
            var search = BuildIlikeValue(filters.PesquisarTarefaOuObservacao);
            var subtarefaIds = await LoadIdsAsync(
                $"subtarefa?select=demanda_id&titulo=ilike.{search}&limit=100000",
                "demanda_id",
                cancellationToken);
            var observacaoIds = await LoadIdsAsync(
                $"observacao?select=demanda_id&texto=ilike.{search}&limit=100000",
                "demanda_id",
                cancellationToken);
            constrainedIds = IntersectIds(constrainedIds, UnionIds(subtarefaIds, observacaoIds));
        }

        HashSet<string>? pesquisaGeralIds = null;
        if (!string.IsNullOrWhiteSpace(filters.PesquisaGeral))
        {
            pesquisaGeralIds = await BuildAdminPesquisaGeralIdsAsync(filters.PesquisaGeral, cancellationToken);
        }

        HashSet<string>? anexoIds = IsAnexoFilter(filters.Anexos)
            ? await LoadIdsAsync("anexo?select=demanda_id&limit=100000", "demanda_id", cancellationToken)
            : null;

        var filtered = rows
            .Where(row =>
            {
                if (!row.GetBooleanOrDefault("is_privada"))
                {
                    return true;
                }

                if (canViewAllPrivateDemandas)
                {
                    return true;
                }

                return string.Equals(row.GetNullableString("private_owner_user_id"), userId, StringComparison.Ordinal);
            })
            .Where(row => MatchesAdminBaseFilters(row, filters))
            .Where(row =>
            {
                var demandaId = row.GetStringOrEmpty("id");
                if (!MatchesAnexosFilter(demandaId, filters.Anexos, anexoIds))
                {
                    return false;
                }

                if (constrainedIds is not null && !constrainedIds.Contains(demandaId))
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(filters.PesquisaGeral))
                {
                    return true;
                }

                return MatchesAdminPesquisaGeralBase(row, filters.PesquisaGeral!)
                    || (pesquisaGeralIds?.Contains(demandaId) ?? false);
            })
            .ToList();

        var ordered = ApplyDemandasSort(filtered, filters).ToList();

        var pageRows = ordered
            .Skip(pagination.Offset)
            .Take(pagination.PageSize)
            .ToArray();

        var data = new List<object>(pageRows.Length);
        foreach (var row in pageRows)
        {
            data.Add(await BuildDetailFromDirectRowAsync(row, includeDetail: false, cancellationToken));
        }

        return new
        {
            data,
            total = ordered.Count,
        };
    }

    public async Task<object> UpdateAsync(string userId, string id, UpdateDemandaRequest request, CancellationToken cancellationToken)
    {
        _ = await FindOneAsync(userId, id, cancellationToken);
        var isResponsavelPrincipal = await IsResponsavelPrincipalAsync(userId, id, cancellationToken);
        var newStatus = request.Status;
        if (!string.IsNullOrWhiteSpace(newStatus) && !isResponsavelPrincipal && !string.Equals(newStatus, "standby", StringComparison.Ordinal))
        {
            newStatus = "standby";
        }

        var updates = new Dictionary<string, object?>();
        if (request.Assunto is not null) updates["assunto"] = request.Assunto;
        if (request.Prioridade.HasValue) updates["prioridade"] = request.Prioridade.Value;
        if (request.Prazo is not null) updates["prazo"] = request.Prazo;
        if (request.IsPrivada.HasValue)
        {
            if (!await _visibility.CanManagePrivateDemandasAsync(userId, cancellationToken))
            {
                throw new InvalidOperationException("Apenas o usuario mestre autorizado pode alterar a privacidade da demanda.");
            }

            updates["is_privada"] = request.IsPrivada.Value;
            updates["private_owner_user_id"] = request.IsPrivada.Value ? userId : null;
        }
        if (!string.IsNullOrWhiteSpace(newStatus))
        {
            updates["status"] = newStatus;
            updates["resolvido_em"] = string.Equals(newStatus, "concluido", StringComparison.Ordinal)
                ? DateTime.UtcNow.ToString("O")
                : null;
        }
        if (request.ObservacoesGerais is not null) updates["observacoes_gerais"] = request.ObservacoesGerais;
        if (request.IsRecorrente.HasValue) updates["is_recorrente"] = request.IsRecorrente.Value;
        if (updates.Count > 0)
        {
            await _supabase.UpdateSingleAsync("Demanda", $"id=eq.{Uri.EscapeDataString(id)}", updates, cancellationToken);
        }

        if (request.Setores is not null || request.ClienteIds is not null || request.Responsaveis is not null || request.Subtarefas is not null)
        {
            await ReplaceDemandaRelationsAsync(id, request.Setores, request.ClienteIds, request.Responsaveis, request.Subtarefas, cancellationToken);
        }

        if (request.PrivateViewerIds is not null)
        {
            await ReplacePrivateViewersAsync(id, request.PrivateViewerIds, cancellationToken);
        }

        if (request.Recorrencia is not null)
        {
            await UpsertRecorrenciaAsync(id, request.Recorrencia, cancellationToken);
            await _supabase.UpdateSingleAsync("Demanda", $"id=eq.{Uri.EscapeDataString(id)}", new { is_recorrente = true }, cancellationToken);
        }
        else if (request.IsRecorrente == false)
        {
            await _supabase.DeleteAsync("recorrencia_config", $"demanda_id=eq.{Uri.EscapeDataString(id)}", cancellationToken);
        }

        await RegistrarAlteracoesDemandaAsync(userId, id, request, newStatus, cancellationToken);

        return await FindOneAsync(userId, id, cancellationToken);
    }

    public async Task<object> RemoveAsync(string userId, string id, CancellationToken cancellationToken)
    {
        _ = await FindOneAsync(userId, id, cancellationToken);
        await _supabase.DeleteAsync("Demanda", $"id=eq.{Uri.EscapeDataString(id)}", cancellationToken);
        return new { id };
    }

    public async Task<object> AddObservacaoAsync(string userId, string demandaId, string texto, CancellationToken cancellationToken)
    {
        var demanda = await _supabase.QuerySingleAsync(
            $"Demanda?select=*&id=eq.{Uri.EscapeDataString(demandaId)}&limit=1",
            cancellationToken);
        if (demanda is null)
        {
            throw new KeyNotFoundException("Demanda nao encontrada");
        }

        if (!await _visibility.CanViewDemandaAsync(userId, demandaId, cancellationToken))
        {
            throw new UnauthorizedAccessException("Sem permissao para ver esta demanda");
        }
        if (string.IsNullOrWhiteSpace(texto))
        {
            throw new InvalidOperationException("Informe o texto da observação.");
        }

        await _supabase.InsertSingleAsync("observacao", new
        {
            demanda_id = demandaId,
            user_id = userId,
            texto = texto.Trim(),
        }, cancellationToken);

        var demandaUpdates = new Dictionary<string, object?>
        {
            ["ultima_observacao_em"] = DateTime.UtcNow.ToString("O"),
        };

        var isResponsavel = await IsResponsavelPrincipalAsync(userId, demandaId, cancellationToken);
        if (!isResponsavel)
        {
            demandaUpdates["status"] = "standby";
        }

        await _supabase.UpdateSingleAsync("Demanda", $"id=eq.{Uri.EscapeDataString(demandaId)}", demandaUpdates, cancellationToken);
        await _audit.AddDemandaEventAsync(
            demandaId,
            userId,
            "observacao_adicionada",
            "Observacao adicionada.",
            null,
            cancellationToken);
        return await FindOneAsync(userId, demandaId, cancellationToken);
    }

    public async Task<object> UpdateObservacaoAsync(string userId, string demandaId, string observacaoId, string texto, CancellationToken cancellationToken)
    {
        var demanda = await _supabase.QuerySingleAsync(
            $"Demanda?select=*&id=eq.{Uri.EscapeDataString(demandaId)}&limit=1",
            cancellationToken);
        if (demanda is null)
        {
            throw new KeyNotFoundException("Demanda nao encontrada");
        }

        if (!await _visibility.CanViewDemandaAsync(userId, demandaId, cancellationToken))
        {
            throw new UnauthorizedAccessException("Sem permissao para ver esta demanda");
        }
        if (string.IsNullOrWhiteSpace(texto))
        {
            throw new InvalidOperationException("Informe o texto da observação.");
        }

        var observacao = await _supabase.QuerySingleAsync(
            $"observacao?select=id,user_id&demanda_id=eq.{Uri.EscapeDataString(demandaId)}&id=eq.{Uri.EscapeDataString(observacaoId)}&limit=1",
            cancellationToken);
        if (observacao is null)
        {
            throw new KeyNotFoundException("Observação não encontrada");
        }

        var isResponsavel = await IsResponsavelPrincipalAsync(userId, demandaId, cancellationToken);
        if (!string.Equals(observacao.Value.GetStringOrEmpty("user_id"), userId, StringComparison.Ordinal) && !isResponsavel)
        {
            throw new UnauthorizedAccessException("Sem permissão para editar esta observação.");
        }

        await _supabase.UpdateSingleAsync(
            "observacao",
            $"id=eq.{Uri.EscapeDataString(observacaoId)}&demanda_id=eq.{Uri.EscapeDataString(demandaId)}",
            new
            {
                texto = texto.Trim(),
                user_id = userId,
                created_at = DateTime.UtcNow.ToString("O"),
            },
            cancellationToken);

        await _audit.AddDemandaEventAsync(
            demandaId,
            userId,
            "observacao_editada",
            "Observacao editada.",
            new { observacaoId },
            cancellationToken);

        return await FindOneAsync(userId, demandaId, cancellationToken);
    }

    public async Task<object> AddAnexoAsync(
        string userId,
        string demandaId,
        byte[] buffer,
        string originalFilename,
        string? displayName,
        string contentType,
        long size,
        CancellationToken cancellationToken)
    {
        var demanda = await _supabase.QuerySingleAsync(
            $"Demanda?select=*&id=eq.{Uri.EscapeDataString(demandaId)}&limit=1",
            cancellationToken);
        if (demanda is null)
        {
            throw new KeyNotFoundException("Demanda nao encontrada");
        }

        if (!await _visibility.CanViewDemandaAsync(userId, demandaId, cancellationToken))
        {
            throw new UnauthorizedAccessException("Sem permissao para ver esta demanda");
        }
        if (buffer.Length == 0)
        {
            throw new InvalidOperationException("Arquivo inválido.");
        }

        var legacyDemandaId = await ResolveLegacyDemandaIdAsync(demanda.Value, demandaId, cancellationToken);
        if (_options.PreferLegacyAttachments && !string.IsNullOrWhiteSpace(legacyDemandaId) && _legacyAttachments.IsConfigured)
        {
            var legacy = await _legacyAttachments.UploadAsync(
                legacyDemandaId,
                buffer,
                string.IsNullOrWhiteSpace(originalFilename) ? "file" : originalFilename,
                string.IsNullOrWhiteSpace(displayName) ? originalFilename : displayName!,
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                cancellationToken);

            var createdLegacy = await _supabase.InsertSingleAsync("anexo", new
            {
                demanda_id = demandaId,
                filename = string.IsNullOrWhiteSpace(legacy.Filename) ? originalFilename : legacy.Filename,
                mime_type = string.IsNullOrWhiteSpace(contentType) ? GuessMimeType(legacy.Filename) : contentType,
                size,
                storage_path = legacy.StoragePath,
            }, cancellationToken);

            await _audit.AddDemandaEventAsync(
                demandaId,
                userId,
                "anexo_adicionado",
                $"Anexo adicionado no legado: {createdLegacy.GetStringOrEmpty("filename")}.",
                new
                {
                    anexoId = createdLegacy.GetNullableString("id"),
                    filename = createdLegacy.GetStringOrEmpty("filename"),
                    storage = "legacy",
                },
                cancellationToken);

            return createdLegacy.Clone();
        }

        if (_options.RequireLegacyAttachments)
        {
            throw new InvalidOperationException("Esta demanda ainda não possui vínculo com uma demanda do sistema antigo para armazenar anexos no legado.");
        }

        var safeName = $"{Guid.NewGuid():D}-{SanitizeFilename(string.IsNullOrWhiteSpace(originalFilename) ? "file" : originalFilename)}";
        var bucket = await EnsureAnexosBucketAsync(cancellationToken);
        var objectPath = $"demandas/{demandaId}/{safeName}";
        await _supabase.UploadObjectAsync(
            bucket,
            objectPath,
            buffer,
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            upsert: false,
            cancellationToken);

        var created = await _supabase.InsertSingleAsync("anexo", new
        {
            demanda_id = demandaId,
            filename = string.IsNullOrWhiteSpace(originalFilename) ? "file" : originalFilename,
            mime_type = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            size,
            storage_path = BuildSupabaseStoragePath(bucket, objectPath),
        }, cancellationToken);

        await _audit.AddDemandaEventAsync(
            demandaId,
            userId,
            "anexo_adicionado",
            $"Anexo adicionado: {created.GetStringOrEmpty("filename")}.",
            new
            {
                anexoId = created.GetNullableString("id"),
                filename = created.GetStringOrEmpty("filename"),
            },
            cancellationToken);

        return created.Clone();
    }

    public async Task<DemandaDownloadResult> GetAnexoForDownloadAsync(
        string userId,
        string demandaId,
        string anexoId,
        CancellationToken cancellationToken)
    {
        _ = await FindOneAsync(userId, demandaId, cancellationToken);
        var anexo = await _supabase.QuerySingleAsync(
            $"anexo?select=*&id=eq.{Uri.EscapeDataString(anexoId)}&demanda_id=eq.{Uri.EscapeDataString(demandaId)}&limit=1",
            cancellationToken);
        if (anexo is null)
        {
            throw new KeyNotFoundException("Anexo não encontrado");
        }

        var storage = ParseAnexoStoragePath(anexo.Value.GetNullableString("storage_path"));
        if (LegacyAttachmentService.TryParseStoragePath(anexo.Value.GetNullableString("storage_path"), out var legacyReference))
        {
            var legacyDownload = await _legacyAttachments.DownloadAsync(legacyReference, cancellationToken);
            return new DemandaDownloadResult(
                legacyDownload.Buffer,
                anexo.Value.GetStringOrEmpty("filename"),
                anexo.Value.GetNullableString("mime_type") ?? legacyDownload.ContentType ?? "application/octet-stream");
        }

        if (storage.Mode == "supabase")
        {
            var download = await _supabase.DownloadObjectAsync(storage.Bucket!, storage.ObjectPath, cancellationToken);
            return new DemandaDownloadResult(
                download.Buffer,
                anexo.Value.GetStringOrEmpty("filename"),
                anexo.Value.GetNullableString("mime_type") ?? download.ContentType);
        }

        var uploadDir = Environment.GetEnvironmentVariable("UPLOAD_DIR") ?? "./uploads";
        var fullPath = Path.GetFullPath(Path.Combine(uploadDir, storage.ObjectPath));
        if (!File.Exists(fullPath))
        {
            throw new KeyNotFoundException("Arquivo não encontrado");
        }

        return new DemandaDownloadResult(
            await File.ReadAllBytesAsync(fullPath, cancellationToken),
            anexo.Value.GetStringOrEmpty("filename"),
            anexo.Value.GetNullableString("mime_type") ?? "application/octet-stream");
    }

    public async Task<IReadOnlyList<object>> ExportAsync(string userId, ListDemandasFiltersQuery filters, CancellationToken cancellationToken)
    {
        var exportFilters = new ListDemandasFiltersQuery
        {
            ClienteId = filters.ClienteId,
            Assunto = filters.Assunto,
            Status = filters.Status,
            OcultarStandby = filters.OcultarStandby,
            OcultarConcluidas = filters.OcultarConcluidas,
            TipoRecorrencia = filters.TipoRecorrencia,
            Protocolo = filters.Protocolo,
            Prioridade = filters.Prioridade,
            CriadorId = filters.CriadorId,
            ResponsavelPrincipalId = filters.ResponsavelPrincipalId,
            ResponsavelApenasPrincipal = filters.ResponsavelApenasPrincipal,
            SetorIds = filters.SetorIds?.ToList(),
            CondicaoPrazo = filters.CondicaoPrazo,
            PesquisarTarefaOuObservacao = filters.PesquisarTarefaOuObservacao,
            PesquisaGeral = filters.PesquisaGeral,
            DataCriacaoDe = filters.DataCriacaoDe,
            DataCriacaoAte = filters.DataCriacaoAte,
            PrazoDe = filters.PrazoDe,
            PrazoAte = filters.PrazoAte,
            Page = 1,
            PageSize = 10000,
        };
        var result = await ListAsync(userId, exportFilters, cancellationToken);
        var json = JsonSerializer.Serialize(result);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("data")
            .EnumerateArray()
            .Select(item => (object)item.Clone())
            .ToList();
    }

    public async Task<object> BuscarIaAsync(
        string userId,
        string query,
        string? scope,
        BuscarIaContextRequest? context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.OpenAiApiKey))
        {
            throw new InvalidOperationException("Busca por IA não configurada. Defina OPENAI_API_KEY no servidor.");
        }

        var requestScope = NormalizeIaScope(scope);
        var searchMode = ParsePesquisaGeralMode(query, requestScope);
        var referenceData = await LoadIaReferenceDataAsync(cancellationToken);
        var aiExtraction = await TryExtractFiltersWithOpenAiAsync(query, referenceData, cancellationToken);
        var filters = BuildIaFilters(query, aiExtraction.Filters, referenceData, requestScope);
        ApplyIaContextToFilters(query, filters, context);

        var shouldSearchDemandas = requestScope is "all" or "demandas" or "observacoes_gerais" or "status";
        var previewData = new List<JsonElement>();
        var previewTotal = 0;
        if (shouldSearchDemandas)
        {
            var result = await ListAsync(userId, filters, cancellationToken);
            (previewData, previewTotal) = ParseListResult(result);
        }

        var protocolos = previewData
            .Select(item => item.GetProperty("protocolo").GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Take(3)
            .Cast<string>()
            .ToList();

        var globalEvidence = await SearchGlobalCatalogAsync(query, requestScope, cancellationToken);
        var links = new List<object>();
        var demandasLink = BuildDemandasLink(filters);
        links.Add(new { label = previewTotal > 0 ? "Abrir demandas com esses filtros" : "Abrir lista de demandas", url = demandasLink });
        foreach (var link in InferLinksFromQuery(query))
        {
            if (!links.Any(existing => JsonSerializer.Serialize(existing).Contains(link.Url, StringComparison.Ordinal)))
            {
                links.Add(new { label = link.Label, url = link.Url });
            }
        }
        foreach (var item in globalEvidence.Matches)
        {
            if (!links.Any(existing => JsonSerializer.Serialize(existing).Contains(item.Route, StringComparison.Ordinal)))
            {
                links.Add(new { label = $"Abrir {item.ModuleLabel}", url = item.Route });
            }
        }

        var message = BuildIaSummary(
            query,
            requestScope,
            previewTotal,
            protocolos,
            globalEvidence.Matches,
            aiExtraction.UsedAi,
            aiExtraction.ResponseText);
        var topMatches = previewData.Take(5).Select(item => new
        {
            demandaId = item.GetProperty("id").GetString() ?? string.Empty,
            route = $"/demandas/{item.GetProperty("id").GetString() ?? string.Empty}",
            protocolo = item.GetProperty("protocolo").GetString() ?? "—",
            assunto = item.GetProperty("assunto").GetString() ?? "—",
            fields = BuildTopMatchFields(item, filters),
        }).ToList();

        return new
        {
            filters,
            message,
            ai = new
            {
                online = true,
                used = aiExtraction.UsedAi,
                mode = aiExtraction.Mode,
                engine = aiExtraction.Engine,
            },
            links,
            preview = new
            {
                total = previewTotal,
                protocolos,
            },
            evidence = new
            {
                mode = searchMode,
                modeLabel = PesquisaGeralModeLabel(searchMode),
                searchTerm = filters.PesquisaGeral,
                fieldCounts = BuildFieldCounts(filters),
                moduleCounts = globalEvidence.ModuleCounts,
                topMatches,
                globalMatches = globalEvidence.GlobalMatchesPayload,
            },
        };
    }

    public async Task<object> GetDashboardKpisAsync(bool avaliarComIa, CancellationToken cancellationToken)
    {
        _ = avaliarComIa;
        try
        {
            var rpcResult = await _supabase.RpcAsync<JsonElement>("rpc_dashboard_kpis", new { }, cancellationToken);
            var row = rpcResult.ValueKind switch
            {
                JsonValueKind.Array => rpcResult.EnumerateArray().FirstOrDefault(),
                JsonValueKind.Object => rpcResult,
                _ => default,
            };
            if (row.ValueKind != JsonValueKind.Undefined)
            {
                return new
                {
                    metricas = MapDashboardMetricas(row),
                };
            }
        }
        catch
        {
        }

        var rowsFallback = await _supabase.QueryAllRowsAsync(
            "Demanda?select=id,status,created_at,updated_at,resolvido_em,ultima_observacao_em&order=created_at.desc",
            cancellationToken);
        var now = DateTime.UtcNow;
        var concluidas = rowsFallback.Where(row => string.Equals(row.GetStringOrEmpty("status"), "concluido", StringComparison.Ordinal)).ToList();
        var temposResolucao = concluidas
            .Select(row => ComputeTempoHoras(ParseDate(row.GetNullableString("created_at")), ParseDate(row.GetNullableString("resolvido_em"))))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();
        var comUltimaObs = rowsFallback
            .Select(row => ParseDate(row.GetNullableString("ultima_observacao_em")))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();

        var porStatus = rowsFallback
            .GroupBy(row => row.GetStringOrEmpty("status"))
            .ToDictionary(group => group.Key, group => group.Count());

        var metricas = new
        {
            totalDemandas = rowsFallback.Length,
            concluidas = concluidas.Count,
            emAberto = rowsFallback.Count(row => string.Equals(row.GetStringOrEmpty("status"), "em_aberto", StringComparison.Ordinal)),
            tempoMedioResolucaoHoras = temposResolucao.Count > 0 ? Math.Round(temposResolucao.Average(), 1) : (double?)null,
            demandasSemObservacaoRecente = rowsFallback.Count(row =>
            {
                var ultima = ParseDate(row.GetNullableString("ultima_observacao_em"));
                return !ultima.HasValue || (now - ultima.Value).TotalHours > 24 * 7;
            }),
            tempoMedioDesdeUltimaObservacaoHoras = comUltimaObs.Count > 0 ? Math.Round(comUltimaObs.Average(value => (now - value).TotalHours), 1) : (double?)null,
            porStatus,
        };

        return new { metricas };
    }

    private async Task<string> GerarProtocoloAsync(CancellationToken cancellationToken)
    {
        var year = DateTime.UtcNow.Year;
        var start = Uri.EscapeDataString($"{year}-01-01");
        var end = Uri.EscapeDataString($"{year + 1}-01-01");
        var rows = await _supabase.QueryRowsAsync(
            $"Demanda?select=id&created_at=gte.{start}&created_at=lt.{end}&limit=100000",
            cancellationToken);
        return $"LUX-{year}-{(rows.Length + 1).ToString().PadLeft(5, '0')}";
    }

    private static (int Page, int PageSize, int Offset) GetPagination(ListDemandasFiltersQuery filters)
    {
        var page = Math.Max(filters.Page ?? 1, 1);
        var requestedPageSize = Math.Max(filters.PageSize ?? 100, 1);
        var pageSize = Math.Min(requestedPageSize, 10_000);
        return (page, pageSize, (page - 1) * pageSize);
    }

    private static IEnumerable<JsonElement> ApplyDemandasSort(IReadOnlyList<JsonElement> rows, ListDemandasFiltersQuery filters)
    {
        var sortBy = NormalizeSortBy(filters.SortBy);
        var descending = NormalizeSortDirection(filters.SortDirection, sortBy) == "desc";

        return sortBy switch
        {
            "id" => OrderByText(rows, row => row.GetNullableString("id"), descending),
            "createdAt" => OrderByDate(rows, row => row.GetNullableString("created_at"), descending),
            "protocolo" => OrderByText(rows, row => row.GetNullableString("protocolo"), descending),
            "prioridade" => OrderByBoolean(rows, row => row.GetBooleanOrDefault("prioridade"), descending),
            "assunto" => OrderByText(rows, row => row.GetNullableString("assunto"), descending),
            "prazo" => OrderByDate(rows, row => row.GetNullableString("prazo"), descending),
            "status" => OrderByStatus(rows, descending),
            _ => OrderByDefaultDemandas(rows),
        };
    }

    private static string? NormalizeSortBy(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return normalized switch
        {
            "id" => "id",
            "createdAt" => "createdAt",
            "protocolo" => "protocolo",
            "prioridade" => "prioridade",
            "cliente" => "cliente",
            "assunto" => "assunto",
            "criador" => "criador",
            "responsaveis" => "responsaveis",
            "prazo" => "prazo",
            "status" => "status",
            _ => null,
        };
    }

    private static string NormalizeSortDirection(string? value, string? sortBy)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (normalized is "asc" or "desc")
        {
            return normalized;
        }

        return sortBy is "createdAt" or "prioridade" or "prazo" ? "desc" : "asc";
    }

    private static IOrderedEnumerable<JsonElement> OrderByDefaultDemandas(IEnumerable<JsonElement> rows) =>
        rows
            .OrderByDescending(row => row.GetBooleanOrDefault("prioridade"))
            .ThenBy(row => DateSortTicks(row.GetNullableString("prazo"), descending: false))
            .ThenByDescending(row => DateSortTicks(row.GetNullableString("created_at"), descending: true))
            .ThenBy(row => row.GetNullableString("protocolo") ?? string.Empty, StringComparer.Create(new CultureInfo("pt-BR"), ignoreCase: true));

    private static IOrderedEnumerable<JsonElement> OrderByDate(
        IEnumerable<JsonElement> rows,
        Func<JsonElement, string?> selector,
        bool descending)
    {
        var ordered = descending
            ? rows.OrderByDescending(row => DateSortTicks(selector(row), descending: true))
            : rows.OrderBy(row => DateSortTicks(selector(row), descending: false));

        return ordered
            .ThenByDescending(row => DateSortTicks(row.GetNullableString("created_at"), descending: true))
            .ThenBy(row => row.GetNullableString("protocolo") ?? string.Empty, StringComparer.Create(new CultureInfo("pt-BR"), ignoreCase: true));
    }

    private static IOrderedEnumerable<JsonElement> OrderByText(
        IEnumerable<JsonElement> rows,
        Func<JsonElement, string?> selector,
        bool descending)
    {
        var comparer = StringComparer.Create(new CultureInfo("pt-BR"), ignoreCase: true);
        var ordered = descending
            ? rows
                .OrderBy(row => string.IsNullOrWhiteSpace(selector(row)) ? 1 : 0)
                .ThenByDescending(row => selector(row) ?? string.Empty, comparer)
            : rows
                .OrderBy(row => string.IsNullOrWhiteSpace(selector(row)) ? 1 : 0)
                .ThenBy(row => selector(row) ?? string.Empty, comparer);

        return ordered
            .ThenByDescending(row => DateSortTicks(row.GetNullableString("created_at"), descending: true))
            .ThenBy(row => row.GetNullableString("protocolo") ?? string.Empty, comparer);
    }

    private static IOrderedEnumerable<JsonElement> OrderByBoolean(
        IEnumerable<JsonElement> rows,
        Func<JsonElement, bool> selector,
        bool descending)
    {
        var ordered = descending
            ? rows.OrderByDescending(selector)
            : rows.OrderBy(selector);

        return ordered
            .ThenBy(row => DateSortTicks(row.GetNullableString("prazo"), descending: false))
            .ThenByDescending(row => DateSortTicks(row.GetNullableString("created_at"), descending: true));
    }

    private static IOrderedEnumerable<JsonElement> OrderByStatus(IEnumerable<JsonElement> rows, bool descending)
    {
        var ordered = descending
            ? rows
                .OrderBy(row => StatusSortWeight(row.GetNullableString("status")) >= 999 ? 1 : 0)
                .ThenByDescending(row => StatusSortWeight(row.GetNullableString("status")))
            : rows
                .OrderBy(row => StatusSortWeight(row.GetNullableString("status")) >= 999 ? 1 : 0)
                .ThenBy(row => StatusSortWeight(row.GetNullableString("status")));

        return ordered
            .ThenByDescending(row => DateSortTicks(row.GetNullableString("created_at"), descending: true))
            .ThenBy(row => row.GetNullableString("protocolo") ?? string.Empty, StringComparer.Create(new CultureInfo("pt-BR"), ignoreCase: true));
    }

    private static int StatusSortWeight(string? value) =>
        value switch
        {
            "em_aberto" => 1,
            "em_andamento" => 2,
            "standby" => 3,
            "concluido" => 4,
            "cancelado" => 5,
            _ => 999,
        };

    private static long DateSortTicks(string? value, bool descending)
    {
        var date = ParseDate(value);
        if (!date.HasValue)
        {
            return descending ? long.MinValue : long.MaxValue;
        }

        return date.Value.Ticks;
    }

    private async Task<IaReferenceData> LoadIaReferenceDataAsync(CancellationToken cancellationToken)
    {
        var setoresTask = _supabase.QueryAllRowsAsync("Setor?select=id,name&order=name.asc", cancellationToken);
        var clientesTask = _supabase.QueryAllRowsAsync("Cliente?select=id,name,active&active=eq.true&order=name.asc", cancellationToken);
        var usersTask = _supabase.QueryAllRowsAsync("User?select=id,name,active&active=eq.true&order=name.asc", cancellationToken);
        await Task.WhenAll(setoresTask, clientesTask, usersTask);

        return new IaReferenceData(
            setoresTask.Result.Select(row => new NamedEntity(row.GetStringOrEmpty("id"), row.GetStringOrEmpty("name"))).ToList(),
            clientesTask.Result.Select(row => new NamedEntity(row.GetStringOrEmpty("id"), row.GetStringOrEmpty("name"))).ToList(),
            usersTask.Result.Select(row => new NamedEntity(row.GetStringOrEmpty("id"), row.GetStringOrEmpty("name"))).ToList());
    }

    private async Task<IaFilterExtractionResult> TryExtractFiltersWithOpenAiAsync(
        string query,
        IaReferenceData referenceData,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.OpenAiApiKey);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model = string.IsNullOrWhiteSpace(_options.OpenAiModel) ? "gpt-4.1-mini" : _options.OpenAiModel,
                    temperature = 0,
                    messages = new object[]
                    {
                        new
                        {
                            role = "system",
                            content =
                                "Você converte pedidos em português para filtros de demandas. " +
                                "Retorne apenas JSON válido no formato {\"filters\": {...}, \"responseText\": \"...\"}. " +
                                "Não invente IDs. Use somente IDs das listas enviadas. " +
                                "Campos válidos: clienteId, assunto, status, tipoRecorrencia, protocolo, prioridade, criadorId, responsavelPrincipalId, setorIds, condicaoPrazo, pesquisarTarefaOuObservacao, pesquisaGeral, dataCriacaoDe, dataCriacaoAte, prazoDe, prazoAte. " +
                                "responseText deve ser uma resposta curta, em português do Brasil, explicando o que você entendeu da busca e o que será aberto no sistema."
                        },
                        new
                        {
                            role = "user",
                            content = JsonSerializer.Serialize(new
                            {
                                query,
                                setores = referenceData.Setores,
                                clientes = referenceData.Clientes,
                                users = referenceData.Users,
                            })
                        }
                    }
                }),
                Encoding.UTF8,
                "application/json");

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new IaFilterExtractionResult(
                    new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
                    false,
                    "fallback",
                    null,
                    null);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var content = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "{}";
            var cleaned = content
                .Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("```", string.Empty, StringComparison.Ordinal)
                .Trim();

            using var parsed = JsonDocument.Parse(cleaned);
            var root = parsed.RootElement;
            var filters = root.TryGetProperty("filters", out var filtersElement) && filtersElement.ValueKind == JsonValueKind.Object
                ? filtersElement
                : root;
            if (filters.ValueKind != JsonValueKind.Object)
            {
                return new IaFilterExtractionResult(
                    new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
                    false,
                    "fallback",
                    null,
                    null);
            }

            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in filters.EnumerateObject())
            {
                result[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Array => property.Value.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)).ToList(),
                    _ => property.Value.ToString(),
                };
            }

            var responseText = root.TryGetProperty("responseText", out var responseElement)
                ? responseElement.GetString()?.Trim()
                : null;

            return new IaFilterExtractionResult(
                result,
                true,
                "openai",
                string.IsNullOrWhiteSpace(responseText) ? null : responseText,
                "gpt-4o-mini");
        }
        catch
        {
            return new IaFilterExtractionResult(
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
                false,
                "fallback",
                null,
                null);
        }
    }

    private ListDemandasFiltersQuery BuildIaFilters(
        string query,
        Dictionary<string, object?> aiFilters,
        IaReferenceData referenceData,
        string scope)
    {
        var heuristics = ExtractHeuristicIaFilters(query, referenceData);
        var validClienteIds = referenceData.Clientes.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var validUserIds = referenceData.Users.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var validSetorIds = referenceData.Setores.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var mentionsCliente = QueryMentions(query, "cliente");
        var mentionsCriador = QueryMentions(query, "criador", "criado por");
        var mentionsResponsavel = QueryMentions(query, "responsavel", "responsável");
        var mentionsSetor = QueryMentions(query, "setor");
        var mentionsPrioridade = QueryMentionsPrioridade(query);
        var mentionsStatus = QueryMentionsStatus(query);
        var mentionsRecorrencia = QueryMentions(query, "recorr", "diaria", "diario", "semanal", "quinzenal", "mensal");
        var mentionsPrazo = QueryMentions(query, "prazo", "vencid", "finaliz");
        var filters = new ListDemandasFiltersQuery
        {
            ClienteId = mentionsCliente
                ? MatchValidId(aiFilters, "clienteId", validClienteIds) ?? heuristics.ClienteId
                : heuristics.ClienteId,
            Assunto = MatchString(aiFilters, "assunto") ?? heuristics.Assunto,
            Status = mentionsStatus
                ? NormalizeStatus(MatchString(aiFilters, "status")) ?? heuristics.Status
                : heuristics.Status,
            TipoRecorrencia = mentionsRecorrencia
                ? NormalizeRecorrencia(MatchString(aiFilters, "tipoRecorrencia")) ?? heuristics.TipoRecorrencia
                : heuristics.TipoRecorrencia,
            Protocolo = MatchString(aiFilters, "protocolo") ?? heuristics.Protocolo,
            Prioridade = mentionsPrioridade
                ? MatchBool(aiFilters, "prioridade") ?? heuristics.Prioridade
                : heuristics.Prioridade,
            CriadorId = mentionsCriador
                ? MatchValidId(aiFilters, "criadorId", validUserIds) ?? heuristics.CriadorId
                : heuristics.CriadorId,
            ResponsavelPrincipalId = mentionsResponsavel
                ? MatchValidId(aiFilters, "responsavelPrincipalId", validUserIds) ?? heuristics.ResponsavelPrincipalId
                : heuristics.ResponsavelPrincipalId,
            SetorIds = mentionsSetor
                ? MatchValidIds(aiFilters, "setorIds", validSetorIds) ?? heuristics.SetorIds
                : heuristics.SetorIds,
            CondicaoPrazo = mentionsPrazo
                ? NormalizeCondicaoPrazo(MatchString(aiFilters, "condicaoPrazo")) ?? heuristics.CondicaoPrazo
                : heuristics.CondicaoPrazo,
            PesquisarTarefaOuObservacao = QueryMentions(query, "tarefa", "subtarefa", "observacao", "observações", "obs")
                ? MatchString(aiFilters, "pesquisarTarefaOuObservacao") ?? heuristics.PesquisarTarefaOuObservacao
                : heuristics.PesquisarTarefaOuObservacao,
            PesquisaGeral = NormalizePesquisaGeralFromQuery(query, MatchString(aiFilters, "pesquisaGeral") ?? heuristics.PesquisaGeral),
            DataCriacaoDe = MatchDate(aiFilters, "dataCriacaoDe"),
            DataCriacaoAte = MatchDate(aiFilters, "dataCriacaoAte"),
            PrazoDe = MatchDate(aiFilters, "prazoDe"),
            PrazoAte = MatchDate(aiFilters, "prazoAte"),
        };

        if (scope == "status" && string.IsNullOrWhiteSpace(filters.PesquisaGeral) && !string.IsNullOrWhiteSpace(filters.Status))
        {
            filters.PesquisaGeral = StatusLabelPt(filters.Status!);
            filters.Status = null;
        }

        return filters;
    }

    private static void ApplyIaContextToFilters(string query, ListDemandasFiltersQuery filters, BuscarIaContextRequest? context)
    {
        _ = query;
        if (context?.PreviousFilters is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(filters.ClienteId) && context.PreviousFilters.TryGetValue("clienteId", out var clienteId))
        {
            filters.ClienteId = clienteId?.ToString();
        }
        if (string.IsNullOrWhiteSpace(filters.ResponsavelPrincipalId) && context.PreviousFilters.TryGetValue("responsavelPrincipalId", out var responsavelId))
        {
            filters.ResponsavelPrincipalId = responsavelId?.ToString();
        }
        if (string.IsNullOrWhiteSpace(filters.CriadorId) && context.PreviousFilters.TryGetValue("criadorId", out var criadorId))
        {
            filters.CriadorId = criadorId?.ToString();
        }
    }

    private async Task<GlobalIaEvidence> SearchGlobalCatalogAsync(string query, string scope, CancellationToken cancellationToken)
    {
        var normalizedQuery = NormalizeIaText(query);
        List<GlobalIaMatch> matches = [];
        try
        {
            var rows = await _supabase.RpcAsync<JsonElement[]>("rpc_ia_global_catalog", new { }, cancellationToken);
            matches = rows
                .Select(row => new GlobalIaMatch(
                    row.GetStringOrEmpty("module"),
                    GlobalModuleLabel(row.GetStringOrEmpty("module")),
                    row.GetStringOrEmpty("title"),
                    row.GetStringOrEmpty("snippet"),
                    row.GetStringOrEmpty("route"),
                    $"{row.GetStringOrEmpty("title")} {row.GetStringOrEmpty("snippet")} {row.GetStringOrEmpty("searchable")}"))
                .Where(item => ShouldProcessGlobalModule(scope, item.Module))
                .Where(item => string.IsNullOrWhiteSpace(normalizedQuery) || NormalizeIaText(item.Searchable).Contains(normalizedQuery, StringComparison.Ordinal))
                .Take(8)
                .ToList();
        }
        catch
        {
            matches = [];
        }

        var counts = matches
            .GroupBy(item => item.Module)
            .Select(group => new
            {
                module = group.Key,
                label = GlobalModuleLabel(group.Key),
                count = group.Count(),
            })
            .ToList();

        return new GlobalIaEvidence(
            matches,
            counts.Cast<object>().ToList(),
            matches.Select(item => (object)new
            {
                module = item.Module,
                moduleLabel = item.ModuleLabel,
                title = item.Title,
                snippet = item.Snippet,
                route = string.IsNullOrWhiteSpace(item.Route) ? "/demandas" : item.Route,
            }).ToList());
    }

    private static bool ShouldProcessGlobalModule(string scope, string module) =>
        scope switch
        {
            "all" => true,
            "paginas" => false,
            "demandas" => false,
            "observacoes_gerais" => false,
            "status" => false,
            _ => string.Equals(scope, module, StringComparison.OrdinalIgnoreCase),
        };

    private static string BuildIaSummary(
        string query,
        string scope,
        int previewTotal,
        IReadOnlyList<string> protocolos,
        IReadOnlyList<GlobalIaMatch> globalMatches,
        bool usedAi,
        string? aiResponseText)
    {
        _ = query;
        if (!string.IsNullOrWhiteSpace(aiResponseText))
        {
            var summarySuffix = previewTotal > 0
                ? protocolos.Count > 0
                    ? $" Encontrei {previewTotal} demanda(s). Exemplos: {string.Join(", ", protocolos)}."
                    : $" Encontrei {previewTotal} demanda(s)."
                : globalMatches.Count > 0
                    ? " Não encontrei demandas com esse recorte, mas localizei itens relacionados em outros módulos."
                    : " Não encontrei resultados com esse recorte.";
            return $"{aiResponseText.Trim()} {summarySuffix}".Trim();
        }

        if (previewTotal > 0)
        {
            var protocolosText = protocolos.Count > 0 ? $" Exemplos: {string.Join(", ", protocolos)}." : string.Empty;
            return usedAi
                ? $"Interpretei sua busca com IA e encontrei {previewTotal} demanda(s).{protocolosText}"
                : $"Apliquei uma leitura automática da busca e encontrei {previewTotal} demanda(s).{protocolosText}";
        }

        if (globalMatches.Count > 0)
        {
            return usedAi
                ? scope == "all"
                    ? "Interpretei sua busca com IA. Não encontrei resultados em demandas, mas há correspondências em outros módulos do sistema."
                    : "Interpretei sua busca com IA. Não encontrei demandas para esse filtro, mas localizei itens em outro módulo relacionado."
                : scope == "all"
                    ? "Não encontrei resultados em demandas, mas há correspondências em outros módulos do sistema."
                    : "Não encontrei demandas para esse filtro, mas a busca localizou itens em outro módulo relacionado.";
        }

        return usedAi
            ? "Interpretei sua busca com IA, mas não encontrei resultados para esse recorte."
            : "Não encontrei resultados para essa busca.";
    }

    private static List<object> BuildTopMatchFields(JsonElement item, ListDemandasFiltersQuery filters)
    {
        var fields = new List<object>();
        if (!string.IsNullOrWhiteSpace(filters.Protocolo))
        {
            fields.Add(new { key = "protocolo", label = "Protocolo", snippet = item.GetProperty("protocolo").GetString() ?? string.Empty });
        }
        if (!string.IsNullOrWhiteSpace(filters.Assunto) || !string.IsNullOrWhiteSpace(filters.PesquisaGeral))
        {
            fields.Add(new { key = "assunto", label = "Assunto", snippet = item.GetProperty("assunto").GetString() ?? string.Empty });
        }
        if (!string.IsNullOrWhiteSpace(filters.Status))
        {
            fields.Add(new { key = "status", label = "Status", snippet = item.GetProperty("status").GetString() ?? string.Empty });
        }
        return fields;
    }

    private static List<object> BuildFieldCounts(ListDemandasFiltersQuery filters)
    {
        var counts = new List<object>();
        void Add(string key, string label, bool shouldAdd)
        {
            if (shouldAdd)
            {
                counts.Add(new { key, label, count = 1 });
            }
        }

        Add("protocolo", "Protocolo", !string.IsNullOrWhiteSpace(filters.Protocolo));
        Add("assunto", "Assunto", !string.IsNullOrWhiteSpace(filters.Assunto));
        Add("status", "Status", !string.IsNullOrWhiteSpace(filters.Status));
        Add("observacoesGerais", "Pesquisa geral", !string.IsNullOrWhiteSpace(filters.PesquisaGeral));
        Add("setores", "Setores", filters.SetorIds?.Count > 0);
        Add("clientes", "Clientes", !string.IsNullOrWhiteSpace(filters.ClienteId));
        return counts;
    }

    private static (List<JsonElement> Data, int Total) ParseListResult(object result)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var total = document.RootElement.TryGetProperty("total", out var totalElement) && totalElement.TryGetInt32(out var parsed)
            ? parsed
            : 0;
        var data = document.RootElement.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array
            ? dataElement.EnumerateArray().Select(item => item.Clone()).ToList()
            : new List<JsonElement>();
        return (data, total);
    }

    private static string NormalizeIaScope(string? scope)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "all", "demandas", "setores", "clientes", "templates", "usuarios", "paginas", "observacoes_gerais", "status",
        };
        return !string.IsNullOrWhiteSpace(scope) && allowed.Contains(scope) ? scope.ToLowerInvariant() : "all";
    }

    private static string ParsePesquisaGeralMode(string query, string scope)
    {
        if (scope == "observacoes_gerais")
        {
            return "observacoes_gerais_only";
        }

        if (scope == "status")
        {
            return "status_only";
        }

        var normalized = NormalizeIaText(query);
        if (normalized.Contains("apenas observacao geral", StringComparison.Ordinal) ||
            normalized.Contains("somente observacao geral", StringComparison.Ordinal))
        {
            return "observacoes_gerais_only";
        }
        if (normalized.Contains("somente status", StringComparison.Ordinal) ||
            normalized.Contains("apenas status", StringComparison.Ordinal))
        {
            return "status_only";
        }

        return "all";
    }

    private static string PesquisaGeralModeLabel(string mode) =>
        mode switch
        {
            "observacoes_gerais_only" => "Somente observações gerais",
            "status_only" => "Somente status",
            _ => "Busca geral",
        };

    private static string GlobalModuleLabel(string module) =>
        module switch
        {
            "setores" => "Setores",
            "clientes" => "Clientes",
            "templates" => "Templates",
            "usuarios" => "Usuários",
            "paginas" => "Páginas",
            "demandas" => "Demandas",
            _ => module,
        };

    private static string BuildDemandasLink(ListDemandasFiltersQuery filters)
    {
        var parameters = new List<string>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parameters.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
            }
        }

        Add("clienteId", filters.ClienteId);
        Add("assunto", filters.Assunto);
        Add("status", filters.Status);
        if (filters.OcultarStandby.HasValue) Add("ocultarStandby", filters.OcultarStandby.Value ? "true" : "false");
        if (filters.OcultarConcluidas.HasValue) Add("ocultarConcluidas", filters.OcultarConcluidas.Value ? "true" : "false");
        Add("tipoRecorrencia", filters.TipoRecorrencia);
        Add("protocolo", filters.Protocolo);
        if (filters.Prioridade.HasValue) Add("prioridade", filters.Prioridade.Value ? "true" : "false");
        Add("criadorId", filters.CriadorId);
        Add("responsavelPrincipalId", filters.ResponsavelPrincipalId);
        if (filters.ResponsavelApenasPrincipal.HasValue) Add("responsavelApenasPrincipal", filters.ResponsavelApenasPrincipal.Value ? "true" : "false");
        if (filters.SetorIds?.Count > 0)
        {
            parameters.AddRange(filters.SetorIds.Select(setorId => $"setorIds={Uri.EscapeDataString(setorId)}"));
        }
        Add("condicaoPrazo", filters.CondicaoPrazo);
        Add("pesquisarTarefaOuObservacao", filters.PesquisarTarefaOuObservacao);
        Add("pesquisaGeral", filters.PesquisaGeral);
        Add("dataCriacaoDe", filters.DataCriacaoDe);
        Add("dataCriacaoAte", filters.DataCriacaoAte);
        Add("prazoDe", filters.PrazoDe);
        Add("prazoAte", filters.PrazoAte);

        return parameters.Count == 0 ? "/demandas" : $"/demandas?{string.Join("&", parameters)}";
    }

    private static List<(string Label, string Url)> InferLinksFromQuery(string query)
    {
        var normalized = NormalizeIaText(query);
        var links = new List<(string Label, string Url)>();
        void Add(string label, string url)
        {
            if (!links.Any(item => item.Url == url))
            {
                links.Add((label, url));
            }
        }

        if (normalized.Contains("dashboard", StringComparison.Ordinal) || normalized.Contains("kpi", StringComparison.Ordinal))
        {
            Add("Abrir Dashboard KPIs", "/dashboard");
        }
        if (normalized.Contains("template", StringComparison.Ordinal) || normalized.Contains("modelo", StringComparison.Ordinal))
        {
            Add("Abrir Templates", "/templates");
        }
        if (normalized.Contains("cadastro", StringComparison.Ordinal) ||
            normalized.Contains("cliente", StringComparison.Ordinal) ||
            normalized.Contains("setor", StringComparison.Ordinal) ||
            normalized.Contains("responsavel", StringComparison.Ordinal))
        {
            Add("Abrir Cadastros", "/cadastros");
        }
        if (normalized.Contains("nova demanda", StringComparison.Ordinal) || normalized.Contains("criar demanda", StringComparison.Ordinal))
        {
            Add("Criar Nova Demanda", "/demandas/nova");
        }
        Add("Abrir Demandas", "/demandas");
        return links;
    }

    private static ListDemandasFiltersQuery ExtractHeuristicIaFilters(string query, IaReferenceData referenceData)
    {
        var normalized = NormalizeIaText(query);
        var filters = new ListDemandasFiltersQuery();

        if (normalized.Contains("standby", StringComparison.Ordinal) || normalized.Contains("stand by", StringComparison.Ordinal))
            filters.Status = "standby";
        else if (normalized.Contains("cancelad", StringComparison.Ordinal))
            filters.Status = "cancelado";
        else if (normalized.Contains("em andamento", StringComparison.Ordinal) || normalized.Contains("andamento", StringComparison.Ordinal))
            filters.Status = "em_andamento";
        else if (normalized.Contains("em aberto", StringComparison.Ordinal) || normalized.Contains("aberta", StringComparison.Ordinal) || normalized.Contains("aberto", StringComparison.Ordinal))
            filters.Status = "em_aberto";
        else if (normalized.Contains("concluid", StringComparison.Ordinal))
            filters.Status = "concluido";

        if (normalized.Contains("sem prioridade", StringComparison.Ordinal) || normalized.Contains("sem urgencia", StringComparison.Ordinal))
            filters.Prioridade = false;
        else if (normalized.Contains("prioridade", StringComparison.Ordinal) || normalized.Contains("urgente", StringComparison.Ordinal))
            filters.Prioridade = true;

        if (normalized.Contains("vencid", StringComparison.Ordinal))
            filters.CondicaoPrazo = "vencido";
        else if (normalized.Contains("no prazo", StringComparison.Ordinal) || normalized.Contains("dentro do prazo", StringComparison.Ordinal))
            filters.CondicaoPrazo = "no_prazo";
        else if (normalized.Contains("finalizada", StringComparison.Ordinal))
            filters.CondicaoPrazo = "finalizada";

        if (normalized.Contains("diaria", StringComparison.Ordinal) || normalized.Contains("diario", StringComparison.Ordinal))
            filters.TipoRecorrencia = "diaria";
        else if (normalized.Contains("quinzenal", StringComparison.Ordinal))
            filters.TipoRecorrencia = "quinzenal";
        else if (normalized.Contains("semanal", StringComparison.Ordinal))
            filters.TipoRecorrencia = "semanal";
        else if (normalized.Contains("mensal", StringComparison.Ordinal))
            filters.TipoRecorrencia = "mensal";

        var protocoloMatch = Regex.Match(query, @"\b([A-Za-z]{2,}-\d{4}-\d{3,})\b", RegexOptions.IgnoreCase);
        if (protocoloMatch.Success)
        {
            filters.Protocolo = protocoloMatch.Groups[1].Value.ToUpperInvariant();
        }

        filters.SetorIds = referenceData.Setores
            .Where(item => ContainsNormalizedEntity(normalized, item.Name))
            .Select(item => item.Id)
            .Distinct()
            .ToList();
        if (filters.SetorIds.Count == 0)
        {
            filters.SetorIds = null;
        }

        if (normalized.Contains("cliente", StringComparison.Ordinal))
        {
            filters.ClienteId = referenceData.Clientes.FirstOrDefault(item => ContainsNormalizedEntity(normalized, item.Name))?.Id;
        }
        if (normalized.Contains("criador", StringComparison.Ordinal) || normalized.Contains("criado por", StringComparison.Ordinal))
        {
            filters.CriadorId = referenceData.Users.FirstOrDefault(item => ContainsNormalizedEntity(normalized, item.Name))?.Id;
        }
        if (normalized.Contains("responsavel", StringComparison.Ordinal))
        {
            filters.ResponsavelPrincipalId = referenceData.Users.FirstOrDefault(item => ContainsNormalizedEntity(normalized, item.Name))?.Id;
        }

        filters.PesquisaGeral = NormalizePesquisaGeralFromQuery(query, null);
        return filters;
    }

    private static bool ContainsNormalizedEntity(string normalizedQuery, string name)
    {
        var normalizedName = NormalizeIaText(name);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return false;
        }
        if (normalizedName.Length <= 2)
        {
            return Regex.IsMatch(normalizedQuery, $@"\b{Regex.Escape(normalizedName)}\b");
        }
        return normalizedQuery.Contains(normalizedName, StringComparison.Ordinal);
    }

    private static string NormalizePesquisaGeralFromQuery(string query, string? current)
    {
        var rawQuery = query.Trim();
        var quoted = Regex.Match(rawQuery, "\"([^\"]{2,})\"");
        if (quoted.Success)
        {
            return quoted.Groups[1].Value.Trim();
        }

        var dateTokens = ExtractDateTokens(rawQuery);
        if (dateTokens.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(current))
            {
                return dateTokens[0];
            }

            var currentTrimmed = current.Trim();
            var currentNormalized = NormalizeIaText(currentTrimmed);
            var hasDateInCurrent = dateTokens.Any(date => currentNormalized.Contains(NormalizeIaText(date), StringComparison.Ordinal));
            var hasManyTokens = TokenizePesquisaGeral(currentTrimmed).Count >= 3;
            if (hasDateInCurrent && hasManyTokens)
            {
                return dateTokens[0];
            }
        }

        if (!string.IsNullOrWhiteSpace(current))
        {
            var currentTrimmed = current.Trim();
            if (!IsGenericPesquisaGeralPhrase(currentTrimmed))
            {
                return currentTrimmed;
            }
        }

        var normalizedQuery = NormalizeIaText(rawQuery);
        var mentionsFullTextFields =
            normalizedQuery.Contains("observacao", StringComparison.Ordinal) ||
            normalizedQuery.Contains("obs", StringComparison.Ordinal) ||
            normalizedQuery.Contains("dados basicos", StringComparison.Ordinal) ||
            normalizedQuery.Contains("campos basicos", StringComparison.Ordinal) ||
            normalizedQuery.Contains("recorrente", StringComparison.Ordinal) ||
            normalizedQuery.Contains("data base", StringComparison.Ordinal);

        if (mentionsFullTextFields)
        {
            if (dateTokens.Count > 0)
            {
                return dateTokens[0];
            }

            return rawQuery;
        }

        var tokens = TokenizePesquisaGeral(rawQuery);
        if (tokens.Count == 1)
        {
            return tokens[0];
        }

        return tokens.Count > 1 ? string.Join(' ', tokens.Take(4)) : string.Empty;
    }

    private static bool QueryMentions(string query, params string[] fragments)
    {
        var normalized = NormalizeIaText(query);
        return fragments.Any(fragment => normalized.Contains(NormalizeIaText(fragment), StringComparison.Ordinal));
    }

    private static bool QueryMentionsPrioridade(string query) =>
        QueryMentions(query, "prioridade", "urgencia", "urgência", "urgente", "sem prioridade", "sem urgencia", "sem urgência");

    private static bool QueryMentionsStatus(string query) =>
        QueryMentions(query, "status", "standby", "stand by", "cancelad", "andamento", "em aberto", "aberto", "aberta", "concluid");

    private static List<string> TokenizePesquisaGeral(string value)
    {
        var stopwords = new HashSet<string>(StringComparer.Ordinal)
        {
            "a", "o", "as", "os", "um", "uma", "de", "do", "da", "dos", "das",
            "no", "na", "nos", "nas", "em", "por", "para", "com", "sem",
            "que", "quais", "qual", "onde", "como", "tem", "tenho", "quero",
            "me", "minha", "meu", "minhas", "meus", "sobre", "sistema",
            "campo", "campos", "dados", "basicos", "basico", "procure", "buscar",
            "demanda", "demandas", "apenas", "somente", "so", "status",
            "observacao", "observacoes", "obs", "geral", "gerais", "usuario", "user",
            "qualquer", "todo", "todos", "todas",
            "aberto", "aberta", "andamento", "concluido", "concluida", "cancelado", "cancelada", "standby",
            "quanta", "quantas", "quanto", "quantos", "quantidade", "numero", "número", "total", "qtd",
            "tem", "temos", "ha", "há", "existem", "existe", "cadastrada", "cadastradas", "cadastrado", "cadastrados",
            "hoje", "agora", "atualmente"
        };

        return Regex.Split(NormalizeIaText(value), "[^a-z0-9]+")
            .Select(token => token.Trim())
            .Where(token => token.Length >= 2 && !stopwords.Contains(token))
            .ToList();
    }

    private static bool IsGenericPesquisaGeralPhrase(string value)
    {
        var normalized = NormalizeIaText(value);
        if (TokenizePesquisaGeral(value).Count == 0)
        {
            return true;
        }
        return string.IsNullOrWhiteSpace(normalized) ||
               normalized is "demanda" or "demandas" or "dados basicos" or "dados basico" or
               "campos basicos" or "campo basico" or "observacao" or "observacoes" or
               "observacoes gerais" or "obs";
    }

    private static List<string> ExtractDateTokens(string query) =>
        Regex.Matches(query, @"\b\d{2}/\d{2}/\d{4}\b|\b\d{4}-\d{2}-\d{2}\b")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string? NormalizeStatus(string? status)
    {
        var normalized = NormalizeIaText(status ?? string.Empty);
        return normalized switch
        {
            "standby" or "stand by" => "standby",
            "cancelado" or "cancelada" => "cancelado",
            "em andamento" or "andamento" => "em_andamento",
            "em aberto" or "aberto" or "aberta" => "em_aberto",
            "concluido" or "concluida" or "concluído" or "concluída" => "concluido",
            _ => null,
        };
    }

    private static string? NormalizeRecorrencia(string? value)
    {
        var normalized = NormalizeIaText(value ?? string.Empty);
        return normalized switch
        {
            "diaria" or "diario" => "diaria",
            "semanal" => "semanal",
            "quinzenal" => "quinzenal",
            "mensal" => "mensal",
            _ => null,
        };
    }

    private static string? NormalizeCondicaoPrazo(string? value)
    {
        var normalized = NormalizeIaText(value ?? string.Empty);
        return normalized switch
        {
            "vencido" or "vencida" => "vencido",
            "no prazo" => "no_prazo",
            "finalizada" or "finalizado" => "finalizada",
            _ => null,
        };
    }

    private static string NormalizeIaText(string value)
    {
        var normalized = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(ch);
            }
        }
        return builder.ToString().Trim();
    }

    private static string? MatchString(Dictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var value) ? value?.ToString()?.Trim() : null;

    private static bool? MatchBool(Dictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            bool flag => flag,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => null,
        };
    }

    private static string? MatchValidId(Dictionary<string, object?> values, string key, HashSet<string> allowed)
    {
        var candidate = MatchString(values, key);
        return !string.IsNullOrWhiteSpace(candidate) && allowed.Contains(candidate) ? candidate : null;
    }

    private static List<string>? MatchValidIds(Dictionary<string, object?> values, string key, HashSet<string> allowed)
    {
        if (!values.TryGetValue(key, out var value) || value is not IEnumerable<string> ids)
        {
            return null;
        }

        var filtered = ids.Where(allowed.Contains).Distinct().ToList();
        return filtered.Count > 0 ? filtered : null;
    }

    private static string? MatchDate(Dictionary<string, object?> values, string key)
    {
        var candidate = MatchString(values, key);
        return candidate is not null && Regex.IsMatch(candidate, @"^\d{4}-\d{2}-\d{2}$") ? candidate : null;
    }

    private async Task<bool> IsResponsavelPrincipalAsync(string userId, string demandaId, CancellationToken cancellationToken)
    {
        var rows = await _supabase.QueryRowsAsync(
            $"demanda_responsavel?select=id&demanda_id=eq.{Uri.EscapeDataString(demandaId)}&user_id=eq.{Uri.EscapeDataString(userId)}&is_principal=eq.true&limit=1",
            cancellationToken);
        return rows.Length > 0;
    }

    private string GetAnexosBucket() =>
        string.IsNullOrWhiteSpace(_options.SupabaseStorageBucket) ? "demandas-anexos" : _options.SupabaseStorageBucket.Trim();

    private string BuildSupabaseStoragePath(string bucket, string objectPath) =>
        $"supabase://{bucket}/{objectPath}";

    private async Task<string> EnsureAnexosBucketAsync(CancellationToken cancellationToken)
    {
        var bucket = GetAnexosBucket();
        if (_anexosBucketReady)
        {
            return bucket;
        }

        var buckets = await _supabase.ListBucketsAsync(cancellationToken);
        var exists = buckets.Any(item => string.Equals(item.GetStringOrEmpty("name"), bucket, StringComparison.Ordinal));
        if (!exists)
        {
            await _supabase.CreateBucketAsync(bucket, isPublic: false, cancellationToken);
        }

        _anexosBucketReady = true;
        return bucket;
    }

    private static (string Mode, string? Bucket, string ObjectPath) ParseAnexoStoragePath(string? storagePath)
    {
        var raw = (storagePath ?? string.Empty).Trim();
        if (raw.StartsWith("supabase://", StringComparison.OrdinalIgnoreCase))
        {
            var withoutProtocol = raw["supabase://".Length..];
            var slashIndex = withoutProtocol.IndexOf('/');
            if (slashIndex > 0)
            {
                return ("supabase", withoutProtocol[..slashIndex], withoutProtocol[(slashIndex + 1)..]);
            }
        }

        return ("local", null, raw);
    }

    private static string SanitizeFilename(string filename) =>
        Regex.Replace(filename, @"[^a-zA-Z0-9._-]", "_");

    private static string GuessMimeType(string? filename)
    {
        var extension = Path.GetExtension(filename ?? string.Empty).ToLowerInvariant();
        return extension switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };
    }

    private static string StatusLabelPt(string status) =>
        status switch
        {
            "em_aberto" => "em aberto",
            "em_andamento" => "em andamento",
            "concluido" => "concluído",
            "standby" => "standby",
            "cancelado" => "cancelado",
            _ => status,
        };

    private async Task UpsertRecorrenciaAsync(string demandaId, RecorrenciaInput recorrencia, CancellationToken cancellationToken)
    {
        var existing = await _supabase.QuerySingleAsync(
            $"recorrencia_config?select=id&demanda_id=eq.{Uri.EscapeDataString(demandaId)}&limit=1",
            cancellationToken);
        var payload = new
        {
            demanda_id = demandaId,
            data_base = recorrencia.DataBase,
            tipo = recorrencia.Tipo,
            prazo_reabertura_dias = recorrencia.PrazoReaberturaDias ?? 0,
        };

        if (existing is null)
        {
            await _supabase.InsertSingleAsync("recorrencia_config", payload, cancellationToken);
            return;
        }

        await _supabase.UpdateSingleAsync(
            "recorrencia_config",
            $"demanda_id=eq.{Uri.EscapeDataString(demandaId)}",
            new
            {
                data_base = recorrencia.DataBase,
                tipo = recorrencia.Tipo,
                prazo_reabertura_dias = recorrencia.PrazoReaberturaDias ?? 0,
            },
            cancellationToken);
    }

    private async Task ReplaceDemandaRelationsAsync(
        string demandaId,
        IReadOnlyList<string>? setores,
        IReadOnlyList<string>? clienteIds,
        IReadOnlyList<DemandaResponsavelInput>? responsaveis,
        IReadOnlyList<DemandaSubtarefaUpdateInput>? subtarefas,
        CancellationToken cancellationToken)
    {
        if (setores is not null)
        {
            await _supabase.DeleteAsync("demanda_setor", $"demanda_id=eq.{Uri.EscapeDataString(demandaId)}", cancellationToken);
            if (setores.Count > 0)
            {
                await _supabase.InsertManyAsync("demanda_setor",
                    setores.Select(setorId => new { demanda_id = demandaId, setor_id = setorId }),
                    cancellationToken);
            }
        }

        if (clienteIds is not null)
        {
            await _supabase.DeleteAsync("demanda_cliente", $"demanda_id=eq.{Uri.EscapeDataString(demandaId)}", cancellationToken);
            if (clienteIds.Count > 0)
            {
                await _supabase.InsertManyAsync("demanda_cliente",
                    clienteIds.Select(clienteId => new { demanda_id = demandaId, cliente_id = clienteId }),
                    cancellationToken);
            }
        }

        if (responsaveis is not null)
        {
            await _supabase.DeleteAsync("demanda_responsavel", $"demanda_id=eq.{Uri.EscapeDataString(demandaId)}", cancellationToken);
            if (responsaveis.Count > 0)
            {
                await _supabase.InsertManyAsync("demanda_responsavel",
                    responsaveis.Select(item => new
                    {
                        demanda_id = demandaId,
                        user_id = item.UserId,
                        is_principal = item.IsPrincipal ?? false,
                    }),
                    cancellationToken);
            }
        }

        if (subtarefas is not null)
        {
            await _supabase.DeleteAsync("subtarefa", $"demanda_id=eq.{Uri.EscapeDataString(demandaId)}", cancellationToken);
            if (subtarefas.Count > 0)
            {
                await _supabase.InsertManyAsync("subtarefa",
                    subtarefas.Select((item, index) => new
                    {
                        demanda_id = demandaId,
                        titulo = item.Titulo,
                        concluida = item.Concluida ?? false,
                        ordem = item.Ordem ?? index,
                        responsavel_user_id = item.ResponsavelUserId,
                    }),
                    cancellationToken);
            }
        }
    }

    private async Task ReplacePrivateViewersAsync(
        string demandaId,
        IReadOnlyList<string>? viewerIds,
        CancellationToken cancellationToken)
    {
        if (viewerIds is null)
        {
            return;
        }

        try
        {
            await _supabase.DeleteAsync("demanda_private_viewer", $"demanda_id=eq.{Uri.EscapeDataString(demandaId)}", cancellationToken);
            var uniqueIds = viewerIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (uniqueIds.Length > 0)
            {
                await _supabase.InsertManyAsync(
                    "demanda_private_viewer",
                    uniqueIds.Select(userId => new { demanda_id = demandaId, user_id = userId }),
                    cancellationToken);
            }
        }
        catch (InvalidOperationException error) when (IsMissingPrivateViewerTable(error))
        {
        }
    }

    private static bool IsMissingPrivateViewerTable(Exception error)
    {
        var message = error.Message;
        return message.Contains("demanda_private_viewer", StringComparison.OrdinalIgnoreCase)
            || message.Contains("PGRST205", StringComparison.OrdinalIgnoreCase)
            || message.Contains("42P01", StringComparison.OrdinalIgnoreCase);
    }

    private object MapDemandaListFromRpc(JsonElement row)
    {
        var criador = row.GetOptionalProperty("criador");
        return MapDemandaList(
            row,
            criador,
            MapResponsaveis(row.GetArrayOrEmpty("responsaveis")),
            MapSetores(row.GetArrayOrEmpty("setores")),
            MapClientes(row.GetArrayOrEmpty("clientes")));
    }

    private object MapDemandaDetailFromRpc(JsonElement row, IReadOnlyList<object>? historico = null)
    {
        var detail = MapDemandaListFromRpc(row);
        var recurring = row.GetOptionalProperty("recorrencia_config");

        return new
        {
            id = row.GetStringOrEmpty("id"),
            protocolo = row.GetStringOrEmpty("protocolo"),
            assunto = row.GetStringOrEmpty("assunto"),
            prioridade = row.GetBooleanOrDefault("prioridade"),
            prazo = NormalizeDate(row.GetNullableString("prazo")),
            status = row.GetStringOrEmpty("status"),
            criadorId = row.GetNullableString("criador_id"),
            observacoesGerais = row.GetNullableString("observacoes_gerais"),
            isRecorrente = row.GetBooleanOrDefault("is_recorrente"),
            isPrivada = row.GetBooleanOrDefault("is_privada"),
            demandaOrigemId = row.GetNullableString("demanda_origem_id"),
            createdAt = NormalizeDate(row.GetNullableString("created_at")),
            updatedAt = NormalizeDate(row.GetNullableString("updated_at")),
            resolvidoEm = NormalizeDate(row.GetNullableString("resolvido_em")),
            ultimaObservacaoEm = NormalizeDate(row.GetNullableString("ultima_observacao_em")),
            tempoResolucaoHoras = ComputeTempoHoras(ParseDate(row.GetNullableString("created_at")), ParseDate(row.GetNullableString("resolvido_em"))),
            tempoDesdeUltimaObservacaoHoras = ComputeTempoHoras(ParseDate(row.GetNullableString("ultima_observacao_em")), DateTime.UtcNow),
            criador = row.GetOptionalProperty("criador") is JsonElement criadorValue && criadorValue.ValueKind == JsonValueKind.Object
                ? new
                {
                    id = criadorValue.GetStringOrEmpty("id"),
                    name = criadorValue.GetStringOrEmpty("name"),
                    email = criadorValue.GetStringOrEmpty("email"),
                }
                : null,
            responsaveis = MapResponsaveis(row.GetArrayOrEmpty("responsaveis")),
            setores = MapSetores(row.GetArrayOrEmpty("setores")),
            clientes = MapClientes(row.GetArrayOrEmpty("clientes")),
            subtarefas = MapSubtarefas(row.GetArrayOrEmpty("subtarefas")),
            observacoes = MapObservacoes(row.GetArrayOrEmpty("observacoes")),
            anexos = row.GetArrayOrEmpty("anexos").Select(item => (object)item.Clone()).ToList(),
            historico = historico ?? Array.Empty<object>(),
            recorrenciaConfig = recurring is JsonElement recurringValue && recurringValue.ValueKind == JsonValueKind.Object
                ? new
                {
                    dataBase = NormalizeDate(recurringValue.GetNullableString("data_base")) ?? recurringValue.GetNullableString("data_base"),
                    tipo = recurringValue.GetStringOrEmpty("tipo"),
                    prazoReaberturaDias = recurringValue.GetNullableInt32("prazo_reabertura_dias") ?? 0,
                }
                : null,
        };
    }

    private async Task<object> BuildDetailFromDirectRowAsync(JsonElement row, bool includeDetail, CancellationToken cancellationToken)
    {
        var demandaId = row.GetStringOrEmpty("id");
        var criadorTask = LoadCriadorAsync(row.GetNullableString("criador_id"), cancellationToken);
        var responsaveisTask = LoadResponsaveisAsync(demandaId, cancellationToken);
        var setoresTask = LoadSetoresAsync(demandaId, cancellationToken);
        var clientesTask = LoadClientesAsync(demandaId, cancellationToken);
        await Task.WhenAll(criadorTask, responsaveisTask, setoresTask, clientesTask);

        if (!includeDetail)
        {
            return MapDemandaList(row, criadorTask.Result, responsaveisTask.Result, setoresTask.Result, clientesTask.Result);
        }

        var subtarefasTask = LoadSubtarefasAsync(demandaId, cancellationToken);
        var observacoesTask = LoadObservacoesAsync(demandaId, cancellationToken);
        var anexosTask = LoadAnexosAsync(demandaId, cancellationToken);
        var recorrenciaTask = LoadRecorrenciaAsync(demandaId, cancellationToken);
        var historicoTask = _audit.LoadDemandaEventsAsync(demandaId, cancellationToken);
        var privateViewersTask = LoadPrivateViewersAsync(demandaId, cancellationToken);
        await Task.WhenAll(subtarefasTask, observacoesTask, anexosTask, recorrenciaTask, historicoTask, privateViewersTask);

        var recurring = recorrenciaTask.Result;

        return new
        {
            id = row.GetStringOrEmpty("id"),
            protocolo = row.GetStringOrEmpty("protocolo"),
            assunto = row.GetStringOrEmpty("assunto"),
            prioridade = row.GetBooleanOrDefault("prioridade"),
            prazo = NormalizeDate(row.GetNullableString("prazo")) ?? row.GetNullableString("prazo"),
            status = row.GetStringOrEmpty("status"),
            criadorId = row.GetNullableString("criador_id"),
            observacoesGerais = row.GetNullableString("observacoes_gerais"),
            isRecorrente = row.GetBooleanOrDefault("is_recorrente"),
            isPrivada = row.GetBooleanOrDefault("is_privada"),
            demandaOrigemId = row.GetNullableString("demanda_origem_id"),
            createdAt = NormalizeDate(row.GetNullableString("created_at")),
            updatedAt = NormalizeDate(row.GetNullableString("updated_at")),
            resolvidoEm = NormalizeDate(row.GetNullableString("resolvido_em")),
            ultimaObservacaoEm = NormalizeDate(row.GetNullableString("ultima_observacao_em")),
            tempoResolucaoHoras = ComputeTempoHoras(ParseDate(row.GetNullableString("created_at")), ParseDate(row.GetNullableString("resolvido_em"))),
            tempoDesdeUltimaObservacaoHoras = ComputeTempoHoras(ParseDate(row.GetNullableString("ultima_observacao_em")), DateTime.UtcNow),
            criador = criadorTask.Result is JsonElement criadorValue && criadorValue.ValueKind == JsonValueKind.Object
                ? new
                {
                    id = criadorValue.GetStringOrEmpty("id"),
                    name = criadorValue.GetStringOrEmpty("name"),
                    email = criadorValue.GetStringOrEmpty("email"),
                }
                : null,
            responsaveis = responsaveisTask.Result,
            setores = setoresTask.Result,
            clientes = clientesTask.Result,
            subtarefas = subtarefasTask.Result,
            observacoes = observacoesTask.Result,
            anexos = anexosTask.Result,
            historico = historicoTask.Result,
            privateViewers = privateViewersTask.Result,
            recorrenciaConfig = recurring is JsonElement recurringValue && recurringValue.ValueKind == JsonValueKind.Object
                ? new
                {
                    dataBase = NormalizeDate(recurringValue.GetNullableString("data_base")) ?? recurringValue.GetNullableString("data_base"),
                    tipo = recurringValue.GetStringOrEmpty("tipo"),
                    prazoReaberturaDias = recurringValue.GetNullableInt32("prazo_reabertura_dias") ?? 0,
                }
                : null,
        };
    }

    private static object MapDemandaList(
        JsonElement row,
        JsonElement? criador,
        IReadOnlyList<object> responsaveis,
        IReadOnlyList<object> setores,
        IReadOnlyList<object> clientes)
    {
        var createdAt = NormalizeDate(row.GetNullableString("created_at"));
        var resolvidoEm = NormalizeDate(row.GetNullableString("resolvido_em"));
        var ultimaObservacaoEm = NormalizeDate(row.GetNullableString("ultima_observacao_em"));
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
            protocolo = row.GetStringOrEmpty("protocolo"),
            assunto = row.GetStringOrEmpty("assunto"),
            prioridade = row.GetBooleanOrDefault("prioridade"),
            prazo = NormalizeDate(row.GetNullableString("prazo")) ?? row.GetNullableString("prazo"),
            status = row.GetStringOrEmpty("status"),
            criadorId = row.GetNullableString("criador_id"),
            observacoesGerais = row.GetNullableString("observacoes_gerais"),
            isRecorrente = row.GetBooleanOrDefault("is_recorrente"),
            isPrivada = row.GetBooleanOrDefault("is_privada"),
            demandaOrigemId = row.GetNullableString("demanda_origem_id"),
            createdAt,
            updatedAt = NormalizeDate(row.GetNullableString("updated_at")) ?? row.GetNullableString("updated_at"),
            resolvidoEm,
            ultimaObservacaoEm,
            tempoResolucaoHoras = ComputeTempoHoras(ParseDate(createdAt), ParseDate(resolvidoEm)),
            tempoDesdeUltimaObservacaoHoras = ComputeTempoHoras(ParseDate(ultimaObservacaoEm), DateTime.UtcNow),
            criador = criadorObject,
            responsaveis,
            setores,
            clientes,
        };
    }

    private static List<object> MapResponsaveis(IReadOnlyList<JsonElement> rows) =>
        rows.Select(row =>
        {
            var user = row.GetOptionalProperty("user");
            return (object)new
            {
                userId = row.GetStringOrEmpty("userId"),
                isPrincipal = row.GetBooleanOrDefault("isPrincipal"),
                user = user is JsonElement userValue && userValue.ValueKind == JsonValueKind.Object
                    ? new
                    {
                        id = userValue.GetStringOrEmpty("id"),
                        name = userValue.GetStringOrEmpty("name"),
                        email = userValue.GetStringOrEmpty("email"),
                    }
                    : null,
            };
        }).ToList();

    private static List<object> MapSetores(IReadOnlyList<JsonElement> rows) =>
        rows.Select(row =>
        {
            var setor = row.GetOptionalProperty("setor");
            return (object)new
            {
                setor = setor is JsonElement setorValue && setorValue.ValueKind == JsonValueKind.Object
                    ? new
                    {
                        id = setorValue.GetStringOrEmpty("id"),
                        name = setorValue.GetStringOrEmpty("name"),
                        slug = setorValue.GetStringOrEmpty("slug"),
                    }
                    : null,
            };
        }).ToList();

    private static List<object> MapClientes(IReadOnlyList<JsonElement> rows) =>
        rows.Select(row =>
        {
            var cliente = row.GetOptionalProperty("cliente");
            return (object)new
            {
                cliente = cliente is JsonElement clienteValue && clienteValue.ValueKind == JsonValueKind.Object
                    ? new
                    {
                        id = clienteValue.GetStringOrEmpty("id"),
                        name = clienteValue.GetStringOrEmpty("name"),
                        active = clienteValue.GetBooleanOrDefault("active", true),
                    }
                    : null,
            };
        }).ToList();

    private static List<object> MapSubtarefas(IReadOnlyList<JsonElement> rows) =>
        rows.Select(row =>
        {
            var responsavel = row.GetOptionalProperty("responsavel");
            return new
            {
                ordem = row.GetNullableInt32("ordem") ?? 0,
                payload = (object)new
                {
                    id = row.GetNullableString("id"),
                    titulo = row.GetStringOrEmpty("titulo"),
                    concluida = row.GetBooleanOrDefault("concluida"),
                    ordem = row.GetNullableInt32("ordem") ?? 0,
                    responsavelUserId = row.GetNullableString("responsavelUserId") ?? row.GetNullableString("responsavel_user_id"),
                    responsavel = responsavel is JsonElement responsavelValue && responsavelValue.ValueKind == JsonValueKind.Object
                        ? new
                        {
                            id = responsavelValue.GetStringOrEmpty("id"),
                            name = responsavelValue.GetStringOrEmpty("name"),
                            email = responsavelValue.GetStringOrEmpty("email"),
                        }
                        : null,
                },
            };
        })
        .OrderBy(item => item.ordem)
        .Select(item => item.payload)
        .ToList();

    private static List<object> MapObservacoes(IReadOnlyList<JsonElement> rows) =>
        rows.Select(row =>
        {
            var user = row.GetOptionalProperty("user");
            return (object)new
            {
                id = row.GetNullableString("id"),
                texto = row.GetStringOrEmpty("texto"),
                createdAt = NormalizeDate(row.GetNullableString("createdAt") ?? row.GetNullableString("created_at")),
                user = user is JsonElement userValue && userValue.ValueKind == JsonValueKind.Object
                    ? new
                    {
                        id = userValue.GetStringOrEmpty("id"),
                        name = userValue.GetStringOrEmpty("name"),
                    }
                    : null,
            };
        }).ToList();

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

    private async Task<IReadOnlyList<object>> LoadResponsaveisAsync(string demandaId, CancellationToken cancellationToken)
    {
        var rows = await _supabase.QueryRowsAsync(
            $"demanda_responsavel?select=user_id,is_principal&demanda_id=eq.{Uri.EscapeDataString(demandaId)}",
            cancellationToken);
        var userIds = rows.Select(row => row.GetStringOrEmpty("user_id")).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        Dictionary<string, JsonElement> usersById = [];
        if (userIds.Length > 0)
        {
            var users = await _supabase.QueryRowsAsync(
                $"User?select=id,name,email&id=in.({string.Join(",", userIds.Select(Uri.EscapeDataString))})",
                cancellationToken);
            usersById = users.ToDictionary(user => user.GetStringOrEmpty("id"));
        }

        return rows.Select(row =>
        {
            var userId = row.GetStringOrEmpty("user_id");
            usersById.TryGetValue(userId, out var user);
            return (object)new
            {
                userId,
                isPrincipal = row.GetBooleanOrDefault("is_principal"),
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

    private async Task<IReadOnlyList<object>> LoadPrivateViewersAsync(string demandaId, CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _supabase.QueryRowsAsync(
                $"demanda_private_viewer?select=user_id&demanda_id=eq.{Uri.EscapeDataString(demandaId)}",
                cancellationToken);
            var userIds = rows
                .Select(row => row.GetStringOrEmpty("user_id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (userIds.Length == 0)
            {
                return Array.Empty<object>();
            }

            var users = await _supabase.QueryRowsAsync(
                $"User?select=id,name,email&id=in.({string.Join(",", userIds.Select(Uri.EscapeDataString))})",
                cancellationToken);
            var usersById = users.ToDictionary(user => user.GetStringOrEmpty("id"));
            return userIds.Select(userId =>
            {
                usersById.TryGetValue(userId, out var user);
                return (object)new
                {
                    user = user.ValueKind == JsonValueKind.Object
                        ? new
                        {
                            id = user.GetStringOrEmpty("id"),
                            name = user.GetStringOrEmpty("name"),
                            email = user.GetStringOrEmpty("email"),
                        }
                        : new
                        {
                            id = userId,
                            name = string.Empty,
                            email = string.Empty,
                        },
                };
            }).ToList();
        }
        catch (InvalidOperationException error) when (IsMissingPrivateViewerTable(error))
        {
            return Array.Empty<object>();
        }
    }

    private async Task<IReadOnlyList<object>> LoadSetoresAsync(string demandaId, CancellationToken cancellationToken)
    {
        var links = await _supabase.QueryRowsAsync(
            $"demanda_setor?select=setor_id&demanda_id=eq.{Uri.EscapeDataString(demandaId)}",
            cancellationToken);
        var setorIds = links.Select(link => link.GetStringOrEmpty("setor_id")).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        if (setorIds.Length == 0)
        {
            return Array.Empty<object>();
        }

        var setores = await _supabase.QueryRowsAsync(
            $"Setor?select=id,name,slug&id=in.({string.Join(",", setorIds.Select(Uri.EscapeDataString))})",
            cancellationToken);
        return setores.Select(setor => (object)new
        {
            setor = new
            {
                id = setor.GetStringOrEmpty("id"),
                name = setor.GetStringOrEmpty("name"),
                slug = setor.GetStringOrEmpty("slug"),
            },
        }).ToList();
    }

    private async Task<IReadOnlyList<object>> LoadClientesAsync(string demandaId, CancellationToken cancellationToken)
    {
        var links = await _supabase.QueryRowsAsync(
            $"demanda_cliente?select=cliente_id&demanda_id=eq.{Uri.EscapeDataString(demandaId)}",
            cancellationToken);
        var clienteIds = links.Select(link => link.GetStringOrEmpty("cliente_id")).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        if (clienteIds.Length == 0)
        {
            return Array.Empty<object>();
        }

        var clientes = await _supabase.QueryRowsAsync(
            $"Cliente?select=id,name,active&id=in.({string.Join(",", clienteIds.Select(Uri.EscapeDataString))})",
            cancellationToken);
        return clientes.Select(cliente => (object)new
        {
            cliente = new
            {
                id = cliente.GetStringOrEmpty("id"),
                name = cliente.GetStringOrEmpty("name"),
                active = cliente.GetBooleanOrDefault("active", true),
            },
        }).ToList();
    }

    private async Task<IReadOnlyList<object>> LoadSubtarefasAsync(string demandaId, CancellationToken cancellationToken)
    {
        var rows = await _supabase.QueryRowsAsync(
            $"subtarefa?select=id,titulo,concluida,ordem,responsavel_user_id&demanda_id=eq.{Uri.EscapeDataString(demandaId)}&order=ordem.asc&id=not.is.null",
            cancellationToken);
        var userIds = rows.Select(row => row.GetNullableString("responsavel_user_id")).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        Dictionary<string, JsonElement> usersById = [];
        if (userIds.Length > 0)
        {
            var users = await _supabase.QueryRowsAsync(
                $"User?select=id,name,email&id=in.({string.Join(",", userIds.Select(id => Uri.EscapeDataString(id!)))})",
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
                    concluida = row.GetBooleanOrDefault("concluida"),
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
        }).OrderBy(item => item.ordem)
          .Select(item => item.payload)
          .ToList();
    }

    private async Task<IReadOnlyList<object>> LoadObservacoesAsync(string demandaId, CancellationToken cancellationToken)
    {
        var rows = await _supabase.QueryRowsAsync(
            $"observacao?select=id,user_id,texto,created_at&demanda_id=eq.{Uri.EscapeDataString(demandaId)}&order=created_at.desc",
            cancellationToken);
        var userIds = rows.Select(row => row.GetStringOrEmpty("user_id")).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        Dictionary<string, JsonElement> usersById = [];
        if (userIds.Length > 0)
        {
            var users = await _supabase.QueryRowsAsync(
                $"User?select=id,name&id=in.({string.Join(",", userIds.Select(Uri.EscapeDataString))})",
                cancellationToken);
            usersById = users.ToDictionary(user => user.GetStringOrEmpty("id"));
        }

        return rows.Select(row =>
        {
            var userId = row.GetStringOrEmpty("user_id");
            usersById.TryGetValue(userId, out var user);
            return (object)new
            {
                id = row.GetNullableString("id"),
                texto = row.GetStringOrEmpty("texto"),
                createdAt = NormalizeDate(row.GetNullableString("created_at")),
                user = user.ValueKind == JsonValueKind.Object
                    ? new
                    {
                        id = user.GetStringOrEmpty("id"),
                        name = user.GetStringOrEmpty("name"),
                    }
                    : null,
            };
        }).ToList();
    }

    private async Task<IReadOnlyList<object>> LoadAnexosAsync(string demandaId, CancellationToken cancellationToken)
    {
        var rows = await _supabase.QueryRowsAsync(
            $"anexo?select=*&demanda_id=eq.{Uri.EscapeDataString(demandaId)}",
            cancellationToken);
        return rows.Select(row => (object)row.Clone()).ToList();
    }

    private async Task EnsureLegacyAnexosLinkedAsync(JsonElement demanda, string demandaId, CancellationToken cancellationToken)
    {
        if (!_legacyAttachments.IsConfigured)
        {
            return;
        }

        var existing = await _supabase.QueryRowsAsync(
            $"anexo?select=id&demanda_id=eq.{Uri.EscapeDataString(demandaId)}&limit=1",
            cancellationToken);
        if (existing.Length > 0)
        {
            return;
        }

        var legacyDemandaId = await ResolveLegacyDemandaIdAsync(demanda, demandaId, cancellationToken);
        if (string.IsNullOrWhiteSpace(legacyDemandaId))
        {
            return;
        }

        IReadOnlyList<LegacyAttachmentMetadata> legacyAnexos;
        try
        {
            legacyAnexos = await _legacyAttachments.ListAsync(legacyDemandaId, cancellationToken);
        }
        catch
        {
            return;
        }

        foreach (var item in legacyAnexos)
        {
            if (string.IsNullOrWhiteSpace(item.StoragePath))
            {
                continue;
            }

            var duplicate = await _supabase.QueryRowsAsync(
                $"anexo?select=id&storage_path=eq.{Uri.EscapeDataString(item.StoragePath)}&limit=1",
                cancellationToken);
            if (duplicate.Length > 0)
            {
                continue;
            }

            await _supabase.InsertSingleAsync("anexo", new
            {
                demanda_id = demandaId,
                filename = string.IsNullOrWhiteSpace(item.Filename) ? "Anexo legado" : item.Filename,
                mime_type = GuessMimeType(item.Filename),
                size = 0,
                storage_path = item.StoragePath,
            }, cancellationToken);
        }
    }

    private async Task<string?> ResolveLegacyDemandaIdAsync(JsonElement demanda, string demandaId, CancellationToken cancellationToken)
    {
        var legacyId = demanda.GetNullableString("legacy_id");
        if (!string.IsNullOrWhiteSpace(legacyId))
        {
            return legacyId;
        }

        var rows = await _supabase.QueryRowsAsync(
            $"anexo?select=storage_path&demanda_id=eq.{Uri.EscapeDataString(demandaId)}&storage_path=like.legacy*&limit=1",
            cancellationToken);
        var storagePath = rows.FirstOrDefault().GetNullableString("storage_path");
        return LegacyAttachmentService.TryParseStoragePath(storagePath, out var reference)
            ? reference.DemandaId
            : null;
    }

    private async Task<JsonElement?> LoadRecorrenciaAsync(string demandaId, CancellationToken cancellationToken) =>
        await _supabase.QuerySingleAsync(
            $"recorrencia_config?select=*&demanda_id=eq.{Uri.EscapeDataString(demandaId)}&limit=1",
            cancellationToken);

    private static object MapDashboardMetricas(JsonElement row)
    {
        var porStatus = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (row.GetOptionalProperty("por_status") is JsonElement porStatusValue && porStatusValue.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in porStatusValue.EnumerateObject())
            {
                porStatus[property.Name] = property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value)
                    ? value
                    : int.TryParse(property.Value.ToString(), out var parsed)
                        ? parsed
                        : 0;
            }
        }

        return new
        {
            totalDemandas = row.GetNullableInt32("total_demandas") ?? 0,
            concluidas = row.GetNullableInt32("concluidas") ?? 0,
            emAberto = row.GetNullableInt32("em_aberto") ?? 0,
            tempoMedioResolucaoHoras = ParseNullableDouble(row.GetNullableString("tempo_medio_resolucao_horas")),
            demandasSemObservacaoRecente = row.GetNullableInt32("demandas_sem_observacao_recente") ?? 0,
            tempoMedioDesdeUltimaObservacaoHoras = ParseNullableDouble(row.GetNullableString("tempo_medio_desde_ultima_observacao_horas")),
            porStatus,
        };
    }

    private async Task RegistrarCriacaoDemandaAsync(
        string userId,
        string demandaId,
        int totalSetores,
        int totalClientes,
        int totalResponsaveis,
        int totalSubtarefas,
        bool recorrente,
        string? templateName,
        CancellationToken cancellationToken)
    {
        var descricao = string.IsNullOrWhiteSpace(templateName)
            ? "Demanda criada."
            : $"Demanda criada a partir do template \"{templateName}\".";

        var detalhes = BuildResumoInicialDemanda(totalSetores, totalClientes, totalResponsaveis, totalSubtarefas, recorrente);
        if (detalhes.Count > 0)
        {
            descricao += $" Configuracao inicial: {string.Join(", ", detalhes)}.";
        }

        await _audit.AddDemandaEventAsync(
            demandaId,
            userId,
            string.IsNullOrWhiteSpace(templateName) ? "demanda_criada" : "demanda_criada_template",
            descricao,
            new
            {
                template = templateName,
                setores = totalSetores,
                clientes = totalClientes,
                responsaveis = totalResponsaveis,
                subtarefas = totalSubtarefas,
                recorrente,
            },
            cancellationToken);
    }

    private async Task RegistrarAlteracoesDemandaAsync(
        string userId,
        string demandaId,
        UpdateDemandaRequest request,
        string? newStatus,
        CancellationToken cancellationToken)
    {
        var camposBasicos = new List<string>();
        if (request.Assunto is not null) camposBasicos.Add("assunto");
        if (request.Prioridade.HasValue) camposBasicos.Add("prioridade");
        if (request.Prazo is not null) camposBasicos.Add("prazo");
        if (!string.IsNullOrWhiteSpace(newStatus)) camposBasicos.Add("status");
        if (request.ObservacoesGerais is not null) camposBasicos.Add("observacoes gerais");
        if (request.IsRecorrente.HasValue && request.Recorrencia is null) camposBasicos.Add("indicador de recorrencia");

        if (camposBasicos.Count > 0)
        {
            await _audit.AddDemandaEventAsync(
                demandaId,
                userId,
                "demanda_atualizada",
                $"Campos basicos atualizados: {JoinLabels(camposBasicos)}.",
                new
                {
                    campos = camposBasicos,
                    status = newStatus,
                },
                cancellationToken);
        }

        if (request.Setores is not null)
        {
            await _audit.AddDemandaEventAsync(
                demandaId,
                userId,
                "demanda_setores_atualizados",
                $"Setores atualizados ({CountLabel(request.Setores.Count, "setor", "setores")}).",
                new { total = request.Setores.Count },
                cancellationToken);
        }

        if (request.ClienteIds is not null)
        {
            await _audit.AddDemandaEventAsync(
                demandaId,
                userId,
                "demanda_clientes_atualizados",
                $"Clientes atualizados ({CountLabel(request.ClienteIds.Count, "cliente", "clientes")}).",
                new { total = request.ClienteIds.Count },
                cancellationToken);
        }

        if (request.Responsaveis is not null)
        {
            var principals = request.Responsaveis.Count(item => item.IsPrincipal == true);
            await _audit.AddDemandaEventAsync(
                demandaId,
                userId,
                "demanda_responsaveis_atualizados",
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
            var concluidas = request.Subtarefas.Count(item => item.Concluida == true);
            await _audit.AddDemandaEventAsync(
                demandaId,
                userId,
                "demanda_subtarefas_atualizadas",
                $"Subtarefas atualizadas ({CountLabel(request.Subtarefas.Count, "item", "itens")}, {CountLabel(concluidas, "concluida", "concluidas")}).",
                new
                {
                    total = request.Subtarefas.Count,
                    concluidas,
                },
                cancellationToken);
        }

        if (request.Recorrencia is not null)
        {
            await _audit.AddDemandaEventAsync(
                demandaId,
                userId,
                "demanda_recorrencia_configurada",
                $"Recorrencia configurada: {FormatRecorrenciaDescription(request.Recorrencia)}.",
                new
                {
                    tipo = request.Recorrencia.Tipo,
                    dataBase = request.Recorrencia.DataBase,
                    prazoReaberturaDias = request.Recorrencia.PrazoReaberturaDias,
                },
                cancellationToken);
        }
        else if (request.IsRecorrente == false)
        {
            await _audit.AddDemandaEventAsync(
                demandaId,
                userId,
                "demanda_recorrencia_removida",
                "Recorrencia removida.",
                null,
                cancellationToken);
        }
    }

    private static List<string> BuildResumoInicialDemanda(
        int totalSetores,
        int totalClientes,
        int totalResponsaveis,
        int totalSubtarefas,
        bool recorrente)
    {
        var detalhes = new List<string>();
        if (totalSetores > 0) detalhes.Add(CountLabel(totalSetores, "setor", "setores"));
        if (totalClientes > 0) detalhes.Add(CountLabel(totalClientes, "cliente", "clientes"));
        if (totalResponsaveis > 0) detalhes.Add(CountLabel(totalResponsaveis, "responsavel", "responsaveis"));
        if (totalSubtarefas > 0) detalhes.Add(CountLabel(totalSubtarefas, "subtarefa", "subtarefas"));
        if (recorrente) detalhes.Add("recorrencia");
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

    private async Task<HashSet<string>> BuildAdminPesquisaGeralIdsAsync(string search, CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var ilike = BuildIlikeValue(search);

        var matchingUsers = await LoadIdsAsync(
            $"User?select=id&or=(name.ilike.{ilike},email.ilike.{ilike})&limit=100000",
            "id",
            cancellationToken);
        if (matchingUsers.Count > 0)
        {
            ids.UnionWith(await LoadIdsAsync(
                $"Demanda?select=id&criador_id=in.({string.Join(",", matchingUsers.Select(Uri.EscapeDataString))})&limit=100000",
                "id",
                cancellationToken));
            ids.UnionWith(await LoadIdsAsync(
                $"demanda_responsavel?select=demanda_id&user_id=in.({string.Join(",", matchingUsers.Select(Uri.EscapeDataString))})&limit=100000",
                "demanda_id",
                cancellationToken));
        }

        var matchingSetores = await LoadIdsAsync(
            $"Setor?select=id&or=(name.ilike.{ilike},slug.ilike.{ilike})&limit=100000",
            "id",
            cancellationToken);
        if (matchingSetores.Count > 0)
        {
            ids.UnionWith(await LoadIdsAsync(
                $"demanda_setor?select=demanda_id&setor_id=in.({string.Join(",", matchingSetores.Select(Uri.EscapeDataString))})&limit=100000",
                "demanda_id",
                cancellationToken));
        }

        var matchingClientes = await LoadIdsAsync(
            $"Cliente?select=id&name=ilike.{ilike}&limit=100000",
            "id",
            cancellationToken);
        if (matchingClientes.Count > 0)
        {
            ids.UnionWith(await LoadIdsAsync(
                $"demanda_cliente?select=demanda_id&cliente_id=in.({string.Join(",", matchingClientes.Select(Uri.EscapeDataString))})&limit=100000",
                "demanda_id",
                cancellationToken));
        }

        ids.UnionWith(await LoadIdsAsync(
            $"subtarefa?select=demanda_id&titulo=ilike.{ilike}&limit=100000",
            "demanda_id",
            cancellationToken));
        ids.UnionWith(await LoadIdsAsync(
            $"observacao?select=demanda_id&texto=ilike.{ilike}&limit=100000",
            "demanda_id",
            cancellationToken));

        var recurringRows = await _supabase.QueryAllRowsAsync(
            "recorrencia_config?select=demanda_id,tipo,data_base,prazo_reabertura_dias",
            cancellationToken);
        foreach (var row in recurringRows)
        {
            var recurringText = string.Join(" ", new[]
            {
                row.GetNullableString("tipo"),
                row.GetNullableString("data_base"),
                row.GetNullableString("prazo_reabertura_dias"),
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

            if (ContainsIgnoreCase(recurringText, search))
            {
                var demandaId = row.GetStringOrEmpty("demanda_id");
                if (!string.IsNullOrWhiteSpace(demandaId))
                {
                    ids.Add(demandaId);
                }
            }
        }

        return ids;
    }

    private async Task<HashSet<string>> LoadIdsAsync(string query, string fieldName, CancellationToken cancellationToken)
    {
        var rows = await _supabase.QueryAllRowsAsync(query, cancellationToken);
        return rows
            .Select(row => row.GetStringOrEmpty(fieldName))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> UnionIds(HashSet<string> first, HashSet<string> second)
    {
        var result = new HashSet<string>(first, StringComparer.Ordinal);
        result.UnionWith(second);
        return result;
    }

    private static HashSet<string> IntersectIds(HashSet<string>? current, HashSet<string> next)
    {
        if (current is null)
        {
            return new HashSet<string>(next, StringComparer.Ordinal);
        }

        current.IntersectWith(next);
        return current;
    }

    private async Task<List<string>> ApplyAnexosFilterAsync(
        IReadOnlyList<string> ids,
        string? filtro,
        CancellationToken cancellationToken)
    {
        if (!IsAnexoFilter(filtro))
        {
            return ids.ToList();
        }

        var idsComAnexo = await LoadIdsAsync("anexo?select=demanda_id&limit=100000", "demanda_id", cancellationToken);
        return ids
            .Where(id => MatchesAnexosFilter(id, filtro, idsComAnexo))
            .ToList();
    }

    private static bool IsAnexoFilter(string? filtro) =>
        string.Equals(filtro, "com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(filtro, "sem", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesAnexosFilter(string demandaId, string? filtro, HashSet<string>? idsComAnexo)
    {
        if (!IsAnexoFilter(filtro))
        {
            return true;
        }

        var hasAnexo = idsComAnexo?.Contains(demandaId) ?? false;
        return string.Equals(filtro, "com", StringComparison.OrdinalIgnoreCase) ? hasAnexo : !hasAnexo;
    }

    private static bool MatchesAdminBaseFilters(JsonElement row, ListDemandasFiltersQuery filters)
    {
        if (!string.IsNullOrWhiteSpace(filters.Assunto)
            && !ContainsIgnoreCase(row.GetStringOrEmpty("assunto"), filters.Assunto))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filters.Status)
            && !string.Equals(row.GetStringOrEmpty("status"), filters.Status, StringComparison.Ordinal))
        {
            return false;
        }

        if (filters.OcultarStandby == true
            && !string.Equals(filters.Status, "standby", StringComparison.Ordinal)
            && string.Equals(row.GetStringOrEmpty("status"), "standby", StringComparison.Ordinal))
        {
            return false;
        }

        if (filters.OcultarConcluidas == true
            && string.IsNullOrWhiteSpace(filters.Status)
            && (string.Equals(row.GetStringOrEmpty("status"), "concluido", StringComparison.Ordinal)
                || string.Equals(row.GetStringOrEmpty("status"), "cancelado", StringComparison.Ordinal)))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filters.Protocolo)
            && !ContainsIgnoreCase(row.GetStringOrEmpty("protocolo"), filters.Protocolo))
        {
            return false;
        }

        if (filters.Prioridade.HasValue && row.GetBooleanOrDefault("prioridade") != filters.Prioridade.Value)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filters.CriadorId)
            && !string.Equals(row.GetNullableString("criador_id"), filters.CriadorId, StringComparison.Ordinal))
        {
            return false;
        }

        var createdAt = ParseDate(row.GetNullableString("created_at"));
        var prazo = ParseDate(row.GetNullableString("prazo"));
        var status = row.GetStringOrEmpty("status");

        var dataCriacaoDe = ParseDate(filters.DataCriacaoDe);
        if (dataCriacaoDe.HasValue && (!createdAt.HasValue || createdAt.Value.Date < dataCriacaoDe.Value.Date))
        {
            return false;
        }

        var dataCriacaoAte = ParseDate(filters.DataCriacaoAte);
        if (dataCriacaoAte.HasValue && (!createdAt.HasValue || createdAt.Value.Date > dataCriacaoAte.Value.Date))
        {
            return false;
        }

        var prazoDe = ParseDate(filters.PrazoDe);
        if (prazoDe.HasValue && (!prazo.HasValue || prazo.Value.Date < prazoDe.Value.Date))
        {
            return false;
        }

        var prazoAte = ParseDate(filters.PrazoAte);
        if (prazoAte.HasValue && (!prazo.HasValue || prazo.Value.Date > prazoAte.Value.Date))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filters.CondicaoPrazo))
        {
            var today = DateTime.UtcNow.Date;
            var condicao = filters.CondicaoPrazo.Trim();
            if (string.Equals(condicao, "finalizada", StringComparison.Ordinal) && !string.Equals(status, "concluido", StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(condicao, "vencido", StringComparison.Ordinal)
                && (!prazo.HasValue || prazo.Value.Date >= today))
            {
                return false;
            }

            if (string.Equals(condicao, "no_prazo", StringComparison.Ordinal)
                && (!prazo.HasValue || prazo.Value.Date < today))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesAdminPesquisaGeralBase(JsonElement row, string search)
    {
        var aggregate = string.Join(" ", new[]
        {
            row.GetStringOrEmpty("protocolo"),
            row.GetStringOrEmpty("assunto"),
            row.GetStringOrEmpty("status"),
            row.GetBooleanOrDefault("prioridade") ? "prioridade urgente sim" : "prioridade nao",
            row.GetNullableString("observacoes_gerais"),
            row.GetNullableString("prazo"),
            row.GetNullableString("created_at"),
            row.GetNullableString("resolvido_em"),
            row.GetNullableString("ultima_observacao_em"),
            row.GetBooleanOrDefault("is_recorrente") ? "recorrente" : "nao recorrente",
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        return ContainsIgnoreCase(aggregate, search);
    }

    private static bool ContainsIgnoreCase(string? source, string? value) =>
        !string.IsNullOrWhiteSpace(source)
        && !string.IsNullOrWhiteSpace(value)
        && source.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static string BuildIlikeValue(string raw) =>
        Uri.EscapeDataString($"*{raw.Trim()}*");

    private static bool IsUuid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Guid.TryParse(value, out _);

    private static List<string>? NormalizeUuidList(IEnumerable<string>? values)
    {
        var list = values?
            .Where(IsUuid)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return list is { Count: > 0 } ? list : null;
    }

    private static List<DemandaResponsavelInput>? NormalizeResponsaveis(IEnumerable<DemandaResponsavelInput>? values)
    {
        var list = values?
            .Where(item => IsUuid(item.UserId))
            .GroupBy(item => item.UserId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        return list is { Count: > 0 } ? list : null;
    }

    private static List<DemandaSubtarefaCreateInput>? NormalizeCreateSubtarefas(IEnumerable<DemandaSubtarefaCreateInput>? values)
    {
        var list = values?
            .Where(item => !string.IsNullOrWhiteSpace(item.Titulo))
            .Select(item => new DemandaSubtarefaCreateInput
            {
                Titulo = item.Titulo.Trim(),
                Ordem = item.Ordem,
                ResponsavelUserId = IsUuid(item.ResponsavelUserId) ? item.ResponsavelUserId : null,
            })
            .ToList();
        return list is { Count: > 0 } ? list : null;
    }

    private static string FormatRecorrenciaDescription(RecorrenciaInput recorrencia)
    {
        var detalhes = new List<string>();
        if (!string.IsNullOrWhiteSpace(recorrencia.Tipo))
        {
            detalhes.Add($"tipo {recorrencia.Tipo}");
        }

        if (!string.IsNullOrWhiteSpace(recorrencia.DataBase))
        {
            detalhes.Add($"data base {FormatDateForDescription(recorrencia.DataBase)}");
        }

        if (recorrencia.PrazoReaberturaDias.HasValue)
        {
            detalhes.Add($"reabertura em {recorrencia.PrazoReaberturaDias.Value} dia(s)");
        }

        return detalhes.Count > 0 ? string.Join(", ", detalhes) : "configurada";
    }

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

    private static string? NormalizeDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParse(value, out var parsed) ? parsed.ToUniversalTime().ToString("O") : value;
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, out var parsed) ? parsed : null;

    private static double? ComputeTempoHoras(DateTime? from, DateTime? to)
    {
        if (!from.HasValue || !to.HasValue)
        {
            return null;
        }

        return Math.Round((to.Value - from.Value).TotalHours, 1);
    }

    private static double? ParseNullableDouble(string? value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private sealed record NamedEntity(string Id, string Name);

    private sealed record IaReferenceData(
        IReadOnlyList<NamedEntity> Setores,
        IReadOnlyList<NamedEntity> Clientes,
        IReadOnlyList<NamedEntity> Users);

    private sealed record IaFilterExtractionResult(
        Dictionary<string, object?> Filters,
        bool UsedAi,
        string Mode,
        string? ResponseText,
        string? Engine);

    private sealed record GlobalIaMatch(
        string Module,
        string ModuleLabel,
        string Title,
        string Snippet,
        string Route,
        string Searchable);

    private sealed record GlobalIaEvidence(
        IReadOnlyList<GlobalIaMatch> Matches,
        IReadOnlyList<object> ModuleCounts,
        IReadOnlyList<object> GlobalMatchesPayload);
}
