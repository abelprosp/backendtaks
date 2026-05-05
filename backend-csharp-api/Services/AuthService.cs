using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BCrypt.Net;
using LuxusDemandas.Api.Configuration;
using LuxusDemandas.Api.Models;
using LuxusDemandas.Api.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace LuxusDemandas.Api.Services;

public sealed class AuthService
{
    private readonly SupabaseRestService _supabase;
    private readonly AppOptions _options;

    public AuthService(SupabaseRestService supabase, IOptions<AppOptions> options)
    {
        _supabase = supabase;
        _options = options.Value;
    }

    public async Task<TokenResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var user = await _supabase.FindUserByEmailAsync(request.Email, cancellationToken);
        if (user is null || !user.Active)
        {
            throw new UnauthorizedAccessException("Credenciais inválidas");
        }

        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            throw new UnauthorizedAccessException("Credenciais inválidas");
        }

        return BuildTokenResponse(user);
    }

    public async Task<TokenResponse> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            throw new UnauthorizedAccessException("Refresh token ausente");
        }

        var tokenHandler = new JwtSecurityTokenHandler();
        var key = JwtKeyHelper.NormalizeSecretToKey(_options.JwtRefreshSecret);
        ClaimsPrincipal principal;
        try
        {
            principal = tokenHandler.ValidateToken(request.RefreshToken, new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),
            }, out _);
        }
        catch
        {
            throw new UnauthorizedAccessException("Refresh token inválido ou expirado");
        }

        var type = principal.FindFirstValue("type");
        if (!string.Equals(type, "refresh", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Token inválido para renovação");
        }

        var userId = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
                     ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new UnauthorizedAccessException("Token inválido");
        }

        var user = await _supabase.FindUserByIdAsync(userId, cancellationToken);
        if (user is null || !user.Active)
        {
            throw new UnauthorizedAccessException("Usuário inválido");
        }

        return BuildTokenResponse(user);
    }

    public async Task<object> BootstrapAsync(AuthUser user, BootstrapQuery query, CancellationToken cancellationToken)
    {
        var setoresTask = query.IncludeSetores ? _supabase.ListSetoresAsync(cancellationToken) : Task.FromResult<IReadOnlyList<SetorDto>>([]);
        var clientesTask = query.IncludeClientes ? _supabase.ListClientesAsync(!query.AllClientes, cancellationToken) : Task.FromResult<IReadOnlyList<ClienteDto>>([]);
        var usersTask = query.IncludeUsers
            ? (query.FullUsers
                ? _supabase.ListAllUsersAsync(cancellationToken).ContinueWith(t => (object)t.Result, cancellationToken)
                : _supabase.ListUsersForDropdownAsync(cancellationToken).ContinueWith(t => (object)t.Result, cancellationToken))
            : Task.FromResult<object>(Array.Empty<object>());
        var rolesTask = query.IncludeRoles ? _supabase.ListRolesAsync(cancellationToken).ContinueWith(t => (object)t.Result, cancellationToken) : Task.FromResult<object>(Array.Empty<object>());

        await Task.WhenAll(setoresTask, clientesTask, usersTask, rolesTask);

        return new
        {
            user,
            setores = query.IncludeSetores ? setoresTask.Result : null,
            clientes = query.IncludeClientes ? clientesTask.Result : null,
            users = query.IncludeUsers ? usersTask.Result : null,
            roles = query.IncludeRoles ? rolesTask.Result : null,
        };
    }

    public async Task<AuthUser> UpdateProfileAsync(string userId, UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        var currentUser = await _supabase.FindUserByIdAsync(userId, cancellationToken);
        if (currentUser is null || !currentUser.Active)
        {
            throw new KeyNotFoundException("Usuário não encontrado.");
        }

        var currentName = currentUser.Name.Trim();
        var currentEmail = currentUser.Email.Trim();
        var normalizedName = request.Name?.Trim();
        var normalizedEmail = request.Email?.Trim().ToLowerInvariant();
        var hasNameChange = !string.IsNullOrWhiteSpace(normalizedName) && !string.Equals(normalizedName, currentName, StringComparison.Ordinal);
        var hasEmailChange = !string.IsNullOrWhiteSpace(normalizedEmail) && !string.Equals(normalizedEmail, currentEmail, StringComparison.OrdinalIgnoreCase);
        var hasPasswordChange = !string.IsNullOrWhiteSpace(request.NewPassword);
        var requiresCurrentPassword = hasEmailChange || hasPasswordChange;

        if (!hasNameChange && !hasEmailChange && !hasPasswordChange)
        {
            return new AuthUser(currentUser.Id, currentUser.Email, currentUser.Name, currentUser.Roles);
        }

        if (requiresCurrentPassword)
        {
            if (string.IsNullOrWhiteSpace(request.CurrentPassword))
            {
                throw new InvalidOperationException("Informe a senha atual para alterar e-mail ou senha.");
            }

            if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, currentUser.PasswordHash))
            {
                throw new InvalidOperationException("Senha atual inválida.");
            }
        }

        if (hasEmailChange)
        {
            var existingUser = await _supabase.QuerySingleAsync(
                $"User?select=id&email=eq.{Uri.EscapeDataString(normalizedEmail!)}&id=neq.{Uri.EscapeDataString(userId)}&limit=1",
                cancellationToken);
            if (existingUser is not null)
            {
                throw new InvalidOperationException("Esse e-mail já está em uso por outro usuário.");
            }
        }

        var updates = new Dictionary<string, object?>();
        if (hasNameChange)
        {
            updates["name"] = normalizedName;
        }

        if (hasEmailChange)
        {
            updates["email"] = normalizedEmail;
        }

        if (hasPasswordChange)
        {
            if (request.NewPassword!.Trim().Length < 6)
            {
                throw new InvalidOperationException("A nova senha precisa ter ao menos 6 caracteres.");
            }

            if (BCrypt.Net.BCrypt.Verify(request.NewPassword, currentUser.PasswordHash))
            {
                throw new InvalidOperationException("A nova senha precisa ser diferente da senha atual.");
            }

            updates["password_hash"] = BCrypt.Net.BCrypt.HashPassword(request.NewPassword.Trim(), 10);
        }

        _ = await _supabase.UpdateSingleAsync(
            "User",
            $"id=eq.{Uri.EscapeDataString(userId)}",
            updates,
            cancellationToken);

        var refreshedUser = await _supabase.FindUserByIdAsync(userId, cancellationToken)
                            ?? throw new InvalidOperationException("Não foi possível atualizar o perfil.");

        return new AuthUser(refreshedUser.Id, refreshedUser.Email, refreshedUser.Name, refreshedUser.Roles);
    }

    public async Task<object> GeneratePasswordAccessLinkAsync(string targetUserId, string actorUserId, CancellationToken cancellationToken)
    {
        _ = actorUserId;
        var user = await _supabase.FindUserByIdAsync(targetUserId, cancellationToken);
        if (user is null)
        {
            throw new KeyNotFoundException("Usuário não encontrado.");
        }
        if (!user.Active)
        {
            throw new InvalidOperationException("Usuário inativo.");
        }

        var purpose = user.NeedsPasswordSetup ? "first_access" : "reset_password";
        var expiresAt = DateTimeOffset.UtcNow.Add(ParseDuration(_options.PasswordAccessTokenExpiresIn));
        var payload = new Dictionary<string, object?>
        {
            ["sub"] = user.Id,
            ["email"] = user.Email,
            ["purpose"] = purpose,
            ["exp"] = expiresAt.ToUnixTimeSeconds(),
            ["ph"] = FingerprintPasswordHash(user.PasswordHash),
        };
        var encodedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        var token = $"{encodedPayload}.{SignPasswordAccessPayload(encodedPayload)}";
        var baseUrl = (_options.FrontendUrl.Length > 0 ? _options.FrontendUrl : _options.FrontendOrigin).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = "https://luxustasks-omega.vercel.app";
        }

        return new
        {
            url = $"{baseUrl}/login/redefinir-senha?token={Uri.EscapeDataString(token)}",
            expiresAt = expiresAt.UtcDateTime.ToString("O"),
            purpose,
            deliveryMethod = "manual",
        };
    }

    public static AuthUser MapAuthenticatedUser(ClaimsPrincipal principal)
    {
        var roles = principal.FindAll("role_slug")
            .Select(claim => new RoleLink(new RoleDto(
                principal.FindFirstValue($"role_id:{claim.Value}") ?? string.Empty,
                principal.FindFirstValue($"role_name:{claim.Value}") ?? claim.Value,
                claim.Value)))
            .ToList();

        return new AuthUser(
            principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
            principal.FindFirstValue(JwtRegisteredClaimNames.Email) ?? principal.FindFirstValue(ClaimTypes.Email) ?? string.Empty,
            principal.FindFirstValue("name") ?? principal.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
            roles);
    }

    private TokenResponse BuildTokenResponse(TokenUser user)
    {
        var accessToken = BuildJwt(user, _options.JwtSecret, _options.JwtExpiresIn, "access");
        var refreshToken = BuildJwt(user, _options.JwtRefreshSecret, _options.RefreshExpiresIn, "refresh");

        return new TokenResponse(
            accessToken,
            refreshToken,
            _options.JwtExpiresIn,
            _options.RefreshExpiresIn,
            new AuthUser(user.Id, user.Email, user.Name, user.Roles));
    }

    private string BuildJwt(TokenUser user, string secret, string expiresIn, string type)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("name", user.Name),
            new("type", type),
        };

        foreach (var link in user.Roles)
        {
            claims.Add(new Claim("role_slug", link.Role.Slug));
            claims.Add(new Claim($"role_id:{link.Role.Slug}", link.Role.Id));
            claims.Add(new Claim($"role_name:{link.Role.Slug}", link.Role.Name));
        }

        var key = new SymmetricSecurityKey(JwtKeyHelper.NormalizeSecretToKey(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expiresAt = DateTime.UtcNow.Add(ParseDuration(expiresIn));
        var token = new JwtSecurityToken(
            claims: claims,
            expires: expiresAt,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private string SignPasswordAccessPayload(string encodedPayload)
    {
        var secret = !string.IsNullOrWhiteSpace(_options.JwtSecret)
            ? _options.JwtSecret
            : (!string.IsNullOrWhiteSpace(_options.SupabaseServiceRoleKey)
                ? _options.SupabaseServiceRoleKey
                : "luxus-password-access-fallback");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(encodedPayload)));
    }

    private static string FingerprintPasswordHash(string? passwordHash)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static TimeSpan ParseDuration(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TimeSpan.FromMinutes(15);
        }

        if (raw.EndsWith('m') && int.TryParse(raw[..^1], out var minutes))
        {
            return TimeSpan.FromMinutes(minutes);
        }

        if (raw.EndsWith('h') && int.TryParse(raw[..^1], out var hours))
        {
            return TimeSpan.FromHours(hours);
        }

        if (raw.EndsWith('d') && int.TryParse(raw[..^1], out var days))
        {
            return TimeSpan.FromDays(days);
        }

        return TimeSpan.FromMinutes(15);
    }
}
