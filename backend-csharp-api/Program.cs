using System.Security.Claims;
using LuxusDemandas.Api.Configuration;
using LuxusDemandas.Api.Security;
using LuxusDemandas.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();

builder.Services.Configure<AppOptions>(options =>
{
    options.FrontendUrl = builder.Configuration["FRONTEND_URL"] ?? string.Empty;
    options.FrontendOrigin = builder.Configuration["FRONTEND_ORIGIN"] ?? string.Empty;
    options.SupabaseUrl = builder.Configuration["SUPABASE_URL"] ?? string.Empty;
    options.SupabaseServiceRoleKey = builder.Configuration["SUPABASE_SERVICE_ROLE_KEY"] ?? string.Empty;
    options.SupabaseAnonKey = builder.Configuration["SUPABASE_ANON_KEY"] ?? string.Empty;
    options.JwtSecret = builder.Configuration["JWT_SECRET"] ?? "luxus-secret-change-me";
    options.JwtRefreshSecret = builder.Configuration["JWT_REFRESH_SECRET"] ?? options.JwtSecret;
    options.JwtExpiresIn = builder.Configuration["JWT_EXPIRES_IN"] ?? "15m";
    options.RefreshExpiresIn = builder.Configuration["REFRESH_EXPIRES_IN"] ?? "7d";
    options.OpenAiApiKey = builder.Configuration["OPENAI_API_KEY"] ?? string.Empty;
    options.SupabaseStorageBucket = builder.Configuration["SUPABASE_STORAGE_BUCKET"] ?? "demandas-anexos";
    options.NodeEnv = builder.Configuration["NODE_ENV"] ?? "development";
});

builder.Services.AddHttpClient();
builder.Services.AddHttpClient<SupabaseRestService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<UsersService>();
builder.Services.AddScoped<SetoresService>();
builder.Services.AddScoped<ClientesService>();
builder.Services.AddScoped<AuditTrailService>();
builder.Services.AddScoped<TemplatesService>();
builder.Services.AddScoped<DemandaVisibilityService>();
builder.Services.AddScoped<DemandasService>();
builder.Services.AddScoped<MessageReviewService>();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
    {
        var origins = new[]
            {
                builder.Configuration["FRONTEND_URL"],
                builder.Configuration["FRONTEND_ORIGIN"],
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim().TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (origins.Length == 0)
        {
            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
            return;
        }

        policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
    });
});

var jwtSecret = builder.Configuration["JWT_SECRET"] ?? "luxus-secret-change-me";
var accessKey = JwtKeyHelper.NormalizeSecretToKey(jwtSecret);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(accessKey),
            NameClaimType = ClaimTypes.Name,
            RoleClaimType = ClaimTypes.Role,
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.MapGet("/", () => Results.Redirect("/health"));

app.UseCors("frontend");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
