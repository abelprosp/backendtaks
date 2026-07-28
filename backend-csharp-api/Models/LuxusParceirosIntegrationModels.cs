using System.ComponentModel.DataAnnotations;

namespace LuxusDemandas.Api.Models;

public sealed class CreateLuxusParceirosDemandaRequest
{
    [Required]
    public string RequestId { get; init; } = string.Empty;

    [Required]
    public string ResponsibleId { get; init; } = string.Empty;

    [Required]
    public string ClientId { get; init; } = string.Empty;

    [Required]
    public string Deadline { get; init; } = string.Empty;

    [Required]
    public string Subject { get; init; } = string.Empty;

    [Required]
    public string Description { get; init; } = string.Empty;

    [Required]
    public string LocalProtocol { get; init; } = string.Empty;

    [Required]
    public string PartnerName { get; init; } = string.Empty;

    public string? BranchName { get; init; }

    [Required]
    public string RequesterName { get; init; } = string.Empty;

    [Required]
    [EmailAddress]
    public string RequesterEmail { get; init; } = string.Empty;

    public bool? Priority { get; init; }
}

public sealed record LuxusParceirosClientDto(
    string Id,
    string Name,
    string? Document,
    string? TradeName);
