using System.Text.Json;
using LuxusDemandas.Api.Support;

namespace LuxusDemandas.Api.Services;

public sealed class AuditTrailService
{
    private readonly SupabaseRestService _supabase;

    public AuditTrailService(SupabaseRestService supabase)
    {
        _supabase = supabase;
    }

    public async Task AddDemandaEventAsync(
        string demandaId,
        string? userId,
        string tipo,
        string descricao,
        object? metadata,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(demandaId) || string.IsNullOrWhiteSpace(tipo) || string.IsNullOrWhiteSpace(descricao))
        {
            return;
        }

        try
        {
            await _supabase.InsertSingleAsync("demanda_evento", new
            {
                demanda_id = demandaId,
                user_id = string.IsNullOrWhiteSpace(userId) ? null : userId,
                tipo,
                descricao,
                metadata,
            }, cancellationToken);
        }
        catch (InvalidOperationException ex) when (IsMissingAuditTable(ex))
        {
        }
    }

    public async Task AddTemplateEventAsync(
        string templateId,
        string? userId,
        string tipo,
        string descricao,
        object? metadata,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(templateId) || string.IsNullOrWhiteSpace(tipo) || string.IsNullOrWhiteSpace(descricao))
        {
            return;
        }

        try
        {
            await _supabase.InsertSingleAsync("template_evento", new
            {
                template_id = templateId,
                user_id = string.IsNullOrWhiteSpace(userId) ? null : userId,
                tipo,
                descricao,
                metadata,
            }, cancellationToken);
        }
        catch (InvalidOperationException ex) when (IsMissingAuditTable(ex))
        {
        }
    }

    public async Task<IReadOnlyList<object>> LoadDemandaEventsAsync(string demandaId, CancellationToken cancellationToken) =>
        await LoadEventsAsync("demanda_evento", "demanda_id", demandaId, cancellationToken);

    public async Task<IReadOnlyList<object>> LoadTemplateEventsAsync(string templateId, CancellationToken cancellationToken) =>
        await LoadEventsAsync("template_evento", "template_id", templateId, cancellationToken);

    private async Task<IReadOnlyList<object>> LoadEventsAsync(
        string table,
        string foreignKey,
        string entityId,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _supabase.QueryRowsAsync(
                $"{table}?select=id,user_id,tipo,descricao,metadata,created_at&{foreignKey}=eq.{Uri.EscapeDataString(entityId)}&order=created_at.desc",
                cancellationToken);

            var userIds = rows
                .Select(row => row.GetNullableString("user_id"))
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

            return rows.Select(row =>
            {
                var userId = row.GetNullableString("user_id") ?? string.Empty;
                usersById.TryGetValue(userId, out var user);
                return (object)new
                {
                    id = row.GetNullableString("id"),
                    tipo = row.GetStringOrEmpty("tipo"),
                    descricao = row.GetStringOrEmpty("descricao"),
                    createdAt = NormalizeDate(row.GetNullableString("created_at")),
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
        catch (InvalidOperationException ex) when (IsMissingAuditTable(ex))
        {
            return Array.Empty<object>();
        }
    }

    private static bool IsMissingAuditTable(InvalidOperationException ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("PGRST205", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Could not find the table", StringComparison.OrdinalIgnoreCase) ||
               (message.Contains("relation", StringComparison.OrdinalIgnoreCase) &&
                message.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParse(value, out var parsed) ? parsed.ToUniversalTime().ToString("O") : value;
    }
}
