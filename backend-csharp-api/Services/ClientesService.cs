using LuxusDemandas.Api.Models;
using LuxusDemandas.Api.Support;

namespace LuxusDemandas.Api.Services;

public sealed class ClientesService
{
    private readonly SupabaseRestService _supabase;

    public ClientesService(SupabaseRestService supabase)
    {
        _supabase = supabase;
    }

    public Task<IReadOnlyList<ClienteDto>> ListAsync(bool activeOnly, CancellationToken cancellationToken) =>
        _supabase.ListClientesAsync(activeOnly, cancellationToken);

    public async Task<object> CreateAsync(CreateClienteRequest request, CancellationToken cancellationToken)
    {
        var created = await _supabase.InsertSingleAsync("Cliente", new
        {
            name = request.Name.Trim(),
            active = request.Active ?? true,
        }, cancellationToken);

        return new
        {
            id = created.GetStringOrEmpty("id"),
            name = created.GetStringOrEmpty("name"),
            active = created.GetBooleanOrDefault("active"),
        };
    }

    public async Task<object> UpdateAsync(string id, UpdateClienteRequest request, CancellationToken cancellationToken)
    {
        var current = await _supabase.QuerySingleAsync(
            $"Cliente?select=id,name,active&id=eq.{Uri.EscapeDataString(id)}&limit=1",
            cancellationToken);
        if (current is null)
        {
            throw new KeyNotFoundException("Cliente não encontrado");
        }

        var updates = new Dictionary<string, object?>();
        if (request.Name is not null)
        {
            updates["name"] = request.Name.Trim();
        }

        if (request.Active.HasValue)
        {
            updates["active"] = request.Active.Value;
        }

        if (updates.Count == 0)
        {
            return new
            {
                id = current.Value.GetStringOrEmpty("id"),
                name = current.Value.GetStringOrEmpty("name"),
                active = current.Value.GetBooleanOrDefault("active"),
            };
        }

        var updated = await _supabase.UpdateSingleAsync(
            "Cliente",
            $"id=eq.{Uri.EscapeDataString(id)}",
            updates,
            cancellationToken);

        return new
        {
            id = updated?.GetStringOrEmpty("id") ?? current.Value.GetStringOrEmpty("id"),
            name = updated?.GetStringOrEmpty("name") ?? current.Value.GetStringOrEmpty("name"),
            active = updated?.GetBooleanOrDefault("active") ?? current.Value.GetBooleanOrDefault("active"),
        };
    }

    public async Task<object> RemoveAsync(string id, CancellationToken cancellationToken)
    {
        var current = await _supabase.QuerySingleAsync(
            $"Cliente?select=id&id=eq.{Uri.EscapeDataString(id)}&limit=1",
            cancellationToken);
        if (current is null)
        {
            throw new KeyNotFoundException("Cliente não encontrado");
        }

        await _supabase.DeleteAsync("Cliente", $"id=eq.{Uri.EscapeDataString(id)}", cancellationToken);
        return new { id };
    }
}
