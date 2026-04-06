using System.Text.Json;
using LuxusDemandas.Api.Support;

namespace LuxusDemandas.Api.Services;

public sealed class DemandaVisibilityService
{
    private const string PrivateDemandMasterEmail = "rafael@luxustelefonia.com.br";
    private readonly SupabaseRestService _supabase;

    public DemandaVisibilityService(SupabaseRestService supabase)
    {
        _supabase = supabase;
    }

    public async Task<IReadOnlyList<string>> VisibleDemandaIdsAsync(string userId, CancellationToken cancellationToken)
    {
        if (await IsAdminAsync(userId, cancellationToken))
        {
            var allRows = await _supabase.QueryAllRowsAsync("Demanda?select=id,is_privada,private_owner_user_id", cancellationToken);
            return allRows
                .Where(row => IsDemandVisibleToUser(row, userId))
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

            var ids = rows
                .Select(row => row.GetStringOrEmpty("demanda_id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToArray();

            return await FilterPrivateDemandIdsAsync(userId, ids, cancellationToken);
        }
        catch
        {
            var asCriadorTask = _supabase.QueryAllRowsAsync(
                $"Demanda?select=id&criador_id=eq.{Uri.EscapeDataString(userId)}",
                cancellationToken);
            var asResponsavelTask = _supabase.QueryAllRowsAsync(
                $"demanda_responsavel?select=demanda_id&user_id=eq.{Uri.EscapeDataString(userId)}",
                cancellationToken);
            var bySetorTask = _supabase.QueryAllRowsAsync(
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
                var demandaSetores = await _supabase.QueryAllRowsAsync(
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

            return await FilterPrivateDemandIdsAsync(userId, ids, cancellationToken);
        }
    }

    public async Task<bool> CanViewDemandaAsync(string userId, string demandaId, CancellationToken cancellationToken)
    {
        var demanda = await _supabase.QuerySingleAsync(
            $"Demanda?select=id,is_privada,private_owner_user_id&id=eq.{Uri.EscapeDataString(demandaId)}&limit=1",
            cancellationToken);
        if (demanda is null)
        {
            return false;
        }

        if (!IsDemandVisibleToUser(demanda.Value, userId))
        {
            return false;
        }

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

    public async Task<bool> CanManagePrivateDemandasAsync(string userId, CancellationToken cancellationToken)
    {
        var user = await _supabase.FindUserByIdAsync(userId, cancellationToken);
        return user is not null
               && string.Equals(user.Email, PrivateDemandMasterEmail, StringComparison.OrdinalIgnoreCase)
               && await IsAdminAsync(userId, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> FilterPrivateDemandIdsAsync(
        string userId,
        IEnumerable<string> ids,
        CancellationToken cancellationToken)
    {
        var idList = ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (idList.Length == 0)
        {
            return Array.Empty<string>();
        }

        var rows = await _supabase.QueryAllRowsAsync(
            $"Demanda?select=id,is_privada,private_owner_user_id&id=in.({string.Join(",", idList.Select(Uri.EscapeDataString))})",
            cancellationToken);

        return rows
            .Where(row => IsDemandVisibleToUser(row, userId))
            .Select(row => row.GetStringOrEmpty("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToArray();
    }

    private static bool IsDemandVisibleToUser(JsonElement row, string userId)
    {
        if (!row.GetBooleanOrDefault("is_privada"))
        {
            return true;
        }

        return string.Equals(
            row.GetNullableString("private_owner_user_id"),
            userId,
            StringComparison.Ordinal);
    }
}
