using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LuxusDemandas.Api.Configuration;
using LuxusDemandas.Api.Models;
using LuxusDemandas.Api.Support;
using Microsoft.Extensions.Options;

namespace LuxusDemandas.Api.Services;

public sealed class LuxusParceirosIntegrationService
{
    private readonly SupabaseRestService _supabase;
    private readonly DemandasService _demandas;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppOptions _options;

    public LuxusParceirosIntegrationService(
        SupabaseRestService supabase,
        DemandasService demandas,
        IHttpClientFactory httpClientFactory,
        IOptions<AppOptions> options)
    {
        _supabase = supabase;
        _demandas = demandas;
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
        var clientes = await _supabase.ListClientesAsync(true, cancellationToken);
        return clientes
            .Where(cliente =>
                string.IsNullOrWhiteSpace(normalized)
                || cliente.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(cliente.NomeFantasia)
                    && cliente.NomeFantasia.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(cliente.Documento)
                    && cliente.Documento.Contains(normalized, StringComparison.OrdinalIgnoreCase)))
            .Take(50)
            .Select(cliente => new LuxusParceirosClientDto(
                cliente.Id,
                cliente.Name,
                cliente.Documento,
                cliente.NomeFantasia))
            .ToList();
    }

    public async Task<object> CreateAsync(
        CreateLuxusParceirosDemandaRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.RequestId, out _)
            || !Guid.TryParse(request.ResponsibleId, out _)
            || !Guid.TryParse(request.ClientId, out _))
        {
            throw new InvalidOperationException("Solicitação, responsável ou cliente inválido.");
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
        var client = (await _supabase.ListClientesAsync(true, cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, request.ClientId, StringComparison.OrdinalIgnoreCase));
        if (client is null)
        {
            throw new KeyNotFoundException("Cliente não encontrado ou inativo no Luxus Task.");
        }

        var technicalUserId = await EnsureTechnicalUserAsync(cancellationToken);
        var origin = new[]
        {
            $"Origem: Luxus Parceiros ({request.LocalProtocol})",
            $"Parceiro: {request.PartnerName}",
            string.IsNullOrWhiteSpace(request.BranchName) ? null : $"Filial: {request.BranchName}",
            $"Solicitante: {request.RequesterName} <{request.RequesterEmail}>",
            string.Empty,
            request.Description.Trim(),
        };
        var created = await _demandas.CreateAsync(
            technicalUserId,
            new CreateDemandaRequest
            {
                Assunto = request.Subject.Trim(),
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
            },
            cancellationToken);

        return new
        {
            id = demandaId,
            protocol,
            status = "em_aberto",
            responsible = new { id = responsible.Id, name = responsible.Name, email = responsible.Email },
            updatedAt = DateTimeOffset.UtcNow,
            mappingId = mapping.GetStringOrEmpty("id"),
        };
    }

    public async Task<object> GetAsync(string externalRequestId, CancellationToken cancellationToken)
    {
        var mapping = await FindMappingByExternalIdAsync(externalRequestId, cancellationToken)
                      ?? throw new KeyNotFoundException("Demanda integrada não encontrada.");
        return await BuildResponseAsync(mapping, cancellationToken);
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
            var payload = BuildCallbackPayload(mapping, currentDemand);
            if (!string.IsNullOrWhiteSpace(_options.LuxusParceirosCallbackUrl))
            {
                var client = _httpClientFactory.CreateClient();
                using var message = new HttpRequestMessage(
                    HttpMethod.Post,
                    _options.LuxusParceirosCallbackUrl)
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
        return BuildCallbackPayload(mapping, demand);
    }

    private object BuildCallbackPayload(JsonElement mapping, object demand)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(demand));
        var root = document.RootElement;
        var observations = ReadArray(root, "observacoes")
            .Select(item => ReadString(item, "texto"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        var responsibles = ReadArray(root, "responsaveis");
        var principal = responsibles.FirstOrDefault(item =>
            ReadBoolean(item, "isPrincipal") || ReadBoolean(item, "is_principal"));
        if (principal.ValueKind == JsonValueKind.Undefined)
        {
            principal = responsibles.FirstOrDefault();
        }
        var user = ReadObject(principal, "user");
        var taskStatus = ReadString(root, "status");
        var resolution = observations.Length > 0
            ? observations[^1]
            : string.Equals(taskStatus, "concluido", StringComparison.OrdinalIgnoreCase)
                ? ReadString(root, "observacoesGerais")
                : string.Empty;
        return new
        {
            externalRequestId = mapping.GetStringOrEmpty("external_request_id"),
            demandId = ReadString(root, "id"),
            id = ReadString(root, "id"),
            protocol = ReadString(root, "protocolo"),
            status = taskStatus,
            resolution,
            observations,
            responsibleId = ReadString(user, "id"),
            responsibleName = ReadString(user, "name"),
            responsible = new
            {
                id = ReadString(user, "id"),
                name = ReadString(user, "name"),
                email = ReadString(user, "email"),
            },
            updatedAt = ReadString(root, "updatedAt"),
        };
    }

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
