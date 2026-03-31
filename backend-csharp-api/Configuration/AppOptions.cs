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
    public string OpenAiApiKey { get; set; } = string.Empty;
    public string SupabaseStorageBucket { get; set; } = "demandas-anexos";
    public string NodeEnv { get; set; } = "development";
}
