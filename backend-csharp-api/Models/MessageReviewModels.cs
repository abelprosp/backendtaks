using System.ComponentModel.DataAnnotations;

namespace LuxusDemandas.Api.Models;

public sealed class RevisarMensagemRequest
{
    [Required]
    [MinLength(5)]
    public string Texto { get; init; } = string.Empty;

    [Required]
    public string Canal { get; init; } = "whatsapp";

    public string? Objetivo { get; init; }

    public string? InstrucoesAdicionais { get; init; }

    public bool ManterTomOriginal { get; init; } = false;
}

public sealed record RevisarMensagemResponse(
    string Canal,
    string TextoOriginal,
    string TextoRevisado,
    string Resumo,
    string? AssuntoSugerido,
    IReadOnlyList<string> Observacoes);
