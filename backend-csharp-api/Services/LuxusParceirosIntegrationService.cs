using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LuxusDemandas.Api.Configuration;
using LuxusDemandas.Api.Models;
using LuxusDemandas.Api.Support;
using Microsoft.Extensions.Options;

namespace LuxusDemandas.Api.Services;

public sealed class LuxusParceirosIntegrationService
{
    private const string DefaultLuxusParceirosCallbackUrl =
        "https://luxusparceiros-production-df5d.up.railway.app/api/integrations/luxus-task/callback";
    private const string ParceirosOriginMarker = "Origem: Luxus Parceiros";
    private const string TechnicalUserDisplayName = "Luxus Parceiros";
    private static readonly JsonSerializerOptions CallbackJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly Regex NomeLinhaPlaceholderRegex = new(
        @"\(\s*nome\s+e\s+n[uú]mero\s+da\s+linha\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly SupabaseRestService _supabase;
    private readonly DemandasService _demandas;
    private readonly ClientesService _clientes;
    private readonly TemplatesService _templates;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppOptions _options;

    public LuxusParceirosIntegrationService(
        SupabaseRestService supabase,
        DemandasService demandas,
        ClientesService clientes,
        TemplatesService templates,
        IHttpClientFactory httpClientFactory,
        IOptions<AppOptions> options)
    {
        _supabase = supabase;
        _demandas = demandas;
        _clientes = clientes;
        _templates = templates;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public bool IsAuthorized(string? received)
    {
        var expected = _options.LuxusParceirosIntegrationKey?.Trim();
        var supplied = received?.Trim();
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(supplied))
        {
            return false;
        }
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length
               && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    public Task<IReadOnlyList<UserDropdownDto>> ListResponsaveisAsync(CancellationToken cancellationToken) =>
        _supabase.ListUsersForDropdownAsync(cancellationToken);

    public async Task<IReadOnlyList<LuxusParceirosClientDto>> ListClientesAsync(
        string? search,
        CancellationToken cancellationToken)
    {
        var normalized = search?.Trim();
        var documentSearch = string.IsNullOrWhiteSpace(normalized)
            ? null
            : Regex.Replace(normalized, "[^0-9]", string.Empty);
        var clientes = await _supabase.ListClientesAsync(true, cancellationToken);
        return clientes
            .Where(cliente =>
                string.IsNullOrWhiteSpace(normalized)
                || cliente.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(cliente.NomeFantasia)
                    && cliente.NomeFantasia.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(cliente.Documento)
                    && (cliente.Documento.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrWhiteSpace(documentSearch)
                            && cliente.Documento.Contains(documentSearch, StringComparison.OrdinalIgnoreCase)))))
            .Take(50)
            .Select(cliente => new LuxusParceirosClientDto(
                cliente.Id,
                cliente.Name,
                cliente.Documento,
                cliente.NomeFantasia,
                cliente.TipoPessoa))
            .ToList();
    }

