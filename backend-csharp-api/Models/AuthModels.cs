namespace LuxusDemandas.Api.Models;

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record BootstrapQuery(
    bool IncludeSetores,
    bool IncludeClientes,
    bool AllClientes,
    bool IncludeUsers,
    bool FullUsers,
    bool IncludeRoles);

public sealed record JwtPayload(
    string Sub,
    string Email,
    string? Name,
    IReadOnlyList<RoleLink>? Roles,
    string? Type);

public sealed record TokenUser(
    string Id,
    string Email,
    string Name,
    bool Active,
    string PasswordHash,
    IReadOnlyList<RoleLink> Roles);

public sealed record TokenResponse(
    string AccessToken,
    string RefreshToken,
    string ExpiresIn,
    string RefreshExpiresIn,
    AuthUser User);

public sealed record AuthUser(
    string Id,
    string Email,
    string Name,
    IReadOnlyList<RoleLink> Roles);

public sealed record RoleLink(RoleDto Role);

public sealed record RoleDto(string Id, string Name, string Slug);

public sealed record SetorDto(string Id, string Name, string Slug);

public sealed record ClienteDto(string Id, string Name, bool Active, string? TipoPessoa, string? Documento);

public sealed record UserDropdownDto(string Id, string Name, string Email);

public sealed record UserListDto(string Id, string Name, string Email, bool Active, string? CreatedAt, IReadOnlyList<RoleDto> Roles);
