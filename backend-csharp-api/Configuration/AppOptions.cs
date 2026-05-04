namespace LuxusDemandas.Api.Configuration;

public sealed class AppOptions
{
    public string FrontendUrl { get; set; } = string.Empty;
    public string FrontendOrigin { get; set; } = string.Empty;
    public string SupabaseUrl { get; set; } = string.Empty;
    public string SupabaseServiceRoleKey { get; set; } = string.Empty;
    public string SupabaseAnonKey { get; set; } = string.Empty;
    public string JwtSecret { get; set; } = string.Empty;
    public string JwtRefreshSecret { get; set; } = string.Empty;
    public string JwtExpiresIn { get; set; } = "15m";
    public string RefreshExpiresIn { get; set; } = "7d";
    public string PasswordAccessTokenExpiresIn { get; set; } = "24h";
    public string LegacyImportedPasswordHash { get; set; } = string.Empty;
    public string OpenAiApiKey { get; set; } = string.Empty;
    public string SupabaseStorageBucket { get; set; } = "demandas-anexos";
    public string LegacyBaseUrl { get; set; } = "http://luxusweb.com.br";
    public string LegacyEmail { get; set; } = string.Empty;
    public string LegacyPassword { get; set; } = string.Empty;
    public bool PreferLegacyAttachments { get; set; } = false;
    public bool RequireLegacyAttachments { get; set; } = false;
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public string SmtpUsername { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public string SmtpFromEmail { get; set; } = string.Empty;
    public string SmtpFromName { get; set; } = "Luxus Demandas";
    public bool SmtpUseSsl { get; set; } = true;
    public string NodeEnv { get; set; } = "development";
}
