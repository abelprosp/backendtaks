using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    private readonly SupabaseRestService _supabase;
    private readonly DemandasService _demandas;
    private readonly ClientesService _clientes;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppOptions _options;

    public LuxusParceirosIntegrationService(
        SupabaseRestService supabase,
        DemandasService demandas,
        ClientesService clientes,
        IHttpClientFactory httpClientFactory,
        IOptions<AppOptions> options)
    {
        _supabase = supabase;
        _demandas = demandas;
        _clientes = clientes;
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
        var cleanDescription = StripPartnerDocumentListing(request.Description);
        var origin = new[]
        {
            $"Origem: Luxus Parceiros ({request.LocalProtocol})",
            $"Parceiro: {request.PartnerName}",
            string.IsNullOrWhiteSpace(request.BranchName) ? null : $"Filial: {request.BranchName}",
            $"Solicitante: {request.RequesterName} <{request.RequesterEmail}>",
            string.Empty,
            cleanDescription,
        };
        var created = await _demandas.CreateAsync(
            technicalUserId,
            new CreateDemandaRequest
            {
                Assunto = string.Equals(request.EntityType, "sale", StringComparison.OrdinalIgnoreCase)
                    ? $"[Aguardando Luxus Task] {request.Subject.Trim()}"
                    : request.Subject.Trim(),
                Prioridade = request.Priority ?? false,
                Prazo = deadline.ToString("yyyy-MM-dd"),
                Status = "em_aberto",
                ObservacoesGerais = string.Join('\n', origin.Where(line => line is not null)),
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

        using var createdJson = JsonDocument.Parse(JsonSerializer.Serialize(created));
        var demandaId = ReadString(createdJson.RootElement, "id");
        var protocol = ReadString(createdJson.RootElement, "protocolo");
        if (string.IsNullOrWhiteSpace(demandaId))
        {
            throw new InvalidOperationException("O Luxus Task criou a demanda sem retornar seu identificador.");
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
                if (usedFilenames.Contains(document.Name)) continue;
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
            "CHANGES_REQUESTED",
        };
        if (!allowed.Contains(request.Stage, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Etapa de venda inválida para esta operação.");

        var mapping = await FindMappingByExternalIdAsync(externalRequestId, cancellationToken)
                      ?? throw new KeyNotFoundException("Demanda integrada não encontrada.");
        if (!string.Equals(mapping.GetNullableString("entity_type"), "sale", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A demanda informada não pertence ao fluxo de vendas.");
        var technicalUserId = await EnsureTechnicalUserAsync(cancellationToken);
        var demandaId = mapping.GetStringOrEmpty("demanda_id");

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
                $"CONTRATO ASSINADO — {request.DocumentName}",
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
                Assunto = $"[Contrato assinado enviado para conferência] {subject}",
            }, cancellationToken);
        }

        var label = request.Stage.ToUpperInvariant() switch
        {
            "AWAITING_PARTNER_SIGNATURE" => "Aguardando assinatura do parceiro",
            "TASK_VALIDATING_SIGNED_CONTRACT" => "Contrato assinado recebido — aguardando validação final",
            "TASK_PROCESSING" => "Aguardando Luxus Task",
            "BLANK_CONTRACT_READY_FOR_ADMIN" => "Contrato em branco recebido",
            "SIGNED_CONTRACT_READY_FOR_ADMIN" => "Contrato assinado pelo parceiro",
            "CHANGES_REQUESTED" => "Correção do contrato solicitada",
            _ => request.Stage,
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
            await _supabase.UpdateSingleAsync(
                "Demanda",
                $"id=eq.{Uri.EscapeDataString(demandaId)}",
                new { assunto = $"[{label}] {cleanSubject}" },
                cancellationToken);
        }
        await _supabase.UpdateSingleAsync(
            "luxus_parceiros_demanda",
            $"id=eq.{Uri.EscapeDataString(mapping.GetStringOrEmpty("id"))}",
            new { workflow_stage = request.Stage, updated_at = DateTimeOffset.UtcNow },
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
        var existingNames = ReadArray(demandJson.RootElement, "anexos")
            .Select(item => ReadString(item, "filename"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
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
            var shortId = document.Id.Replace("-", "");
            if (shortId.Length > 8) shortId = shortId[..8];
            if (existingNames.Any(name =>
                    name.Contains(shortId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, document.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, $"{document.Type}-{document.Name}", StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
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
                    Content = JsonContent.Create(payload),
                };
                message.Headers.Add("x-integration-key", _options.LuxusParceirosIntegrationKey);
                using var response = await client.SendAsync(message, cancellationToken);
                response.EnsureSuccessStatusCode();
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
        var technicalUserId = await EnsureTechnicalUserAsync(cancellationToken);
        var demand = await _demandas.FindOneAsync(
            technicalUserId,
            mapping.GetStringOrEmpty("demanda_id"),
            cancellationToken);

        if (await ImportMissingPartnerDocumentsAsync(mapping, demand, technicalUserId, cancellationToken))
        {
            mapping = await FindMappingByExternalIdAsync(
                mapping.GetStringOrEmpty("external_request_id"),
                cancellationToken) ?? mapping;
            demand = await _demandas.FindOneAsync(
                technicalUserId,
                mapping.GetStringOrEmpty("demanda_id"),
                cancellationToken);
        }

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
                mimeType = ReadString(item, "mime_type"),
                size = ReadLong(item, "size"),
                createdAt = ReadString(item, "created_at"),
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.id))
            .ToArray();
        if (isSaleWorkflow
            && (string.Equals(taskStatus, "concluido", StringComparison.OrdinalIgnoreCase)
                || string.Equals(taskStatus, "cancelado", StringComparison.OrdinalIgnoreCase)))
        {
            var validatingSignedContract = string.Equals(
                workflowStage,
                "TASK_VALIDATING_SIGNED_CONTRACT",
                StringComparison.OrdinalIgnoreCase);
            var next = string.Equals(taskStatus, "cancelado", StringComparison.OrdinalIgnoreCase)
                ? "TASK_REJECTED_REVIEW_PENDING"
                : validatingSignedContract
                    && string.Equals(taskStatus, "concluido", StringComparison.OrdinalIgnoreCase)
                    ? "TASK_APPROVED_REVIEW_PENDING"
                    : workflowStage;
            if (!string.Equals(next, workflowStage, StringComparison.OrdinalIgnoreCase))
            {
                workflowStage = next;
                await _supabase.UpdateSingleAsync(
                    "luxus_parceiros_demanda",
                    $"id=eq.{Uri.EscapeDataString(mapping.GetStringOrEmpty("id"))}",
                    new { workflow_stage = workflowStage, updated_at = DateTimeOffset.UtcNow },
                    cancellationToken);
                var cleanSubject = StripWorkflowPrefix(ReadString(root, "assunto"));
                var prefix = workflowStage switch
                {
                    "TASK_APPROVED_REVIEW_PENDING" => "Contrato aprovado no Luxus Task",
                    "TASK_REJECTED_REVIEW_PENDING" => "Contrato recusado no Luxus Task",
                    "BLANK_CONTRACT_READY_FOR_ADMIN" => "Contrato em branco recebido",
                    _ => "Contrato em branco recebido",
                };
                await _supabase.UpdateSingleAsync(
                    "Demanda",
                    $"id=eq.{Uri.EscapeDataString(ReadString(root, "id"))}",
                    new { assunto = $"[{prefix}] {cleanSubject}" },
                    cancellationToken);
            }
        }
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
            status = taskStatus,
            workflowStage,
            // Sempre envia anexos criados no Task (exceto os que vieram do Parceiros).
            attachments = workflowAttachments,
            resolution,
            observations,
            responsibleId = ReadString(user, "id"),
            responsibleName = ReadString(user, "name"),
            isBeingEdited,
            editorName,
            editorActivity,
            editorLastSeenAt,
            updatedAt = ReadString(root, "updatedAt"),
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
                using var response = await httpClient.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new InvalidOperationException(
                        $"Falha ao baixar documento no Luxus Parceiros (HTTP {(int)response.StatusCode}): {body}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, cancellationToken);
                var buffer = memory.ToArray();
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
            if (json.RootElement.ValueKind != JsonValueKind.Array) return [];
            var documents = new List<LuxusParceirosDocumentDto>();
            foreach (var item in json.RootElement.EnumerateArray())
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

    private async Task<string> EnsureTechnicalUserAsync(CancellationToken cancellationToken)
    {
        var email = _options.LuxusParceirosTechnicalUserEmail.Trim().ToLowerInvariant();
        var existing = await _supabase.FindUserByEmailAsync(email, cancellationToken);
        if (existing is not null)
        {
            return existing.Id;
        }
        var row = await _supabase.InsertSingleAsync(
            "User",
            new
            {
                email,
                name = "LUXUSPARCEIROS",
                active = false,
                password_hash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N")),
            },
            cancellationToken);
        return row.GetStringOrEmpty("id");
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
