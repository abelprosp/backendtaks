using LuxusDemandas.Api.Services;
using LuxusDemandas.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxusDemandas.Api.Controllers;

[ApiController]
[Authorize]
[Route("clientes")]
public sealed class ClientesController : ControllerBase
{
    private readonly ClientesService _clientes;

    public ClientesController(ClientesService clientes)
    {
        _clientes = clientes;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? all = null, CancellationToken cancellationToken = default)
    {
        var activeOnly = !string.Equals(all, "true", StringComparison.OrdinalIgnoreCase);
        return Ok(await _clientes.ListAsync(activeOnly, cancellationToken));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateClienteRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _clientes.CreateAsync(request, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateClienteRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _clientes.UpdateAsync(id, request, cancellationToken));
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
        try
        {
            return Ok(await _clientes.RemoveAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }
}
