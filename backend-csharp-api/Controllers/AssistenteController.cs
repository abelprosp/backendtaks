using LuxusDemandas.Api.Models;
using LuxusDemandas.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxusDemandas.Api.Controllers;

[ApiController]
[Authorize]
[Route("assistente")]
public sealed class AssistenteController : ControllerBase
{
    private readonly MessageReviewService _messageReviewService;

    public AssistenteController(MessageReviewService messageReviewService)
    {
        _messageReviewService = messageReviewService;
    }

    [HttpPost("revisar-mensagem")]
    public async Task<IActionResult> RevisarMensagem([FromBody] RevisarMensagemRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _messageReviewService.ReviewAsync(request, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = ex.Message });
        }
    }
}
