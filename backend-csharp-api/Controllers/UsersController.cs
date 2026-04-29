using LuxusDemandas.Api.Services;
using LuxusDemandas.Api.Models;
using LuxusDemandas.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxusDemandas.Api.Controllers;

[ApiController]
[Authorize]
[Route("users")]
public sealed class UsersController : ControllerBase
{
    private readonly UsersService _users;
    private readonly AuthService _authService;

    public UsersController(UsersService users, AuthService authService)
    {
        _users = users;
        _authService = authService;
    }

    [HttpGet("dropdown")]
    public async Task<IActionResult> Dropdown(CancellationToken cancellationToken)
    {
        return Ok(await _users.ListForDropdownAsync(cancellationToken));
    }

    [HttpGet("roles")]
    public async Task<IActionResult> Roles(CancellationToken cancellationToken)
    {
        return Ok(await _users.ListRolesAsync(cancellationToken));
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        return Ok(await _users.ListAllAsync(cancellationToken));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request, CancellationToken cancellationToken)
    {
        if (!User.HasRoleSlug("admin"))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Apenas usuário master (administrador) pode realizar esta ação." });
        }

        try
        {
            return Ok(await _users.CreateAsync(request, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateUserRequest request, CancellationToken cancellationToken)
    {
        if (!User.HasRoleSlug("admin"))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Apenas usuário master (administrador) pode realizar esta ação." });
        }

        try
        {
            return Ok(await _users.UpdateAsync(id, request, cancellationToken));
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

    [HttpPost("{id}/password-link")]
    public async Task<IActionResult> GeneratePasswordAccessLink(string id, CancellationToken cancellationToken)
    {
        if (!User.HasRoleSlug("admin"))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Apenas usuario master (administrador) pode realizar esta acao." });
        }

        try
        {
            var actor = AuthService.MapAuthenticatedUser(User);
            return Ok(await _authService.GeneratePasswordAccessLinkAsync(id, actor.Id, cancellationToken));
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

    [HttpDelete("{id}")]
    public async Task<IActionResult> Remove(string id, CancellationToken cancellationToken)
    {
        if (!User.HasRoleSlug("admin"))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Apenas usuário master (administrador) pode realizar esta ação." });
        }

        try
        {
            return Ok(await _users.RemoveAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }
}