    public async Task<object> CreateAsync(
        CreateLuxusParceirosDemandaRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.RequestId, out _)
            || !Guid.TryParse(request.ResponsibleId, out _))
        {
            throw new InvalidOperationException("Solicitação ou responsável inválido.");
        }
        if (!DateOnly.TryParseExact(request.Deadline, "yyyy-MM-dd", out var deadline)
            || deadline < DateOnly.FromDateTime(DateTime.UtcNow))
        {
            throw new InvalidOperationException("Informe um prazo válido, igual ou posterior à data atual.");
        }

        var existing = await FindMappingByExternalIdAsync(request.RequestId, cancellationToken);
        if (existing.HasValue)
        {
            return await BuildResponseAsync(existing.Value, cancellationToken);
        }

        var responsible = await _supabase.FindUserByIdAsync(request.ResponsibleId, cancellationToken);
        if (responsible is null || !responsible.Active)
        {
            throw new KeyNotFoundException("Responsável não encontrado ou inativo no Luxus Task.");
        }
        ClienteDto? client;
        if (!string.IsNullOrWhiteSpace(request.ClientId))
        {
            if (!Guid.TryParse(request.ClientId, out _))
            {
                throw new InvalidOperationException("Cliente inválido.");
            }

            client = (await _supabase.ListClientesAsync(true, cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, request.ClientId, StringComparison.OrdinalIgnoreCase));
            if (client is null)
            {
                throw new KeyNotFoundException("Cliente não encontrado ou inativo no Luxus Task.");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.ClientName)
                || string.IsNullOrWhiteSpace(request.ClientDocumentType)
                || string.IsNullOrWhiteSpace(request.ClientDocument))
            {
                throw new InvalidOperationException(
                    "Selecione um cliente ou informe nome, tipo e documento para o cadastro.");
            }

            client = await _clientes.FindOrCreateForIntegrationAsync(
                request.ClientName,
                request.ClientDocumentType,
                request.ClientDocument,
                cancellationToken);
        }

        var technicalUserId = await EnsureTechnicalUserAsync(cancellationToken);
        var saleDescription = !string.IsNullOrWhiteSpace(request.Observations)
            ? request.Observations
            : request.Description;
        var saleBlock = BuildParceirosOriginBlock(
            request.LocalProtocol,
            request.PartnerName,
            request.BranchName,
            request.RequesterName,
            request.RequesterEmail,
            saleDescription);
        var isSale = string.Equals(request.EntityType, "sale", StringComparison.OrdinalIgnoreCase);
        var saleTemplate = isSale
            ? await TryLoadParceirosSaleTemplateAsync(cancellationToken)
            : null;

        object created;
        if (saleTemplate is not null)
        {
            created = await _demandas.CreateFromTemplateAsync(
                technicalUserId,
                saleTemplate.Id,
                new CreateDemandaFromTemplateRequest
                {
                    Assunto = BuildSaleAssuntoFromTemplate(
                        saleTemplate,
                        request.PartnerName,
                        request.LocalProtocol,
                        request.Subject),
                    Prazo = deadline.ToString("yyyy-MM-dd"),
                    Prioridade = request.Priority ?? saleTemplate.PrioridadeDefault,
                    // Instruções = só o texto nativo do template (dados da venda vão em observação).
                    ObservacoesGerais = saleTemplate.ObservacoesGeraisTemplate ?? string.Empty,
                    ClienteIds = [client.Id],
                    Responsaveis = MergeSaleResponsaveis(saleTemplate, request.ResponsibleId),
                },
                cancellationToken);
            Console.WriteLine(
                $"[luxus-parceiros] Venda {request.LocalProtocol} criada a partir do template '{saleTemplate.Name}'.");
        }
        else
        {
            created = await _demandas.CreateAsync(
                technicalUserId,
                new CreateDemandaRequest
                {
                    Assunto = request.Subject.Trim(),
                    Prioridade = request.Priority ?? false,
                    Prazo = deadline.ToString("yyyy-MM-dd"),
                    Status = "em_aberto",
                    ObservacoesGerais = request.Instructions?.Trim() ?? string.Empty,
                    ClienteIds = [client.Id],
                    Responsaveis =
                    [
                        new DemandaResponsavelInput
                        {
                            UserId = request.ResponsibleId,
                            IsPrincipal = true,
                        },
                    ],
                },
                cancellationToken);
        }

        using var createdJson = JsonDocument.Parse(JsonSerializer.Serialize(created));
        var demandaId = ReadString(createdJson.RootElement, "id");
        var protocol = ReadString(createdJson.RootElement, "protocolo");
        if (string.IsNullOrWhiteSpace(demandaId))
        {
            throw new InvalidOperationException("O Luxus Task criou a demanda sem retornar seu identificador.");
        }

        if (!string.IsNullOrWhiteSpace(saleBlock))
        {
            try
            {
                await _demandas.AddObservacaoAsync(technicalUserId, demandaId, saleBlock, cancellationToken);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    $"[luxus-parceiros] Falha ao gravar observação inicial da demanda {demandaId}: {error.Message}");
            }
        }

        var mapping = await _supabase.InsertSingleAsync(
            "luxus_parceiros_demanda",
            new
            {
                demanda_id = demandaId,
                external_request_id = request.RequestId,
                external_protocol = request.LocalProtocol,
                entity_type = string.Equals(request.EntityType, "sale", StringComparison.OrdinalIgnoreCase) ? "sale" : "request",
            },
            cancellationToken);

        var sourceAttachmentIds = new List<string>();
        var usedFilenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var documentsToImport = request.Documents?.ToList() ?? [];
        if (documentsToImport.Count == 0
            && string.Equals(request.EntityType, "sale", StringComparison.OrdinalIgnoreCase))
        {
            documentsToImport = await ListPartnerDocumentsAsync(request.RequestId, cancellationToken);
        }

        foreach (var document in documentsToImport)
        {
            try
            {
                var buffer = await ResolvePartnerDocumentBufferAsync(
                    request.RequestId,
                    document,
                    cancellationToken);
                var uniqueFilename = BuildUniqueAttachmentFilename(document.Name, document.Type, document.Id, usedFilenames);
                var imported = await _demandas.AddAnexoForIntegrationAsync(
                    technicalUserId,
                    demandaId,
                    buffer,
                    uniqueFilename,
                    document.Name,
                    string.IsNullOrWhiteSpace(document.MimeType)
                        ? "application/octet-stream"
                        : document.MimeType,
                    buffer.LongLength,
                    cancellationToken);
                using var importedJson = JsonDocument.Parse(JsonSerializer.Serialize(imported));
                var importedId = ReadString(importedJson.RootElement, "id");
                if (!string.IsNullOrWhiteSpace(importedId)) sourceAttachmentIds.Add(importedId);
                usedFilenames.Add(uniqueFilename);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    $"[luxus-parceiros] Falha ao importar documento {document.Id}/{document.Name}: {error.Message}");
            }
        }

        if (documentsToImport.Count > 0 && sourceAttachmentIds.Count == 0)
        {
            // Segunda tentativa buscando a lista no Parceiros (caso o payload venha sem files).
            var listed = await ListPartnerDocumentsAsync(request.RequestId, cancellationToken);
            foreach (var document in listed)
            {
                var shortId = document.Id.Replace("-", "");
                if (shortId.Length > 8) shortId = shortId[..8];
                var typedName = $"{document.Type}-{document.Name}";
                if (usedFilenames.Any(name =>
                        name.Contains(shortId, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, typedName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                try
                {
                    var buffer = await ResolvePartnerDocumentBufferAsync(
                        request.RequestId,
                        document,
                        cancellationToken);
                    var uniqueFilename = BuildUniqueAttachmentFilename(
                        document.Name,
                        document.Type,
                        document.Id,
                        usedFilenames);
                    var imported = await _demandas.AddAnexoForIntegrationAsync(
                        technicalUserId,
                        demandaId,
                        buffer,
                        uniqueFilename,
                        document.Name,
                        string.IsNullOrWhiteSpace(document.MimeType)
                            ? "application/octet-stream"
                            : document.MimeType,
                        buffer.LongLength,
                        cancellationToken);
                    using var importedJson = JsonDocument.Parse(JsonSerializer.Serialize(imported));
                    var importedId = ReadString(importedJson.RootElement, "id");
                    if (!string.IsNullOrWhiteSpace(importedId)) sourceAttachmentIds.Add(importedId);
                    usedFilenames.Add(uniqueFilename);
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(
                        $"[luxus-parceiros] Falha na 2ª tentativa do documento {document.Id}/{document.Name}: {error.Message}");
                }
            }
        }

        await _supabase.UpdateSingleAsync(
            "luxus_parceiros_demanda",
            $"id=eq.{Uri.EscapeDataString(mapping.GetStringOrEmpty("id"))}",
            new { source_attachment_ids = sourceAttachmentIds },
            cancellationToken);

        return new
        {
            id = demandaId,
            protocol,
            status = "em_aberto",
            responsible = new { id = responsible.Id, name = responsible.Name, email = responsible.Email },
            client = new { id = client.Id, name = client.Name, document = client.Documento },
            updatedAt = DateTimeOffset.UtcNow,
            mappingId = mapping.GetStringOrEmpty("id"),
        };
    }

    private string BuildPartnerDocumentUrl(string saleId, string documentId)
    {
        var configured = string.IsNullOrWhiteSpace(_options.LuxusParceirosCallbackUrl)
            ? DefaultLuxusParceirosCallbackUrl
            : _options.LuxusParceirosCallbackUrl;
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var callback)
            || (callback.Scheme != Uri.UriSchemeHttp && callback.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("URL de retorno do Luxus Parceiros inválida.");
        }
        var path = callback.AbsolutePath.TrimEnd('/');
        const string callbackSuffix = "/callback";
        if (path.EndsWith(callbackSuffix, StringComparison.OrdinalIgnoreCase))
            path = path[..^callbackSuffix.Length];
        var builder = new UriBuilder(callback)
        {
            Path = $"{path}/sales/{Uri.EscapeDataString(saleId)}/documents/{Uri.EscapeDataString(documentId)}",
            Query = string.Empty,
        };
        return builder.Uri.ToString();
    }

    public async Task<object> GetAsync(string externalRequestId, CancellationToken cancellationToken)
    {
        var mapping = await FindMappingByExternalIdAsync(externalRequestId, cancellationToken)
                      ?? throw new KeyNotFoundException("Demanda integrada não encontrada.");
        return await BuildResponseAsync(mapping, cancellationToken);
    }

    public async Task<int> RecoverMissingSaleAttachmentsAsync(CancellationToken cancellationToken)
    {
        var mappings = await _supabase.QueryRowsAsync(
            "luxus_parceiros_demanda?select=external_request_id,source_attachment_ids,entity_type&entity_type=eq.sale&limit=500",
            cancellationToken);
        var recovered = 0;

        foreach (var mapping in mappings)
        {
            if (mapping.GetArrayOrEmpty("source_attachment_ids").Count > 0)
            {
                continue;
            }

            var externalRequestId = mapping.GetStringOrEmpty("external_request_id");
            if (string.IsNullOrWhiteSpace(externalRequestId))
            {
                continue;
            }

            try
            {
                var listedDocuments = await ListPartnerDocumentsAsync(externalRequestId, cancellationToken);
                if (listedDocuments.Count == 0)
                {
                    continue;
                }

                await ImportPartnerDocumentsAsync(
                    externalRequestId,
                    new ImportLuxusParceirosDocumentsRequest { Documents = listedDocuments },
                    cancellationToken);
                recovered++;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    $"[luxus-parceiros] Recuperação em segundo plano falhou para {externalRequestId}: {error.Message}");
            }
        }

        return recovered;
    }

    public async Task<int> ApplyMissingParceirosSaleTemplatesAsync(CancellationToken cancellationToken)
    {
        await EnsureTechnicalUserAsync(cancellationToken);

        var template = await TryLoadParceirosSaleTemplateAsync(cancellationToken);
        if (template is null)
        {
            return 0;
        }

        var mappings = await _supabase.QueryRowsAsync(
            "luxus_parceiros_demanda?select=id,demanda_id,external_request_id,external_protocol,entity_type&entity_type=eq.sale&limit=500",
            cancellationToken);
        var applied = 0;
        foreach (var mapping in mappings)
        {
            try
            {
                if (await ApplySaleTemplateLayoutAsync(mapping, template, cancellationToken))
                {
                    applied++;
                }
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    $"[luxus-parceiros] Falha ao aplicar template na demanda {mapping.GetStringOrEmpty("demanda_id")}: {error.Message}");
            }
        }

        return applied;
    }

    public async Task<object> AddCommentAsync(
        string externalRequestId,
        AddLuxusParceirosCommentRequest request,
        CancellationToken cancellationToken)
    {
        var mapping = await FindMappingByExternalIdAsync(externalRequestId, cancellationToken)
                      ?? throw new KeyNotFoundException("Demanda integrada não encontrada.");
        var technicalUserId = await EnsureTechnicalUserAsync(cancellationToken);
        var text = $"Luxus Parceiros — {request.AuthorName.Trim()}: {request.Content.Trim()}";
        return await _demandas.AddObservacaoAsync(
            technicalUserId,
            mapping.GetStringOrEmpty("demanda_id"),
            text,
            cancellationToken);
    }

    public async Task<object> UpdateDemandDetailsAsync(
        string externalRequestId,
        UpdateLuxusParceirosDemandDetailsRequest request,
        CancellationToken cancellationToken)
    {
        var mapping = await FindMappingByExternalIdAsync(externalRequestId, cancellationToken)
                      ?? throw new KeyNotFoundException("Demanda integrada não encontrada.");
        var technicalUserId = await EnsureTechnicalUserAsync(cancellationToken);
        var demandaId = mapping.GetStringOrEmpty("demanda_id");
        var demand = await _demandas.FindOneAsync(technicalUserId, demandaId, cancellationToken);
        using var demandJson = JsonDocument.Parse(JsonSerializer.Serialize(demand));
        var currentAssunto = StripWorkflowPrefix(ReadString(demandJson.RootElement, "assunto"));
        var currentObs = ReadString(demandJson.RootElement, "observacoesGerais");
        if (string.IsNullOrWhiteSpace(currentObs))
        {
            currentObs = ReadString(demandJson.RootElement, "observacoes_gerais");
        }

        string? assunto = null;
        if (!string.IsNullOrWhiteSpace(request.Subject))
        {
            var incoming = StripWorkflowPrefix(request.Subject.Trim());
            if (HasNomeLinhaPlaceholder(currentAssunto))
            {
                // Só preenche o placeholder do template; nunca sobrescreve título já editado.
                assunto = ReplaceNomeLinhaPlaceholder(currentAssunto, incoming);
            }
            else if (!LooksLikeParceirosSaleTemplateAssunto(currentAssunto)
                     && !string.Equals(currentAssunto, incoming, StringComparison.Ordinal))
            {
                assunto = incoming;
            }
        }

        // Dados da venda/cliente NÃO entram em Instruções (observacoes_gerais).
        // Em sync posterior, só atualizamos prazo/assunto (placeholder); descrição vira observação se ainda não existir.
        string? observacoes = null;
        var shouldAddSaleObservation = false;
        string? saleObservationText = null;
        if (!string.IsNullOrWhiteSpace(request.Description) || !string.IsNullOrWhiteSpace(request.Observations))
        {
            var protocol = !string.IsNullOrWhiteSpace(request.LocalProtocol)
                ? request.LocalProtocol
                : mapping.GetStringOrEmpty("external_protocol");
            if (string.IsNullOrWhiteSpace(protocol))
            {
                protocol = mapping.GetStringOrEmpty("external_request_id");
            }
            var description = !string.IsNullOrWhiteSpace(request.Observations)
                ? request.Observations
                : request.Description;
            saleObservationText = BuildParceirosOriginBlock(
                protocol,
                request.PartnerName,
                request.BranchName,
                request.RequesterName,
                request.RequesterEmail,
                description);
            shouldAddSaleObservation = true;

            // Se Instruções ainda misturam o bloco Parceiros, limpa para ficar só o prefixo (template).
            if (!string.IsNullOrWhiteSpace(currentObs)
                && currentObs.IndexOf(ParceirosOriginMarker, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var idx = currentObs.IndexOf(ParceirosOriginMarker, StringComparison.OrdinalIgnoreCase);
                observacoes = currentObs[..idx].TrimEnd();
            }
        }

        JsonElement? prazoElement = null;
        if (!string.IsNullOrWhiteSpace(request.Deadline))
        {
            if (!DateOnly.TryParseExact(request.Deadline.Trim(), "yyyy-MM-dd", out var deadline)
                || deadline < DateOnly.FromDateTime(DateTime.Now))
            {
                throw new InvalidOperationException("O prazo não pode ser anterior à data de hoje.");
            }
            prazoElement = JsonSerializer.SerializeToElement(deadline.ToString("yyyy-MM-dd"));
        }

        if (assunto is null && observacoes is null && prazoElement is null && !shouldAddSaleObservation)
        {
            throw new InvalidOperationException("Informe assunto, instruções ou prazo para atualizar.");
        }

        object? updated = null;
        if (assunto is not null || observacoes is not null || prazoElement is not null)
        {
            updated = await _demandas.UpdateAsync(
                technicalUserId,
                demandaId,
                new UpdateDemandaRequest
                {
                    Assunto = assunto,
                    ObservacoesGerais = observacoes,
                    Prazo = prazoElement,
                },
                cancellationToken);
        }

        if (shouldAddSaleObservation && !string.IsNullOrWhiteSpace(saleObservationText))
        {
            await EnsureParceirosSaleObservationAsync(
                technicalUserId,
                demandaId,
                saleObservationText,
                cancellationToken);
        }

        return updated ?? await _demandas.FindOneAsync(technicalUserId, demandaId, cancellationToken);
    }

    public async Task<object> UpdateSaleStageAsync(
        string externalRequestId,
        UpdateLuxusParceirosSaleStageRequest request,
        CancellationToken cancellationToken)
    {
        var allowed = new[]
        {
            "AWAITING_PARTNER_SIGNATURE",
            "TASK_VALIDATING_SIGNED_CONTRACT",
            "TASK_PROCESSING",
            "BLANK_CONTRACT_READY_FOR_ADMIN",
            "SIGNED_CONTRACT_READY_FOR_ADMIN",
            "TASK_APPROVED_REVIEW_PENDING",
            "TASK_REJECTED_REVIEW_PENDING",
            "CHANGES_REQUESTED",
            "PRE_REVIEW",
            "COMPLETED",
        };
        if (!allowed.Contains(request.Stage, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Etapa de venda inválida para esta operação.");

        var mapping = await FindMappingByExternalIdAsync(externalRequestId, cancellationToken)
                      ?? throw new KeyNotFoundException("Demanda integrada não encontrada.");
        if (!string.Equals(mapping.GetNullableString("entity_type"), "sale", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A demanda informada não pertence ao fluxo de vendas.");
        var technicalUserId = await EnsureTechnicalUserAsync(cancellationToken);
        var demandaId = mapping.GetStringOrEmpty("demanda_id");
        var currentStage = mapping.GetNullableString("workflow_stage") ?? string.Empty;
        var sameStage = string.Equals(currentStage, request.Stage, StringComparison.OrdinalIgnoreCase);
        var requestOnly = !string.IsNullOrWhiteSpace(request.TurnRequestFrom) || request.ClearTurnRequest == true;

        if (string.Equals(currentStage, "COMPLETED", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(request.Stage, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Esta venda já foi concluída e não pode mais ter a vez alterada.");
        }

        if (requestOnly && sameStage && !string.Equals(request.Stage, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            var requestNote = request.ClearTurnRequest == true
                ? $"Pedido de vez recusado. {request.Note}".Trim()
                : $"Pedido de vez recebido de {request.TurnRequestFrom}. {request.TurnRequestReason} {request.Note}".Trim();
            await _demandas.AddObservacaoAsync(technicalUserId, demandaId, $"[ETAPA LUXUS PARCEIROS] {requestNote}", cancellationToken);
            if (request.ClearTurnRequest == true)
            {
                await _supabase.UpdateSingleAsync(
                    "luxus_parceiros_demanda",
                    $"id=eq.{Uri.EscapeDataString(mapping.GetStringOrEmpty("id"))}",
                    new
                    {
                        workflow_stage = currentStage,
                        turn_request_from = (string?)null,
                        turn_request_reason = (string?)null,
                        turn_request_at = (DateTimeOffset?)null,
                        updated_at = DateTimeOffset.UtcNow,
                    },
                    cancellationToken);
            }
            else
            {
                await _supabase.UpdateSingleAsync(
                    "luxus_parceiros_demanda",
                    $"id=eq.{Uri.EscapeDataString(mapping.GetStringOrEmpty("id"))}",
                    new
                    {
                        workflow_stage = currentStage,
                        turn_request_from = request.TurnRequestFrom,
                        turn_request_reason = request.TurnRequestReason,
                        turn_request_at = DateTimeOffset.UtcNow,
                        updated_at = DateTimeOffset.UtcNow,
                    },
                    cancellationToken);
            }
            var refreshedRequest = await FindMappingByExternalIdAsync(externalRequestId, cancellationToken)
                                   ?? throw new KeyNotFoundException("Demanda integrada não encontrada.");
            return await BuildResponseAsync(refreshedRequest, cancellationToken);
        }

        if (string.Equals(request.Stage, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            var currentCompleted = await _demandas.FindOneAsync(technicalUserId, demandaId, cancellationToken);
            using var currentCompletedJson = JsonDocument.Parse(JsonSerializer.Serialize(currentCompleted));
            var completedSubject = StripWorkflowPrefix(ReadString(currentCompletedJson.RootElement, "assunto"));
            // Finaliza só o vínculo da venda no Parceiros.
            // A demanda no Task permanece no status atual para outros trâmites.
            if (!string.IsNullOrWhiteSpace(completedSubject))
            {
                await _demandas.UpdateAsync(technicalUserId, demandaId, new UpdateDemandaRequest
                {
                    Assunto = completedSubject,
                }, cancellationToken);
            }
            await _supabase.UpdateSingleAsync(
                "luxus_parceiros_demanda",
                $"id=eq.{Uri.EscapeDataString(mapping.GetStringOrEmpty("id"))}",
                new
                {
                    workflow_stage = request.Stage,
                    turn_request_from = (string?)null,
                    turn_request_reason = (string?)null,
                    turn_request_at = (DateTimeOffset?)null,
                    updated_at = DateTimeOffset.UtcNow,
                },
                cancellationToken);
            await _demandas.AddObservacaoAsync(
                technicalUserId,
                demandaId,
                $"[ETAPA LUXUS PARCEIROS] Venda finalizada no Parceiros. A demanda Task permanece aberta. {request.Note}".Trim(),
                cancellationToken);
            var refreshedCompleted = await FindMappingByExternalIdAsync(externalRequestId, cancellationToken)
                                     ?? throw new KeyNotFoundException("Demanda integrada não encontrada.");
            return await BuildResponseAsync(refreshedCompleted, cancellationToken);
        }

        if (string.Equals(request.Stage, "TASK_VALIDATING_SIGNED_CONTRACT", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(request.DocumentId) || string.IsNullOrWhiteSpace(request.DocumentName))
                throw new InvalidOperationException("Informe o contrato assinado que será enviado.");
            var buffer = await DownloadPartnerDocumentBufferAsync(externalRequestId, request.DocumentId, cancellationToken);
            var importedSigned = await _demandas.AddAnexoForIntegrationAsync(
                technicalUserId,
                demandaId,
                buffer,
                request.DocumentName,
                request.DocumentName,
                request.DocumentMimeType ?? "application/pdf",
                buffer.LongLength,
                cancellationToken);
            using var importedSignedJson = JsonDocument.Parse(JsonSerializer.Serialize(importedSigned));
            var importedSignedId = ReadString(importedSignedJson.RootElement, "id");
            if (!string.IsNullOrWhiteSpace(importedSignedId))
            {
                var existingSourceIds = mapping.GetArrayOrEmpty("source_attachment_ids")
                    .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .ToList();
                existingSourceIds.Add(importedSignedId);
                await _supabase.UpdateSingleAsync(
                    "luxus_parceiros_demanda",
                    $"id=eq.{Uri.EscapeDataString(mapping.GetStringOrEmpty("id"))}",
                    new { source_attachment_ids = existingSourceIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() },
                    cancellationToken);
            }

            var current = await _demandas.FindOneAsync(technicalUserId, demandaId, cancellationToken);
            using var currentJson = JsonDocument.Parse(JsonSerializer.Serialize(current));
            var subject = StripWorkflowPrefix(ReadString(currentJson.RootElement, "assunto"));
            await _demandas.UpdateAsync(technicalUserId, demandaId, new UpdateDemandaRequest
            {
                Status = "em_andamento",
                Assunto = subject,
            }, cancellationToken);
        }

        var label = request.Stage.ToUpperInvariant() switch
        {
            "AWAITING_PARTNER_SIGNATURE" => "Em andamento",
            "TASK_VALIDATING_SIGNED_CONTRACT" => "Em andamento",
            "TASK_PROCESSING" => "Em andamento",
            "BLANK_CONTRACT_READY_FOR_ADMIN" => "Em andamento",
            "SIGNED_CONTRACT_READY_FOR_ADMIN" => "Em andamento",
            "TASK_APPROVED_REVIEW_PENDING" => "Em andamento",
            "TASK_REJECTED_REVIEW_PENDING" => "Em andamento",
            "CHANGES_REQUESTED" => "Em andamento",
            "COMPLETED" => "Concluído",
            _ => "Em andamento",
        };
        await _demandas.AddObservacaoAsync(
            technicalUserId,
            demandaId,
            $"[ETAPA LUXUS PARCEIROS] {label}. {request.Note}".Trim(),
            cancellationToken);
        var demandForLabel = await _demandas.FindOneAsync(technicalUserId, demandaId, cancellationToken);
        using (var labelJson = JsonDocument.Parse(JsonSerializer.Serialize(demandForLabel)))
        {
            var cleanSubject = StripWorkflowPrefix(ReadString(labelJson.RootElement, "assunto"));
            if (!string.IsNullOrWhiteSpace(cleanSubject))
            {
                await _supabase.UpdateSingleAsync(
                    "Demanda",
                    $"id=eq.{Uri.EscapeDataString(demandaId)}",
                    new { assunto = cleanSubject },
                    cancellationToken);
            }
        }
        await _supabase.UpdateSingleAsync(
            "luxus_parceiros_demanda",
            $"id=eq.{Uri.EscapeDataString(mapping.GetStringOrEmpty("id"))}",
            new
            {
                workflow_stage = request.Stage,
                turn_request_from = (string?)null,
                turn_request_reason = (string?)null,
                turn_request_at = (DateTimeOffset?)null,
                updated_at = DateTimeOffset.UtcNow,
            },
            cancellationToken);
        var refreshed = await FindMappingByExternalIdAsync(externalRequestId, cancellationToken)
                        ?? throw new KeyNotFoundException("Demanda integrada não encontrada.");
        return await BuildResponseAsync(refreshed, cancellationToken);
    }

    public async Task<DemandaDownloadResult> DownloadAttachmentAsync(
        string externalRequestId,
        string attachmentId,
        CancellationToken cancellationToken)
    {
        var mapping = await FindMappingByExternalIdAsync(externalRequestId, cancellationToken)
                      ?? throw new KeyNotFoundException("Demanda integrada não encontrada.");
        var technicalUserId = await EnsureTechnicalUserAsync(cancellationToken);
        return await _demandas.GetAnexoForDownloadAsync(
            technicalUserId,
            mapping.GetStringOrEmpty("demanda_id"),
            attachmentId,
            cancellationToken);
    }

    public async Task<object> ImportPartnerDocumentsAsync(
        string externalRequestId,
        ImportLuxusParceirosDocumentsRequest request,
        CancellationToken cancellationToken)
    {
        var mapping = await FindMappingByExternalIdAsync(externalRequestId, cancellationToken)
                      ?? throw new KeyNotFoundException("Demanda integrada não encontrada.");
        var technicalUserId = await EnsureTechnicalUserAsync(cancellationToken);
        var demandaId = mapping.GetStringOrEmpty("demanda_id");
        var demand = await _demandas.FindOneAsync(technicalUserId, demandaId, cancellationToken);
        using var demandJson = JsonDocument.Parse(JsonSerializer.Serialize(demand));
        var existingAnexos = ReadArray(demandJson.RootElement, "anexos")
            .Select(item => (Id: ReadString(item, "id"), Filename: ReadString(item, "filename")))
            .Where(item => !string.IsNullOrWhiteSpace(item.Filename))
            .ToList();
        var existingNames = existingAnexos
            .Select(item => item.Filename)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceAttachmentIds = mapping.GetArrayOrEmpty("source_attachment_ids")
            .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
        var imported = 0;
        var failed = new List<string>();
        var skipped = 0;
        foreach (var document in request.Documents)
        {
            if (string.IsNullOrWhiteSpace(document.Id) || string.IsNullOrWhiteSpace(document.Name))
            {
                failed.Add("documento sem id/nome");
                continue;
            }
            // Não pular só pelo nome original (ex.: vários "image.jpg" de CPF/RG).
            // Só considera já importado se o filename único (tipo+nome ou id curto) já existe.
            var shortId = document.Id.Replace("-", "");
            if (shortId.Length > 8) shortId = shortId[..8];
            var typedName = $"{document.Type}-{document.Name}";
            var alreadyThere = existingAnexos
                .Where(item =>
                    item.Filename.Contains(shortId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Filename, typedName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Filename, $"{document.Type}-{shortId}-{document.Name}", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var previous in alreadyThere)
            {
                if (!string.IsNullOrWhiteSpace(previous.Id))
                {
                    await _supabase.DeleteAsync(
                        "anexo",
                        $"id=eq.{Uri.EscapeDataString(previous.Id)}",
                        cancellationToken);
                    sourceAttachmentIds.RemoveAll(id =>
                        string.Equals(id, previous.Id, StringComparison.OrdinalIgnoreCase));
                }
                existingNames.Remove(previous.Filename);
                existingAnexos.Remove(previous);
            }
            try
            {
                var buffer = await ResolvePartnerDocumentBufferAsync(externalRequestId, document, cancellationToken);
                var mimeType = string.IsNullOrWhiteSpace(document.MimeType)
                    ? "application/octet-stream"
                    : document.MimeType;
                var uniqueFilename = BuildUniqueAttachmentFilename(
                    document.Name,
                    document.Type,
                    document.Id,
                    existingNames);
                var created = await _demandas.AddAnexoForIntegrationAsync(
                    technicalUserId,
                    demandaId,
                    buffer,
                    uniqueFilename,
                    document.Name,
                    mimeType,
                    buffer.LongLength,
                    cancellationToken);
                using var createdJson = JsonDocument.Parse(JsonSerializer.Serialize(created));
                var createdId = ReadString(createdJson.RootElement, "id");
                if (!string.IsNullOrWhiteSpace(createdId)) sourceAttachmentIds.Add(createdId);
                existingNames.Add(uniqueFilename);
                existingAnexos.Add((createdId, uniqueFilename));
                imported++;
            }
            catch (Exception error)
            {
                failed.Add($"{document.Type}:{document.Name} ({error.Message})");
                Console.Error.WriteLine(
                    $"[luxus-parceiros] Falha ao reimportar documento {document.Id}/{document.Name}: {error.Message}");
            }
        }

        if (imported > 0)
        {
            await _supabase.UpdateSingleAsync(
                "luxus_parceiros_demanda",
                $"id=eq.{Uri.EscapeDataString(mapping.GetStringOrEmpty("id"))}",
                new { source_attachment_ids = sourceAttachmentIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() },
                cancellationToken);
            mapping = await FindMappingByExternalIdAsync(externalRequestId, cancellationToken) ?? mapping;
            demand = await _demandas.FindOneAsync(technicalUserId, demandaId, cancellationToken);
            await NotifyIfIntegratedAsync(demandaId, demand, cancellationToken);
        }

        if (failed.Count > 0 && imported == 0 && skipped == 0)
        {
            throw new InvalidOperationException(
                $"Nenhum anexo importado. {string.Join("; ", failed)}");
        }

        return new { imported, skipped, failed, total = request.Documents.Count };
    }

    public async Task<object> RemovePartnerDocumentAsync(
        string externalRequestId,
        string documentId,
        string? taskAttachmentId,
        string? documentType,
        string? documentName,
        CancellationToken cancellationToken)
    {
        var mapping = await FindMappingByExternalIdAsync(externalRequestId, cancellationToken)
                      ?? throw new KeyNotFoundException("Demanda integrada não encontrada.");
        var technicalUserId = await EnsureTechnicalUserAsync(cancellationToken);
        var demandaId = mapping.GetStringOrEmpty("demanda_id");
        var demand = await _demandas.FindOneAsync(technicalUserId, demandaId, cancellationToken);
        using var demandJson = JsonDocument.Parse(JsonSerializer.Serialize(demand));
        var shortId = (documentId ?? string.Empty).Replace("-", "");
        if (shortId.Length > 8) shortId = shortId[..8];
        var typedName = string.IsNullOrWhiteSpace(documentType) || string.IsNullOrWhiteSpace(documentName)
            ? string.Empty
            : $"{documentType}-{documentName}";
        var typedWithShortId = string.IsNullOrWhiteSpace(typedName) ? string.Empty : $"{documentType}-{shortId}-{documentName}";
        var matches = ReadArray(demandJson.RootElement, "anexos")
            .Select(item => (Id: ReadString(item, "id"), Filename: ReadString(item, "filename")))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .Where(item =>
                (!string.IsNullOrWhiteSpace(taskAttachmentId)
                    && string.Equals(item.Id, taskAttachmentId, StringComparison.OrdinalIgnoreCase))
                || (shortId.Length >= 8
                    && item.Filename.Contains(shortId, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(typedName)
                    && string.Equals(item.Filename, typedName, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(typedWithShortId)
                    && string.Equals(item.Filename, typedWithShortId, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var sourceAttachmentIds = mapping.GetArrayOrEmpty("source_attachment_ids")
            .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
        var removed = 0;
        foreach (var anexo in matches)
        {
            await _supabase.DeleteAsync(
                "anexo",
                $"id=eq.{Uri.EscapeDataString(anexo.Id)}",
                cancellationToken);
            sourceAttachmentIds.RemoveAll(id =>
                string.Equals(id, anexo.Id, StringComparison.OrdinalIgnoreCase));
            removed++;
        }
        if (removed > 0)
        {
            await _supabase.UpdateSingleAsync(
                "luxus_parceiros_demanda",
                $"id=eq.{Uri.EscapeDataString(mapping.GetStringOrEmpty("id"))}",
                new { source_attachment_ids = sourceAttachmentIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() },
                cancellationToken);
            var refreshed = await _demandas.FindOneAsync(technicalUserId, demandaId, cancellationToken);
            await NotifyIfIntegratedAsync(demandaId, refreshed, cancellationToken);
        }
        return new { removed };
    }

    public async Task NotifyByDemandaIdAsync(string demandaId, CancellationToken cancellationToken)
    {
        var technicalUserId = await EnsureTechnicalUserAsync(cancellationToken);
        var demand = await _demandas.FindOneAsync(technicalUserId, demandaId, cancellationToken);
        await NotifyIfIntegratedAsync(demandaId, demand, cancellationToken);
    }

    public async Task NotifyIfIntegratedAsync(
        string demandaId,
        object currentDemand,
        CancellationToken cancellationToken)
    {
        var rows = await _supabase.QueryRowsAsync(
            $"luxus_parceiros_demanda?select=*&demanda_id=eq.{Uri.EscapeDataString(demandaId)}&limit=1",
            cancellationToken);
        var mapping = rows.FirstOrDefault();
        if (mapping.ValueKind == JsonValueKind.Undefined)
        {
            return;
        }

        try
        {
            using (var demandDocument = JsonDocument.Parse(JsonSerializer.Serialize(currentDemand)))
            {
                var demandStatus = ReadString(demandDocument.RootElement, "status");
                var isSale = string.Equals(mapping.GetNullableString("entity_type"), "sale", StringComparison.OrdinalIgnoreCase);
                var currentStage = mapping.GetNullableString("workflow_stage") ?? string.Empty;
                if (isSale
                    && string.Equals(demandStatus, "concluido", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(currentStage, "COMPLETED", StringComparison.OrdinalIgnoreCase))
                {
                    await _supabase.UpdateSingleAsync(
                        "luxus_parceiros_demanda",
                        $"id=eq.{Uri.EscapeDataString(mapping.GetStringOrEmpty("id"))}",
                        new
                        {
                            workflow_stage = "COMPLETED",
                            updated_at = DateTimeOffset.UtcNow,
                        },
                        cancellationToken);
                    var refreshed = await _supabase.QueryRowsAsync(
                        $"luxus_parceiros_demanda?select=*&demanda_id=eq.{Uri.EscapeDataString(demandaId)}&limit=1",
                        cancellationToken);
                    var refreshedMapping = refreshed.FirstOrDefault();
                    if (refreshedMapping.ValueKind != JsonValueKind.Undefined)
                    {
                        mapping = refreshedMapping;
                    }
                }
            }
            var payload = await BuildCallbackPayloadAsync(mapping, currentDemand, cancellationToken);
            var callbackUrl = string.IsNullOrWhiteSpace(_options.LuxusParceirosCallbackUrl)
                ? DefaultLuxusParceirosCallbackUrl
                : _options.LuxusParceirosCallbackUrl;
            if (!string.IsNullOrWhiteSpace(callbackUrl))
            {
                var client = _httpClientFactory.CreateClient();
                using var message = new HttpRequestMessage(
                    HttpMethod.Post,
                    callbackUrl)
                {
                    Content = JsonContent.Create(payload, options: CallbackJsonOptions),
                };
                message.Headers.Add("x-integration-key", _options.LuxusParceirosIntegrationKey);
                using var response = await client.SendAsync(message, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    var detail = body.Length > 500 ? body[..500] : body;
                    throw new InvalidOperationException(
                        $"HTTP {(int)response.StatusCode}: {detail.Replace('\n', ' ').Replace('\r', ' ').Trim()}");
                }
            }
            await _supabase.UpdateSingleAsync(
                "luxus_parceiros_demanda",
                $"id=eq.{Uri.EscapeDataString(mapping.GetStringOrEmpty("id"))}",
                new
                {
                    last_callback_at = DateTimeOffset.UtcNow,
                    last_callback_error = (string?)null,
                },
                cancellationToken);
        }
        catch (Exception error)
        {
            try
            {
                await _supabase.UpdateSingleAsync(
                    "luxus_parceiros_demanda",
                    $"id=eq.{Uri.EscapeDataString(mapping.GetStringOrEmpty("id"))}",
                    new
                    {
                        last_callback_error = error.Message,
                    },
                    cancellationToken);
            }
            catch
            {
                // O trabalho no Task não pode falhar por indisponibilidade do sistema parceiro.
            }
        }
    }

    private async Task<object> BuildResponseAsync(JsonElement mapping, CancellationToken cancellationToken)
    {
        var isSaleWorkflow = string.Equals(
            mapping.GetNullableString("entity_type"),
            "sale",
            StringComparison.OrdinalIgnoreCase);
        if (isSaleWorkflow && mapping.GetArrayOrEmpty("source_attachment_ids").Count == 0)
        {
            var externalRequestId = mapping.GetStringOrEmpty("external_request_id");
            var listedDocuments = await ListPartnerDocumentsAsync(externalRequestId, cancellationToken);
            if (listedDocuments.Count > 0)
            {
                try
                {
                    await ImportPartnerDocumentsAsync(
                        externalRequestId,
                        new ImportLuxusParceirosDocumentsRequest { Documents = listedDocuments },
                        cancellationToken);
                    mapping = await FindMappingByExternalIdAsync(externalRequestId, cancellationToken)
                              ?? mapping;
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(
                        $"[luxus-parceiros] Falha na recuperação automática dos anexos da venda {externalRequestId}: {error.Message}");
                }
            }
        }

        var technicalUserId = await EnsureTechnicalUserAsync(cancellationToken);
        var demand = await _demandas.FindOneAsync(
            technicalUserId,
            mapping.GetStringOrEmpty("demanda_id"),
            cancellationToken);

        // Não reimporta a partir de texto de observações (isso recriava lixo e anexos errados).
        // A importação oficial é via POST /anexos com ContentBase64 / download da API.
        return await BuildCallbackPayloadAsync(mapping, demand, cancellationToken);
    }

    private async Task<bool> ImportMissingPartnerDocumentsAsync(
        JsonElement mapping,
        object demand,
        string technicalUserId,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(mapping.GetNullableString("entity_type"), "sale", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        using var demandJson = JsonDocument.Parse(JsonSerializer.Serialize(demand));
        var root = demandJson.RootElement;
        var instructions = ReadString(root, "observacoesGerais");
        var observationTexts = ReadArray(root, "observacoes")
            .Select(item => ReadString(item, "texto"))
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var searchableText = string.Join('\n', new[] { instructions }.Concat(observationTexts).Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(searchableText))
        {
            return false;
        }

        var existingNames = ReadArray(root, "anexos")
            .Select(item => ReadString(item, "filename"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceAttachmentIds = mapping.GetArrayOrEmpty("source_attachment_ids")
            .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
        var importedAny = false;
        var documentPattern = new Regex(
            @"Documento\s+(?<type>[^:]+):\s*(?<name>.+?)\s+[—-]\s+https?://\S+/documents/(?<id>[0-9a-f-]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var fallbackPattern = new Regex(
            @"documento\s+(?<name>.+?)\.\s+Ele continua disponível em\s+(?<url>https?://\S+/documents/(?<id>[0-9a-f-]+))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var seenDocumentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in documentPattern.Matches(searchableText).Cast<Match>()
            .Concat(fallbackPattern.Matches(searchableText).Cast<Match>()))
        {
            var documentId = match.Groups["id"].Value;
            var documentName = match.Groups["name"].Value.Trim();
            var documentType = match.Groups["type"].Success ? match.Groups["type"].Value.Trim() : "DOCUMENTO";
            if (string.IsNullOrWhiteSpace(documentId)
                || string.IsNullOrWhiteSpace(documentName)
                || !seenDocumentIds.Add(documentId)
                || existingNames.Contains(documentName))
            {
                continue;
            }

            try
            {
                var buffer = await DownloadPartnerDocumentBufferAsync(
                    mapping.GetStringOrEmpty("external_request_id"),
                    documentId,
                    cancellationToken);
                var mimeType = "application/octet-stream";
                var imported = await _demandas.AddAnexoForIntegrationAsync(
                    technicalUserId,
                    mapping.GetStringOrEmpty("demanda_id"),
                    buffer,
                    documentName,
                    $"{documentType} — {documentName}",
                    mimeType,
                    buffer.LongLength,
                    cancellationToken);
                using var importedJson = JsonDocument.Parse(JsonSerializer.Serialize(imported));
                var importedId = ReadString(importedJson.RootElement, "id");
                if (!string.IsNullOrWhiteSpace(importedId)) sourceAttachmentIds.Add(importedId);
                existingNames.Add(documentName);
                importedAny = true;
            }
            catch
            {
                // Mantém tentativa silenciosa; a UI já mostra o link legado se ainda existir.
            }
        }

        if (!importedAny) return false;

        await _supabase.UpdateSingleAsync(
            "luxus_parceiros_demanda",
            $"id=eq.{Uri.EscapeDataString(mapping.GetStringOrEmpty("id"))}",
            new { source_attachment_ids = sourceAttachmentIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() },
            cancellationToken);
        return true;
    }

    private async Task<object> BuildCallbackPayloadAsync(
        JsonElement mapping,
        object demand,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(demand));
        var root = document.RootElement;
        var observations = ReadArray(root, "observacoes")
            .Select(item => ReadString(item, "texto"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Where(value =>
                value.IndexOf("Não foi possível copiar automaticamente", StringComparison.OrdinalIgnoreCase) < 0
                && value.IndexOf("continua disponível em", StringComparison.OrdinalIgnoreCase) < 0)
            .ToArray();
        var taskResponses = observations
            .Where(value => !value.StartsWith(
                "Luxus Parceiros —",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var responsibles = ReadArray(root, "responsaveis");
        var principal = responsibles.FirstOrDefault(item =>
            ReadBoolean(item, "isPrincipal") || ReadBoolean(item, "is_principal"));
        if (principal.ValueKind == JsonValueKind.Undefined)
        {
            principal = responsibles.FirstOrDefault();
        }
        var user = ReadObject(principal, "user");
        var responsibleId = ReadString(user, "id");
        var editorName = string.Empty;
        var editorActivity = string.Empty;
        string? editorLastSeenAt = null;
        var isBeingEdited = false;
        if (!string.IsNullOrWhiteSpace(responsibleId))
        {
            var presenceRows = await _supabase.QueryRowsAsync(
                $"user_presence?select=status,pathname,page_label,activity,last_seen_at&user_id=eq.{Uri.EscapeDataString(responsibleId)}&limit=1",
                cancellationToken);
            var presence = presenceRows.FirstOrDefault();
            if (presence.ValueKind != JsonValueKind.Undefined)
            {
                editorName = ReadString(user, "name");
                editorActivity = presence.GetNullableString("activity")
                    ?? presence.GetNullableString("page_label")
                    ?? string.Empty;
                editorLastSeenAt = presence.GetNullableString("last_seen_at");
                var pathname = presence.GetNullableString("pathname") ?? string.Empty;
                var status = presence.GetNullableString("status") ?? "online";
                isBeingEdited = DateTimeOffset.TryParse(editorLastSeenAt, out var lastSeen)
                    && lastSeen >= DateTimeOffset.UtcNow.AddSeconds(-90)
                    && !string.Equals(status, "offline", StringComparison.OrdinalIgnoreCase)
                    && pathname.Contains(ReadString(root, "id"), StringComparison.OrdinalIgnoreCase);
            }
        }
        var taskStatus = ReadString(root, "status");
        var isSaleWorkflow = string.Equals(mapping.GetNullableString("entity_type"), "sale", StringComparison.OrdinalIgnoreCase);
        var workflowStage = isSaleWorkflow
            ? mapping.GetNullableString("workflow_stage") ?? "TASK_PROCESSING"
            : string.Empty;
        if (isSaleWorkflow && string.Equals(taskStatus, "concluido", StringComparison.OrdinalIgnoreCase))
        {
            workflowStage = "COMPLETED";
        }
        var sourceIds = mapping.GetArrayOrEmpty("source_attachment_ids")
            .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var workflowAttachments = ReadArray(root, "anexos")
            .Where(item => !sourceIds.Contains(ReadString(item, "id")))
            .Select(item => new
            {
                id = ReadString(item, "id"),
                name = string.IsNullOrWhiteSpace(ReadString(item, "displayName"))
                    ? ReadString(item, "filename")
                    : ReadString(item, "displayName"),
                mimeType = NullIfBlank(ReadString(item, "mime_type")),
                size = ReadLong(item, "size"),
                createdAt = OptionalIso(
                    FirstPresent(ReadString(item, "created_at"), ReadString(item, "createdAt"))),
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.id))
            .ToArray();
        // status concluido da demanda Task envia workflowStage COMPLETED e conclui a venda no Parceiros.
        var resolution = taskResponses.Length > 0
            ? taskResponses[^1]
            : string.Equals(taskStatus, "concluido", StringComparison.OrdinalIgnoreCase)
                ? ReadString(root, "observacoesGerais")
                : string.Empty;
        return new
        {
            externalRequestId = mapping.GetStringOrEmpty("external_request_id"),
            demandId = ReadString(root, "id"),
            protocol = ReadString(root, "protocolo"),
            status = string.IsNullOrWhiteSpace(taskStatus) ? "em_andamento" : taskStatus,
            workflowStage = NullIfBlank(workflowStage),
            // Sempre envia anexos criados no Task (exceto os que vieram do Parceiros).
            attachments = workflowAttachments,
            resolution = NullIfBlank(resolution),
            observations,
            responsibleId = OptionalUuid(ReadString(user, "id")),
            responsibleName = NullIfBlank(ReadString(user, "name")),
            isBeingEdited,
            editorName = NullIfBlank(editorName),
            editorActivity = NullIfBlank(editorActivity),
            editorLastSeenAt = OptionalIso(editorLastSeenAt),
            updatedAt = OptionalIso(FirstPresent(ReadString(root, "updatedAt"), ReadString(root, "updated_at"))),
        };
    }

    private static string BuildUniqueAttachmentFilename(
        string name,
        string type,
        string documentId,
        ISet<string> usedFilenames)
    {
        var safeName = string.IsNullOrWhiteSpace(name) ? "arquivo" : name.Trim();
        var safeType = string.IsNullOrWhiteSpace(type)
            ? "DOC"
            : type.Trim().Replace(' ', '_');
        var shortId = (documentId ?? string.Empty).Replace("-", "");
        if (shortId.Length > 8) shortId = shortId[..8];
        var candidates = new[]
        {
            $"{safeType}-{safeName}",
            $"{safeType}-{shortId}-{safeName}",
            $"{shortId}-{safeName}",
        };
        foreach (var candidate in candidates)
        {
            if (!usedFilenames.Contains(candidate)) return candidate;
        }
        return $"{safeType}-{Guid.NewGuid():N}-{safeName}";
    }

    private async Task<byte[]> ResolvePartnerDocumentBufferAsync(
        string saleId,
        LuxusParceirosDocumentDto document,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(document.ContentBase64))
        {
            try
            {
                var buffer = Convert.FromBase64String(document.ContentBase64);
                if (buffer.Length > 0) return buffer;
            }
            catch (FormatException error)
            {
                Console.Error.WriteLine(
                    $"[luxus-parceiros] ContentBase64 inválido para {document.Id}: {error.Message}");
            }
        }

        return await DownloadPartnerDocumentBufferAsync(saleId, document.Id, cancellationToken);
    }

    private async Task<byte[]> DownloadPartnerDocumentBufferAsync(
        string saleId,
        string documentId,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var httpClient = _httpClientFactory.CreateClient();
                using var message = new HttpRequestMessage(HttpMethod.Get, BuildPartnerDocumentUrl(saleId, documentId));
                message.Headers.Add("x-integration-key", _options.LuxusParceirosIntegrationKey);
                using var response = await httpClient.SendAsync(message, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new InvalidOperationException(
                        $"Falha ao baixar documento no Luxus Parceiros (HTTP {(int)response.StatusCode}): {body}");
                }

                var buffer = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (buffer.Length == 0)
                {
                    throw new InvalidOperationException("O documento veio vazio do Luxus Parceiros.");
                }
                return buffer;
            }
            catch (Exception error) when (attempt < 3)
            {
                lastError = error;
                await Task.Delay(250 * attempt, cancellationToken);
            }
            catch (Exception error)
            {
                lastError = error;
            }
        }

        throw lastError ?? new InvalidOperationException("Falha ao baixar documento no Luxus Parceiros.");
    }

    private async Task<List<LuxusParceirosDocumentDto>> ListPartnerDocumentsAsync(
        string saleId,
        CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            var configured = string.IsNullOrWhiteSpace(_options.LuxusParceirosCallbackUrl)
                ? DefaultLuxusParceirosCallbackUrl
                : _options.LuxusParceirosCallbackUrl;
            if (!Uri.TryCreate(configured, UriKind.Absolute, out var callback))
            {
                return [];
            }

            var path = callback.AbsolutePath.TrimEnd('/');
            const string callbackSuffix = "/callback";
            if (path.EndsWith(callbackSuffix, StringComparison.OrdinalIgnoreCase))
                path = path[..^callbackSuffix.Length];
            var listUrl = new UriBuilder(callback)
            {
                Path = $"{path}/sales/{Uri.EscapeDataString(saleId)}/documents",
                Query = string.Empty,
            }.Uri;

            using var message = new HttpRequestMessage(HttpMethod.Get, listUrl);
            message.Headers.Add("x-integration-key", _options.LuxusParceirosIntegrationKey);
            using var response = await httpClient.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode) return [];
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(payload);
            // A API do Parceiros envolve listas em { success, data: [...] }.
            var listElement = json.RootElement;
            if (listElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in listElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, "data", StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.Array)
                    {
                        listElement = property.Value.Clone();
                        break;
                    }
                }
            }
            if (listElement.ValueKind != JsonValueKind.Array) return [];
            var documents = new List<LuxusParceirosDocumentDto>();
            foreach (var item in listElement.EnumerateArray())
            {
                var id = ReadString(item, "id");
                var name = ReadString(item, "name");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
                documents.Add(new LuxusParceirosDocumentDto
                {
                    Id = id,
                    Name = name,
                    Type = ReadString(item, "type"),
                    MimeType = ReadString(item, "mimeType"),
                    Size = ReadLong(item, "size"),
                });
            }
            return documents;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[luxus-parceiros] Falha ao listar documentos da venda {saleId}: {error.Message}");
            return [];
        }
    }

    private static string StripPartnerDocumentListing(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return string.Empty;
        var lines = description
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(line =>
                line.IndexOf("Documentos recebidos no Parceiros", StringComparison.OrdinalIgnoreCase) < 0
                && line.IndexOf("Não foi possível copiar automaticamente", StringComparison.OrdinalIgnoreCase) < 0
                && line.IndexOf("continua disponível em", StringComparison.OrdinalIgnoreCase) < 0);
        return string.Join('\n', lines).Trim();
    }

    private static string StripWorkflowPrefix(string subject) =>
        Regex.Replace(subject ?? string.Empty, @"^\[[^\]]+\]\s*", string.Empty).Trim();

    private static string BuildParceirosOriginBlock(
        string? localProtocol,
        string? partnerName,
        string? branchName,
        string? requesterName,
        string? requesterEmail,
        string? description)
    {
        var cleanDescription = StripPartnerDocumentListing(description);
        var origin = new[]
        {
            $"Origem: Luxus Parceiros ({localProtocol})",
            string.IsNullOrWhiteSpace(partnerName) ? null : $"Parceiro: {partnerName}",
            string.IsNullOrWhiteSpace(branchName) ? null : $"Filial: {branchName}",
            string.IsNullOrWhiteSpace(requesterName)
                ? null
                : $"Solicitante: {requesterName}"
                  + (string.IsNullOrWhiteSpace(requesterEmail) ? string.Empty : $" <{requesterEmail}>"),
            string.Empty,
            cleanDescription,
        };
        return string.Join('\n', origin.Where(line => line is not null));
    }

    private static string MergeParceirosObservacoes(string? existing, string parceirosBlock)
    {
        var current = (existing ?? string.Empty).Trim();
        var block = (parceirosBlock ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(current))
        {
            return block;
        }
        if (string.IsNullOrWhiteSpace(block))
        {
            return current;
        }

        var idx = current.IndexOf(ParceirosOriginMarker, StringComparison.OrdinalIgnoreCase);
        var prefix = idx >= 0 ? current[..idx].TrimEnd() : current;
        return string.IsNullOrWhiteSpace(prefix) ? block : $"{prefix}\n\n{block}";
    }

    private static bool LooksLikeParceirosSaleTemplateAssunto(string? assunto) =>
        !string.IsNullOrWhiteSpace(assunto)
        && Regex.IsMatch(assunto, @"venda\s+linha\s+nova", RegexOptions.IgnoreCase);

    private static bool HasNomeLinhaPlaceholder(string? assunto) =>
        !string.IsNullOrWhiteSpace(assunto) && NomeLinhaPlaceholderRegex.IsMatch(assunto);

    private static string ReplaceNomeLinhaPlaceholder(string assunto, string subject) =>
        NomeLinhaPlaceholderRegex.Replace(assunto, subject.Trim()).Trim();

    private static string? InferSubjectFromParceirosBlock(string? observacoes)
    {
        if (string.IsNullOrWhiteSpace(observacoes))
        {
            return null;
        }

        var nome = Regex.Match(
            observacoes,
            @"^Nome\s+(.+)\s*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        var linha = Regex.Match(
            observacoes,
            @"^Linha\s+do\s+chip\s+(.+)\s*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!linha.Success)
        {
            linha = Regex.Match(
                observacoes,
                @"^Numero\s+novo\s+(.+)\s*$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
        }

        if (!nome.Success)
        {
            return null;
        }

        var clientName = nome.Groups[1].Value.Trim();
        var digits = linha.Success
            ? Regex.Replace(linha.Groups[1].Value, @"\D", string.Empty)
            : string.Empty;
        if (string.IsNullOrWhiteSpace(digits))
        {
            digits = "semlinha";
        }
        return string.IsNullOrWhiteSpace(clientName) ? null : $"{clientName} {digits}";
    }

    private static string ResolvePartnerBrand(string? partnerName)
    {
        var value = (partnerName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return "PARCEIRO";
        }

        var upper = value.ToUpperInvariant();
        if (upper.Contains("RAEL", StringComparison.Ordinal)) return "RAELCELL";
        if (upper.Contains("LINGO", StringComparison.Ordinal)) return "LINGO";
        if (upper.Contains("ELITE", StringComparison.Ordinal)) return "ELITE";
        if (upper.Contains("META", StringComparison.Ordinal)) return "META";
        return upper;
    }

    private static string BuildSaleAssuntoFromTemplate(
        TemplateDemandaSource template,
        string? partnerName,
        string? localProtocol,
        string? subject = null)
    {
        var brand = ResolvePartnerBrand(partnerName);
        var source = !string.IsNullOrWhiteSpace(template.AssuntoTemplate)
            ? template.AssuntoTemplate.Trim()
            : template.Name.Trim();
        var assunto = Regex.Replace(
            source,
            @"PARCEIRO\s*\(\s*META\s+ou\s+LINGO\s+ou\s+ELITE\s+ou\s+RAELCELL\s*\)",
            brand,
            RegexOptions.IgnoreCase);
        assunto = Regex.Replace(
            assunto,
            @"\(\s*META\s+ou\s+LINGO\s+ou\s+ELITE\s+ou\s+RAELCELL\s*\)",
            brand,
            RegexOptions.IgnoreCase);
        assunto = Regex.Replace(
            assunto,
            @"(?<=-\s*)PARCEIRO(?=\s*-)",
            brand,
            RegexOptions.IgnoreCase);
        if (!string.IsNullOrWhiteSpace(subject))
        {
            var cleanSubject = subject.Trim();
            if (HasNomeLinhaPlaceholder(assunto))
            {
                assunto = ReplaceNomeLinhaPlaceholder(assunto, cleanSubject);
            }
            else if (assunto.IndexOf(cleanSubject, StringComparison.OrdinalIgnoreCase) < 0)
            {
                assunto = $"{assunto} {cleanSubject}";
            }
        }
        if (!string.IsNullOrWhiteSpace(localProtocol)
            && assunto.IndexOf(localProtocol, StringComparison.OrdinalIgnoreCase) < 0)
        {
            assunto = $"{assunto} · {localProtocol.Trim()}";
        }
        return assunto.Trim();
    }

    private static List<DemandaResponsavelInput> MergeSaleResponsaveis(
        TemplateDemandaSource template,
        string partnerResponsibleId)
    {
        var result = new List<DemandaResponsavelInput>
        {
            new()
            {
                UserId = partnerResponsibleId,
                IsPrincipal = true,
            },
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { partnerResponsibleId };
        foreach (var item in template.Responsaveis)
        {
            if (string.IsNullOrWhiteSpace(item.UserId) || !seen.Add(item.UserId))
            {
                continue;
            }
            result.Add(new DemandaResponsavelInput
            {
                UserId = item.UserId,
                IsPrincipal = false,
            });
        }
        return result;
    }

    private async Task<TemplateDemandaSource?> TryLoadParceirosSaleTemplateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var configuredId = _options.LuxusParceirosSaleTemplateId?.Trim();
            string? templateId = null;
            if (!string.IsNullOrWhiteSpace(configuredId) && Guid.TryParse(configuredId, out _))
            {
                templateId = configuredId;
            }
            else
            {
                var name = string.IsNullOrWhiteSpace(_options.LuxusParceirosSaleTemplateName)
                    ? AppOptions.DefaultParceirosSaleTemplateName
                    : _options.LuxusParceirosSaleTemplateName.Trim();
                templateId = await _templates.FindIdByNameAsync(name, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(templateId))
            {
                Console.Error.WriteLine(
                    "[luxus-parceiros] Template de venda Parceiros não encontrado; criando demanda sem o modelo padrão.");
                return null;
            }

            return await _templates.LoadForDemandaAsync(templateId, cancellationToken);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                $"[luxus-parceiros] Falha ao carregar template de venda Parceiros: {error.Message}");
            return null;
        }
    }

    private async Task<bool> ApplySaleTemplateLayoutAsync(
        JsonElement mapping,
        TemplateDemandaSource template,
        CancellationToken cancellationToken)
    {
        var demandaId = mapping.GetStringOrEmpty("demanda_id");
        if (string.IsNullOrWhiteSpace(demandaId))
        {
            return false;
        }

        var demand = await _supabase.QuerySingleAsync(
            $"Demanda?select=id,assunto,status,observacoes_gerais&id=eq.{Uri.EscapeDataString(demandaId)}&limit=1",
            cancellationToken);
        if (demand is null)
        {
            return false;
        }

        var status = demand.Value.GetStringOrEmpty("status");
        if (string.Equals(status, "concluido", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "cancelado", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var currentAssunto = StripWorkflowPrefix(demand.Value.GetStringOrEmpty("assunto"));
        var currentObs = demand.Value.GetNullableString("observacoes_gerais") ?? string.Empty;
        var existingSubtarefas = await _supabase.QueryRowsAsync(
            $"subtarefa?select=titulo,ordem&demanda_id=eq.{Uri.EscapeDataString(demandaId)}",
            cancellationToken);
        var existingTitles = existingSubtarefas
            .Select(row => row.GetStringOrEmpty("titulo").Trim())
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingSubtarefas = template.Subtarefas
            .Where(item => !string.IsNullOrWhiteSpace(item.Titulo)
                && !existingTitles.Contains(item.Titulo.Trim()))
            .ToList();

        var existingSetores = await _supabase.QueryRowsAsync(
            $"demanda_setor?select=setor_id&demanda_id=eq.{Uri.EscapeDataString(demandaId)}",
            cancellationToken);
        var existingSetorIds = existingSetores
            .Select(row => row.GetStringOrEmpty("setor_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingSetores = template.SetorIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && !existingSetorIds.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existingResponsaveis = await _supabase.QueryRowsAsync(
            $"demanda_responsavel?select=user_id&demanda_id=eq.{Uri.EscapeDataString(demandaId)}",
            cancellationToken);
        var existingUserIds = existingResponsaveis
            .Select(row => row.GetStringOrEmpty("user_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingResponsaveis = template.Responsaveis
            .Where(item => !string.IsNullOrWhiteSpace(item.UserId) && !existingUserIds.Contains(item.UserId))
            .ToList();

        var needAssuntoPlaceholder = HasNomeLinhaPlaceholder(currentAssunto);
        var needAssuntoMissing = !LooksLikeParceirosSaleTemplateAssunto(currentAssunto);
        var needAssunto = needAssuntoPlaceholder || needAssuntoMissing;
        var hasSaleBlockInInstructions = currentObs.IndexOf(ParceirosOriginMarker, StringComparison.OrdinalIgnoreCase) >= 0;
        var needObs = !HasTemplateObservations(currentObs, template.ObservacoesGeraisTemplate)
                      || hasSaleBlockInInstructions;
        if (!needAssunto && !needObs && missingSubtarefas.Count == 0
            && missingSetores.Count == 0 && missingResponsaveis.Count == 0)
        {
            return false;
        }

        var protocol = mapping.GetStringOrEmpty("external_protocol");
        var partnerName = ExtractPartnerName(currentAssunto, currentObs);
        var externalRequestId = mapping.GetStringOrEmpty("external_request_id");
        var saleSummary = await FetchPartnerSaleSummaryAsync(externalRequestId, cancellationToken);
        if (saleSummary is not null)
        {
            if (string.IsNullOrWhiteSpace(partnerName) && !string.IsNullOrWhiteSpace(saleSummary.PartnerName))
            {
                partnerName = saleSummary.PartnerName;
            }
        }

        var updates = new Dictionary<string, object?>();
        if (needAssunto)
        {
            var subject = saleSummary?.Subject;
            if (needAssuntoPlaceholder && !string.IsNullOrWhiteSpace(subject))
            {
                updates["assunto"] = ReplaceNomeLinhaPlaceholder(currentAssunto, subject);
            }
            else if (needAssuntoMissing)
            {
                updates["assunto"] = BuildSaleAssuntoFromTemplate(template, partnerName, protocol, subject);
            }
            else if (needAssuntoPlaceholder && string.IsNullOrWhiteSpace(subject))
            {
                // Sem subject no Parceiros: tenta extrair cliente do bloco de origem já gravado.
                var inferred = InferSubjectFromParceirosBlock(currentObs);
                if (!string.IsNullOrWhiteSpace(inferred))
                {
                    updates["assunto"] = ReplaceNomeLinhaPlaceholder(currentAssunto, inferred);
                }
            }
        }
        if (needObs)
        {
            // Instruções = só o template; bloco da venda sai para a tabela observacao.
            updates["observacoes_gerais"] = template.ObservacoesGeraisTemplate ?? string.Empty;
        }
        if (updates.Count > 0)
        {
            updates["updated_at"] = DateTimeOffset.UtcNow;
            await _supabase.UpdateSingleAsync(
                "Demanda",
                $"id=eq.{Uri.EscapeDataString(demandaId)}",
                updates,
                cancellationToken);
        }

        var saleBlock = hasSaleBlockInInstructions
            ? ExtractParceirosSaleBlock(currentObs, protocol, partnerName)
            : null;
        if (string.IsNullOrWhiteSpace(saleBlock) && saleSummary is not null
            && !string.IsNullOrWhiteSpace(saleSummary.Observations))
        {
            saleBlock = BuildParceirosOriginBlock(
                protocol,
                partnerName ?? saleSummary.PartnerName,
                null,
                null,
                null,
                saleSummary.Observations);
        }
        if (!string.IsNullOrWhiteSpace(saleBlock))
        {
            var technicalUserId = await EnsureTechnicalUserAsync(cancellationToken);
            await EnsureParceirosSaleObservationAsync(
                technicalUserId,
                demandaId,
                saleBlock,
                cancellationToken);
        }

        if (missingSubtarefas.Count > 0)
        {
            var nextOrdem = existingSubtarefas
                .Select(row => row.GetNullableInt32("ordem") ?? 0)
                .DefaultIfEmpty(-1)
                .Max() + 1;
            await _supabase.InsertManyAsync(
                "subtarefa",
                missingSubtarefas.Select((item, index) => new
                {
                    demanda_id = demandaId,
                    titulo = item.Titulo.Trim(),
                    concluida = false,
                    ordem = nextOrdem + index,
                    responsavel_user_id = Guid.TryParse(item.ResponsavelUserId, out _)
                        ? item.ResponsavelUserId
                        : null,
                }),
                cancellationToken);
        }

        if (missingSetores.Count > 0)
        {
            await _supabase.InsertManyAsync(
                "demanda_setor",
                missingSetores.Select(setorId => new { demanda_id = demandaId, setor_id = setorId }),
                cancellationToken);
        }

        if (missingResponsaveis.Count > 0)
        {
            await _supabase.InsertManyAsync(
                "demanda_responsavel",
                missingResponsaveis.Select(item => new
                {
                    demanda_id = demandaId,
                    user_id = item.UserId,
                    is_principal = false,
                }),
                cancellationToken);
        }

        Console.WriteLine(
            $"[luxus-parceiros] Template de venda aplicado na demanda {demandaId} (protocolo {protocol}).");
        return true;
    }

    private static bool HasTemplateObservations(string? current, string? templateObs)
    {
        if (string.IsNullOrWhiteSpace(templateObs))
        {
            return true;
        }

        var needle = templateObs
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return !string.IsNullOrWhiteSpace(needle)
            && (current ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string? ExtractPartnerName(string? assunto, string? observacoes)
    {
        if (!string.IsNullOrWhiteSpace(observacoes))
        {
            var fromObs = Regex.Match(
                observacoes,
                @"^Parceiro:\s*(.+)\s*$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (fromObs.Success)
            {
                return fromObs.Groups[1].Value.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(assunto))
        {
            var fromAssunto = Regex.Match(
                assunto,
                @"^Venda\s+\S+\s+(.+?)\s+Linha\b",
                RegexOptions.IgnoreCase);
            if (fromAssunto.Success)
            {
                return fromAssunto.Groups[1].Value.Trim();
            }
        }

        return null;
    }

    private static string ExtractParceirosSaleBlock(string? currentObs, string? protocol, string? partnerName)
    {
        var current = (currentObs ?? string.Empty).Trim();
        var idx = current.IndexOf(ParceirosOriginMarker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            return current[idx..].Trim();
        }

        if (!string.IsNullOrWhiteSpace(current))
        {
            return current;
        }

        return BuildParceirosOriginBlock(protocol, partnerName, null, null, null, null);
    }

    private async Task<string> EnsureTechnicalUserAsync(CancellationToken cancellationToken)
    {
        var email = _options.LuxusParceirosTechnicalUserEmail.Trim().ToLowerInvariant();
        var existing = await _supabase.FindUserByEmailAsync(email, cancellationToken);
        if (existing is not null)
        {
            var needsRename = !string.Equals(
                existing.Name?.Trim(),
                TechnicalUserDisplayName,
                StringComparison.Ordinal);
            // Precisa aparecer no filtro Criador (dropdown só lista active=true).
            var needsActivate = !existing.Active;
            if (needsRename || needsActivate)
            {
                await _supabase.UpdateSingleAsync(
                    "User",
                    $"id=eq.{Uri.EscapeDataString(existing.Id)}",
                    new
                    {
                        name = TechnicalUserDisplayName,
                        active = true,
                    },
                    cancellationToken);
            }
            return existing.Id;
        }
        var row = await _supabase.InsertSingleAsync(
            "User",
            new
            {
                email,
                name = TechnicalUserDisplayName,
                active = true,
                password_hash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N")),
            },
            cancellationToken);
        return row.GetStringOrEmpty("id");
    }

    private async Task EnsureParceirosSaleObservationAsync(
        string technicalUserId,
        string demandaId,
        string saleBlock,
        CancellationToken cancellationToken)
    {
        var trimmed = saleBlock.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return;
        }

        var existing = await _supabase.QueryRowsAsync(
            $"observacao?select=id,texto&demanda_id=eq.{Uri.EscapeDataString(demandaId)}&order=created_at.asc&limit=100",
            cancellationToken);
        var alreadyPresent = existing.Any(row =>
        {
            var texto = row.GetStringOrEmpty("texto");
            return texto.IndexOf(ParceirosOriginMarker, StringComparison.OrdinalIgnoreCase) >= 0
                   || string.Equals(texto.Trim(), trimmed, StringComparison.Ordinal);
        });
        if (alreadyPresent)
        {
            return;
        }

        try
        {
            await _demandas.AddObservacaoAsync(technicalUserId, demandaId, trimmed, cancellationToken);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                $"[luxus-parceiros] Falha ao gravar observação Parceiros na demanda {demandaId}: {error.Message}");
        }
    }

    private sealed record PartnerSaleSummary(
        string? Subject,
        string? Observations,
        string? PartnerName,
        string? ClientName);

    private async Task<PartnerSaleSummary?> FetchPartnerSaleSummaryAsync(
        string? saleId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(saleId) || !Guid.TryParse(saleId, out _))
        {
            return null;
        }

        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            var configured = string.IsNullOrWhiteSpace(_options.LuxusParceirosCallbackUrl)
                ? DefaultLuxusParceirosCallbackUrl
                : _options.LuxusParceirosCallbackUrl;
            if (!Uri.TryCreate(configured, UriKind.Absolute, out var callback))
            {
                return null;
            }

            var path = callback.AbsolutePath.TrimEnd('/');
            const string callbackSuffix = "/callback";
            if (path.EndsWith(callbackSuffix, StringComparison.OrdinalIgnoreCase))
                path = path[..^callbackSuffix.Length];
            var summaryUrl = new UriBuilder(callback)
            {
                Path = $"{path}/sales/{Uri.EscapeDataString(saleId)}",
                Query = string.Empty,
            }.Uri;

            using var message = new HttpRequestMessage(HttpMethod.Get, summaryUrl);
            message.Headers.Add("x-integration-key", _options.LuxusParceirosIntegrationKey);
            using var response = await httpClient.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (string.Equals(property.Name, "data", StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.Object)
                    {
                        root = property.Value.Clone();
                        break;
                    }
                }
            }

            return new PartnerSaleSummary(
                ReadString(root, "subject"),
                !string.IsNullOrWhiteSpace(ReadString(root, "observations"))
                    ? ReadString(root, "observations")
                    : ReadString(root, "description"),
                ReadString(root, "partnerName"),
                ReadString(root, "clientName"));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                $"[luxus-parceiros] Falha ao buscar resumo da venda {saleId}: {error.Message}");
            return null;
        }
    }

    private async Task<JsonElement?> FindMappingByExternalIdAsync(
        string externalRequestId,
        CancellationToken cancellationToken)
    {
        var rows = await _supabase.QueryRowsAsync(
            $"luxus_parceiros_demanda?select=*&external_request_id=eq.{Uri.EscapeDataString(externalRequestId)}&limit=1",
            cancellationToken);
        var row = rows.FirstOrDefault();
        return row.ValueKind == JsonValueKind.Undefined ? null : row.Clone();
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FirstPresent(string primary, string fallback) =>
        string.IsNullOrWhiteSpace(primary) ? fallback : primary;

    private static string? OptionalIso(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed.ToUniversalTime().ToString("o")
            : null;
    }

    private static string? OptionalUuid(string? value) =>
        Guid.TryParse(value, out var parsed) ? parsed.ToString() : null;

    private static string ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.ToString();
            }
        }
        return string.Empty;
    }

    private static bool ReadBoolean(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static long ReadLong(JsonElement element, string name)
    {
        var raw = ReadString(element, name);
        return long.TryParse(raw, out var value) ? value : 0;
    }

    private static JsonElement ReadObject(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }
        return element;
    }

    private static JsonElement[] ReadArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return [];
        }
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Array)
            {
                return property.Value.EnumerateArray().Select(value => value.Clone()).ToArray();
            }
        }
        return [];
    }
}
