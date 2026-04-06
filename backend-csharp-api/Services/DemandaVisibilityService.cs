using System.Text.Json;
using LuxusDemandas.Api.Support;

namespace LuxusDemandas.Api.Services;

public sealed class DemandaVisibilityService
{
    private readonly SupabaseRestService _supabase;

    public DemandaVisibilityService(SupabaseRestService supabase)
    {
        _supabase = supabase;
    }

    public async Task<IReadOnlyList<string>> VisibleDemandaIdsAsync(string userId, CancellationToken cancellationToken)
    {
        if (await IsAdminAsync(userId, cancellationToken))
        {
            var allRows = await _supabase.QueryRowsAsync("Demanda?select=id&limit=100000", cancellationToken);
            return allRows
                .Select(row => row.GetStringOrEmpty("id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToArray();
        }

        try
        {
            var rows = await _supabase.RpcAsync<JsonElement[]>("rpc_visible_demanda_ids", new
            {
                p_user_id = userId,
            }, cancellationToken);

            return rows
                .Select(row => row.GetStringOrEmpty("demanda_id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToArray();
        }
        catch
        {
            var asCriadorTask = _supabase.QueryRowsAsync(
                $"Demanda?select=id&criador_id=eq.{Uri.EscapeDataString(userId)}",
                cancellationToken);
            var asResponsavelTask = _supabase.QueryRowsAsync(
                $"demanda_responsavel?select=demanda_id&user_id=eq.{Uri.EscapeDataString(userId)}",
                cancellationToken);
            var bySetorTask = _supabase.QueryRowsAsync(
                $"user_setor_permissao?select=setor_id&user_id=eq.{Uri.EscapeDataString(userId)}&can_view=eq.true",
                cancellationToken);

            await Task.WhenAll(asCriadorTask, asResponsavelTask, bySetorTask);

            var ids = new HashSet<string>(
                asCriadorTask.Result
                    .Select(row => row.GetStringOrEmpty("id"))
                    .Where(id => !string.IsNullOrWhiteSpace(id)));

            foreach (var item in asResponsavelTask.Result)
            {
                var demandaId = item.GetStringOrEmpty("demanda_id");
                if (!string.IsNullOrWhiteSpace(demandaId))
                {
                    ids.Add(demandaId);
                }
            }

            var setorIds = bySetorTask.Result
                .Select(row => row.GetStringOrEmpty("setor_id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToArray();
            if (setorIds.Length > 0)
            {
                var demandaSetores = await _supabase.QueryRowsAsync(
                    $"demanda_setor?select=demanda_id&setor_id=in.({string.Join(",", setorIds.Select(Uri.EscapeDataString))})",
                    cancellationToken);
                foreach (var item in demandaSetores)
                {
                    var demandaId = item.GetStringOrEmpty("demanda_id");
                    if (!string.IsNullOrWhiteSpace(demandaId))
                    {
                        ids.Add(demandaId);
                    }
                }
            }

            return ids.ToArray();
        }
    }

    public async Task<bool> CanViewDemandaAsync(string userId, string demandaId, CancellationToken cancellationToken)
    {
        if (await IsAdminAsync(userId, cancellationToken))
        {
            return true;
        }

        var ids = await VisibleDemandaIdsAsync(userId, cancellationToken);
        return ids.Contains(demandaId, StringComparer.Ordinal);
    }

    public async Task<bool> IsAdminAsync(string userId, CancellationToken cancellationToken)
    {
        var roleLinks = await _supabase.QueryRowsAsync(
            $"user_role?select=role_id&user_id=eq.{Uri.EscapeDataString(userId)}",
            cancellationToken);
        var roleIds = roleLinks
            .Select(row => row.GetStringOrEmpty("role_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToArray();
        if (roleIds.Length == 0)
        {
            return false;
        }

        var roles = await _supabase.QueryRowsAsync(
            $"Role?select=slug&id=in.({string.Join(",", roleIds.Select(Uri.EscapeDataString))})",
            cancellationToken);
        return roles.Any(role => string.Equals(role.GetStringOrEmpty("slug"), "admin", StringComparison.Ordinal));
    }
}
