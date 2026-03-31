using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace LuxusDemandas.Api.Security;

public static class UserClaimsExtensions
{
    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? string.Empty;

    public static bool HasRoleSlug(this ClaimsPrincipal user, string slug) =>
        user.FindAll("role_slug").Any(claim => string.Equals(claim.Value, slug, StringComparison.OrdinalIgnoreCase));
}
