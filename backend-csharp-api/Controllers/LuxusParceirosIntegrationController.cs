using LuxusDemandas.Api.Models;
using LuxusDemandas.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxusDemandas.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("integrations/luxus-parceiros")]
public sealed class LuxusParceirosIntegrationController : ControllerBase
{
    private readonly LuxusParceirosIntegrationService _integration;

    public LuxusParceirosIntegrationController(LuxusParceirosIntegrationService integration)
    {
        _integration = integration;
    }

    [HttpGet("responsaveis")]
    public async Task<IActionResult> Responsaveis(CancellationToken cancellationToken)
    {
        if (!_integration.IsAuthorized(Request.Headers["x-integration-key"]))
        {
            return Unauthorized(new { message = "Integração não autorizada" });
        }
        return Ok(await _integration.ListResponsaveisAsync(cancellationToken));
    }

    [HttpGet("clientes")]
    public async Task<IActionResult> Clientes(
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (!_integration.IsAuthorized(Request.Headers["x-integration-key"]))
        {
            return Unauthorized(new { message = "Integração não autorizada" });
        }
        return Ok(await _integration.ListClientesAsync(search, cancellationToken));
    }

    [HttpPost("demandas")]
    public async Task<IActionResult> Create(
        [FromBody] CreateLuxusParceirosDemandaRequest request,
        CancellationToken cancellationToken)
    {
        if (!_integration.IsAuthorized(Request.Headers["x-integration-key"]))
        {
            return Unauthorized(new { message = "Integração não autorizada" });
        }
        try
        {
            return Ok(await _integration.CreateAsync(request, cancellationToken));
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

    [HttpGet("demandas/{externalRequestId}")]
    public async Task<IActionResult> Detail(string externalRequestId, CancellationToken cancellationToken)
    {
        if (!_integration.IsAuthorized(Request.Headers["x-integration-key"]))
        {
            return Unauthorized(new { message = "Integração não autorizada" });
        }
        try
        {
            return Ok(await _integration.GetAsync(externalRequestId, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpPost("demandas/{externalRequestId}/comentarios")]
    public async Task<IActionResult> AddComment(
        string externalRequestId,
        [FromBody] AddLuxusParceirosCommentRequest request,
        CancellationToken cancellationToken)
    {
        if (!_integration.IsAuthorized(Request.Headers["x-integration-key"]))
        {
            return Unauthorized(new { message = "Integração não autorizada" });
        }
        try
        {
            return Ok(await _integration.AddCommentAsync(
                externalRequestId,
                request,
                cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpPut("demandas/{externalRequestId}/detalhes")]
    public async Task<IActionResult> UpdateDemandDetails(
        string externalRequestId,
        [FromBody] UpdateLuxusParceirosDemandDetailsRequest request,
        CancellationToken cancellationToken)
    {
        if (!_integration.IsAuthorized(Request.Headers["x-integration-key"]))
            return Unauthorized(new { message = "Integração não autorizada" });
        try
        {
            return Ok(await _integration.UpdateDemandDetailsAsync(externalRequestId, request, cancellationToken));
        }
        catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("demandas/{externalRequestId}/etapa-venda")]
    public async Task<IActionResult> UpdateSaleStage(
        string externalRequestId,
        [FromBody] UpdateLuxusParceirosSaleStageRequest request,
        CancellationToken cancellationToken)
    {
        if (!_integration.IsAuthorized(Request.Headers["x-integration-key"]))
            return Unauthorized(new { message = "Integração não autorizada" });
        try
        {
            return Ok(await _integration.UpdateSaleStageAsync(externalRequestId, request, cancellationToken));
        }
        catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("demandas/{externalRequestId}/anexos/{attachmentId}")]
    public async Task<IActionResult> DownloadAttachment(
        string externalRequestId,
        string attachmentId,
        CancellationToken cancellationToken)
    {
        if (!_integration.IsAuthorized(Request.Headers["x-integration-key"]))
            return Unauthorized(new { message = "Integração não autorizada" });
        try
        {
            var file = await _integration.DownloadAttachmentAsync(externalRequestId, attachmentId, cancellationToken);
            Response.Headers["x-file-name"] = Uri.EscapeDataString(file.Filename);
            return File(file.Buffer, file.MimeType, file.Filename);
        }
        catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
    }

    [HttpPost("demandas/{externalRequestId}/anexos")]
    [RequestSizeLimit(52_428_800)]
    public async Task<IActionResult> ImportDocuments(
        string externalRequestId,
        [FromBody] ImportLuxusParceirosDocumentsRequest request,
        CancellationToken cancellationToken)
    {
        if (!_integration.IsAuthorized(Request.Headers["x-integration-key"]))
            return Unauthorized(new { message = "Integração não autorizada" });
        try
        {
            return Ok(await _integration.ImportPartnerDocumentsAsync(externalRequestId, request, cancellationToken));
        }
        catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("notify/{demandaId}")]
    public async Task<IActionResult> Notify(string demandaId, CancellationToken cancellationToken)
    {
        if (!_integration.IsAuthorized(Request.Headers["x-integration-key"]))
            return Unauthorized(new { message = "Integração não autorizada" });
        try
        {
            await _integration.NotifyByDemandaIdAsync(demandaId, cancellationToken);
            return Ok(new { accepted = true });
        }
        catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
    }
}
