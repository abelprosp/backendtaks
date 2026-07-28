using LuxusDemandas.Api.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LuxusDemandas.Api.Controllers;

[ApiController]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    private readonly AppOptions _options;

    public HealthController(IOptions<AppOptions> options)
    {
        _options = options.Value;
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
}
