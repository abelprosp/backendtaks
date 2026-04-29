using LuxusDemandas.Api.Models;
using LuxusDemandas.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxusDemandas.Api.Controllers;

[ApiController]
[Route("auth")]
public sealed class AuthController : ControllerBase
{
    private readonly AuthService _authService;

    public AuthController(AuthService authService)
    {
        _authService = authService;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _authService.LoginAsync(request, cancellationToken));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _authService.RefreshAsync(request, cancellationToken));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
    }

    [Authorize]
    [HttpPost("me")]
    public IActionResult Me()
    {
        return Ok(AuthService.MapAuthenticatedUser(User));
    }

    [Authorize]
    [HttpPut("me")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var user = AuthService.MapAuthenticatedUser(User);
            return Ok(await _authService.UpdateProfileAsync(user.Id, request, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [Authorize]
    [HttpGet("bootstrap")]
    public async Task<IActionResult> Bootstrap(
        [FromQuery] bool includeSetores = false,
        [FromQuery] bool includeClientes = false,
        [FromQuery] bool allClientes = false,
        [FromQuery] bool includeUsers = false,
        [FromQuery] bool fullUsers = false,
        [FromQuery] bool includeRoles = false,
        CancellationToken cancellationToken = default)
    {
        var user = AuthService.MapAuthenticatedUser(User);
        var query = new BootstrapQuery(includeSetores, includeClientes, allClientes, includeUsers, fullUsers, includeRoles);
        return Ok(await _authService.BootstrapAsync(user, query, cancellationToken));
    }
}
