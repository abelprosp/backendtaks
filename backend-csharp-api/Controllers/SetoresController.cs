using LuxusDemandas.Api.Services;
using LuxusDemandas.Api.Models;
using LuxusDemandas.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxusDemandas.Api.Controllers;

[ApiController]
[Authorize]
[Route("setores")]
public sealed class SetoresController : ControllerBase
{
    private readonly SetoresService _setores;

    public SetoresController(SetoresService setores)
    {
        _setores = setores;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        return Ok(await _setores.ListAsync(cancellationToken));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSetorRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _setores.CreateAsync(request, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateSetorRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _setores.UpdateAsync(id, request, cancellationToken));
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
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Apenas usuários ADM podem excluir setores." });
        }

        try
        {
            return Ok(await _setores.RemoveAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }
}
