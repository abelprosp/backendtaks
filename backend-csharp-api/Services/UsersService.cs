using System.Text.Json;
using BCrypt.Net;
using LuxusDemandas.Api.Models;
using LuxusDemandas.Api.Support;

namespace LuxusDemandas.Api.Services;

public sealed class UsersService
{
    private readonly SupabaseRestService _supabase;

    public UsersService(SupabaseRestService supabase)
    {
        _supabase = supabase;
    }

    public Task<IReadOnlyList<UserDropdownDto>> ListForDropdownAsync(CancellationToken cancellationToken) =>
        _supabase.ListUsersForDropdownAsync(cancellationToken);

    public Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken cancellationToken) =>
        _supabase.ListRolesAsync(cancellationToken);

    public Task<IReadOnlyList<UserListDto>> ListAllAsync(CancellationToken cancellationToken) =>
        _supabase.ListAllUsersAsync(cancellationToken);

    public async Task<object> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var normalizedEmail = request.Email.ToLowerInvariant().Trim();
        var existing = await _supabase.QuerySingleAsync(
            $"User?select=id&email=eq.{Uri.EscapeDataString(normalizedEmail)}&limit=1",
            cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException("E-mail já cadastrado");
        }

        var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, 10);
        var created = await _supabase.InsertSingleAsync("User", new
        {
            email = normalizedEmail,
            password_hash = passwordHash,
            name = request.Name.Trim(),
        }, cancellationToken);

        var userId = created.GetStringOrEmpty("id");
        if (request.RoleIds?.Count > 0)
        {
            await _supabase.InsertManyAsync("user_role",
                request.RoleIds.Select(roleId => new { user_id = userId, role_id = roleId }),
                cancellationToken);
        }

        var roles = await LoadRoleLinksAsync(userId, cancellationToken);
        return new
        {
            id = created.GetStringOrEmpty("id"),
            email = created.GetStringOrEmpty("email"),
            name = created.GetStringOrEmpty("name"),
            active = created.GetBooleanOrDefault("active"),
            createdAt = created.GetNullableString("created_at"),
            updatedAt = created.GetNullableString("updated_at"),
            roles,
        };
    }

    public async Task<object> UpdateAsync(string id, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        var current = await _supabase.QuerySingleAsync(
            $"User?select=id,email,name,active,created_at&id=eq.{Uri.EscapeDataString(id)}&limit=1",
            cancellationToken);
        if (current is null)
        {
            throw new KeyNotFoundException("Usuário não encontrado");
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

        if (updates.Count > 0)
        {
            await _supabase.UpdateSingleAsync(
                "User",
                $"id=eq.{Uri.EscapeDataString(id)}",
                updates,
                cancellationToken);
        }

        if (request.RoleIds is not null)
        {
            await _supabase.DeleteAsync("user_role", $"user_id=eq.{Uri.EscapeDataString(id)}", cancellationToken);
            if (request.RoleIds.Count > 0)
            {
                await _supabase.InsertManyAsync("user_role",
                    request.RoleIds.Select(roleId => new { user_id = id, role_id = roleId }),
                    cancellationToken);
            }
        }

        var roles = await LoadRolesAsync(id, cancellationToken);
        return new
        {
            id = current.Value.GetStringOrEmpty("id"),
            name = request.Name?.Trim() ?? current.Value.GetStringOrEmpty("name"),
            email = current.Value.GetStringOrEmpty("email"),
            active = request.Active ?? current.Value.GetBooleanOrDefault("active"),
            created_at = current.Value.GetNullableString("created_at"),
            roles,
        };
    }

    public async Task<object> RemoveAsync(string id, CancellationToken cancellationToken)
    {
        var current = await _supabase.QuerySingleAsync(
            $"User?select=id,email&id=eq.{Uri.EscapeDataString(id)}&limit=1",
            cancellationToken);
        if (current is null)
        {
            throw new KeyNotFoundException("Usuário não encontrado");
        }

        await _supabase.UpdateSingleAsync(
            "User",
            $"id=eq.{Uri.EscapeDataString(id)}",
            new { active = false },
            cancellationToken);

        return new { id };
    }

    private async Task<IReadOnlyList<RoleLink>> LoadRoleLinksAsync(string userId, CancellationToken cancellationToken)
    {
        var roles = await LoadRolesAsync(userId, cancellationToken);
        return roles.Select(role => new RoleLink(role)).ToList();
    }

    private async Task<IReadOnlyList<RoleDto>> LoadRolesAsync(string userId, CancellationToken cancellationToken)
    {
        var links = await _supabase.QueryRowsAsync(
            $"user_role?select=role_id&user_id=eq.{Uri.EscapeDataString(userId)}",
            cancellationToken);
        var roleIds = links
            .Select(link => link.GetStringOrEmpty("role_id"))
            .Where(roleId => !string.IsNullOrWhiteSpace(roleId))
            .Distinct()
            .ToArray();
        if (roleIds.Length == 0)
        {
            return Array.Empty<RoleDto>();
        }

        var roleRows = await _supabase.QueryRowsAsync(
            $"Role?select=id,name,slug&id=in.({string.Join(",", roleIds.Select(Uri.EscapeDataString))})",
            cancellationToken);

        return roleRows
            .Select(role => new RoleDto(
                role.GetStringOrEmpty("id"),
                role.GetStringOrEmpty("name"),
                role.GetStringOrEmpty("slug")))
            .ToList();
    }
}
