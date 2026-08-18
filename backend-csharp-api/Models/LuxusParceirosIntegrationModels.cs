using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace LuxusDemandas.Api.Models;

public sealed class CreateLuxusParceirosDemandaRequest
{
    public string? EntityType { get; init; }

    [Required]
    public string RequestId { get; init; } = string.Empty;

    [Required]
    public string ResponsibleId { get; init; } = string.Empty;

    public string? ClientId { get; init; }

    public string? ClientName { get; init; }

    public string? ClientDocumentType { get; init; }

    public string? ClientDocument { get; init; }

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

    public IReadOnlyList<LuxusParceirosDocumentDto> Documents { get; init; } = [];
}

public sealed class LuxusParceirosDocumentDto
{
    [Required]
    public string Id { get; init; } = string.Empty;
    [Required]
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string MimeType { get; init; } = "application/octet-stream";
    public long Size { get; init; }

    [JsonPropertyName("contentBase64")]
    public string? ContentBase64 { get; init; }
}

public sealed record LuxusParceirosClientDto(
    string Id,
    string Name,
    string? Document,
    string? TradeName,
    string? PersonType);

public sealed class AddLuxusParceirosCommentRequest
{
    [Required]
    [MinLength(1)]
    public string Content { get; init; } = string.Empty;

    [Required]
    public string AuthorName { get; init; } = string.Empty;
}

public sealed class UpdateLuxusParceirosSaleStageRequest
{
    [Required]
    public string Stage { get; init; } = string.Empty;
    public string? DocumentId { get; init; }
    public string? DocumentName { get; init; }
    public string? DocumentMimeType { get; init; }
    public string? Note { get; init; }
    public string? TurnRequestFrom { get; init; }
    public string? TurnRequestReason { get; init; }
    public bool? ClearTurnRequest { get; init; }
}

public sealed class ImportLuxusParceirosDocumentsRequest
{
    [Required]
    [MinLength(1)]
    public IReadOnlyList<LuxusParceirosDocumentDto> Documents { get; init; } = [];
}
