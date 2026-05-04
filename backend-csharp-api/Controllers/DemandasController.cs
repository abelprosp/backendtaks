using LuxusDemandas.Api.Models;
using LuxusDemandas.Api.Security;
using LuxusDemandas.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxusDemandas.Api.Controllers;

[ApiController]
[Authorize]
[Route("demandas")]
public sealed class DemandasController : ControllerBase
{
    private readonly DemandasService _demandas;

    public DemandasController(DemandasService demandas)
    {
        _demandas = demandas;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDemandaRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _demandas.CreateAsync(User.GetUserId(), request, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("from-template/{templateId}")]
    public async Task<IActionResult> CreateFromTemplate(string templateId, [FromBody] CreateDemandaFromTemplateRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _demandas.CreateFromTemplateAsync(User.GetUserId(), templateId, request, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpPost("buscar-ia")]
    public async Task<IActionResult> BuscarIa([FromBody] BuscarIaRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _demandas.BuscarIaAsync(User.GetUserId(), request.Query, request.Scope, request.Context, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = ex.Message });
        }
    }

    [HttpGet("dashboard-kpis")]
    public async Task<IActionResult> DashboardKpis([FromQuery(Name = "avaliar_ia")] string? avaliarIa, CancellationToken cancellationToken)
    {
        if (!User.HasRoleSlug("admin"))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Apenas usuário master (administrador) pode realizar esta ação." });
        }

        return Ok(await _demandas.GetDashboardKpisAsync(string.Equals(avaliarIa, "true", StringComparison.OrdinalIgnoreCase), cancellationToken));
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] ListDemandasFiltersQuery query, CancellationToken cancellationToken)
    {
        return Ok(await _demandas.ListAsync(User.GetUserId(), query, cancellationToken));
    }

    [HttpGet("export/excel")]
    public async Task<IActionResult> Export([FromQuery] ListDemandasFiltersQuery query, CancellationToken cancellationToken)
    {
        var data = await _demandas.ExportAsync(User.GetUserId(), query, cancellationToken);
        return Ok(data);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Detail(string id, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _demandas.FindOneAsync(User.GetUserId(), id, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateDemandaRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _demandas.UpdateAsync(User.GetUserId(), id, request, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
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
            return Ok(await _demandas.RemoveAsync(User.GetUserId(), id, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
    }

    [HttpPost("{id}/observacoes")]
    public async Task<IActionResult> AddObservacao(string id, [FromBody] UpdateObservacaoRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _demandas.AddObservacaoAsync(User.GetUserId(), id, request.Texto, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPatch("{id}/observacoes/{observacaoId}")]
    public async Task<IActionResult> UpdateObservacao(string id, string observacaoId, [FromBody] UpdateObservacaoRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _demandas.UpdateObservacaoAsync(User.GetUserId(), id, observacaoId, request.Texto, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id}/anexos")]
    [RequestFormLimits(MultipartBodyLengthLimit = 100_000_000)]
    public async Task<IActionResult> AddAnexo(string id, [FromForm] IFormFile? file, [FromForm] string? nome, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { message = "Envie um arquivo (campo \"file\")" });
        }

        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);

        try
        {
            return Ok(await _demandas.AddAnexoAsync(
                User.GetUserId(),
                id,
                memory.ToArray(),
                file.FileName,
                nome,
                file.ContentType,
                file.Length,
                cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("{id}/anexos/{anexoId}/download")]
    public async Task<IActionResult> DownloadAnexo(string id, string anexoId, CancellationToken cancellationToken)
    {
        try
        {
            var download = await _demandas.GetAnexoForDownloadAsync(User.GetUserId(), id, anexoId, cancellationToken);
            return File(download.Buffer, download.MimeType, download.Filename);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
    }
}
