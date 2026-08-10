using LuxusDemandas.Api.Configuration;
using LuxusDemandas.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LuxusDemandas.Api.Controllers;

[ApiController]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    private readonly AppOptions _options;
    private readonly SupabaseRestService _supabase;
    private readonly ILogger<HealthController> _logger;

    public HealthController(
        IOptions<AppOptions> options,
        SupabaseRestService supabase,
        ILogger<HealthController> logger)
    {
        _options = options.Value;
        _supabase = supabase;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new
        {
            status = "ok",
            service = "luxus-demandas-backend-csharp",
            name = "LUXUS DEMANDAS API C#",
            environment = _options.NodeEnv,
            timestamp = DateTimeOffset.UtcNow,
        });
    }

    [HttpGet("ready")]
    public async Task<IActionResult> Ready(CancellationToken cancellationToken)
    {
        try
        {
            _ = await _supabase.QueryRowsAsync(
                "luxus_parceiros_demanda?select=id&limit=1",
                cancellationToken);
            return Ok(new
            {
                status = "ok",
                supabase = "ok",
                timestamp = DateTimeOffset.UtcNow,
            });
        }
        catch (Exception error)
        {
            _logger.LogError(
                error,
                "Healthcheck do Supabase falhou. TraceId: {TraceId}",
                HttpContext.TraceIdentifier);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "error",
                supabase = "unavailable",
                traceId = HttpContext.TraceIdentifier,
                timestamp = DateTimeOffset.UtcNow,
            });
        }
    }
}
