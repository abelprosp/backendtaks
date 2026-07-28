namespace LuxusDemandas.Api.Security;

public static class PasswordAccessDefaults
{
    public const string LegacyPlaceholderDomain = "luxus.local";
    public const string DefaultImportedPasswordHash = "$2a$06$T/2vNgiBvzUe1c0GvDZFyetzLYmz37qm73Yh2GBJo0r4hypfp/6BG";

    public static bool IsPlaceholderHash(string? passwordHash, string? configuredImportedHash = null)
    {
        var importedHash = string.IsNullOrWhiteSpace(configuredImportedHash)
            ? DefaultImportedPasswordHash
            : configuredImportedHash.Trim();

        return !string.IsNullOrWhiteSpace(passwordHash)
               && string.Equals(passwordHash.Trim(), importedHash, StringComparison.Ordinal);
    }

    public static bool HasDeliverableEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email)
        && !email.Trim().EndsWith($"@{LegacyPlaceholderDomain}", StringComparison.OrdinalIgnoreCase);
}
