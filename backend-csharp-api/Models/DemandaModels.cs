using System.ComponentModel.DataAnnotations;

namespace LuxusDemandas.Api.Models;

public sealed class RecorrenciaInput
{
    [Required]
    public string DataBase { get; init; } = string.Empty;

    [Required]
    public string Tipo { get; init; } = string.Empty;

    public int? PrazoReaberturaDias { get; init; }
}

public sealed class DemandaResponsavelInput
{
    [Required]
    public string UserId { get; init; } = string.Empty;

    public bool? IsPrincipal { get; init; }
}

public sealed class DemandaSubtarefaCreateInput
{
    [Required]
    [MinLength(1)]
    public string Titulo { get; init; } = string.Empty;

    public string? ResponsavelUserId { get; init; }

    public int? Ordem { get; init; }
}

public sealed class DemandaSubtarefaUpdateInput
{
    [Required]
    [MinLength(1)]
    public string Titulo { get; init; } = string.Empty;

    public bool? Concluida { get; init; }

    public int? Ordem { get; init; }

    public string? ResponsavelUserId { get; init; }
}

public sealed class CreateDemandaRequest
{
    [Required]
    [MinLength(1)]
    public string Assunto { get; init; } = string.Empty;

    public bool? Prioridade { get; init; }

    public string? Prazo { get; init; }

    public string? Status { get; init; }

    public string? ObservacoesGerais { get; init; }

    public bool? IsRecorrente { get; init; }

    public bool? IsPrivada { get; init; }

    public List<string>? PrivateViewerIds { get; init; }

    public List<string>? Setores { get; init; }

    public List<string>? ClienteIds { get; init; }

    public List<DemandaResponsavelInput>? Responsaveis { get; init; }

    public List<DemandaSubtarefaCreateInput>? Subtarefas { get; init; }

    public RecorrenciaInput? Recorrencia { get; init; }
}

public sealed class UpdateDemandaRequest
{
    [MinLength(1)]
    public string? Assunto { get; init; }

    public bool? Prioridade { get; init; }

    public string? Prazo { get; init; }

    public string? Status { get; init; }

    public string? ObservacoesGerais { get; init; }

    public List<string>? Setores { get; init; }

    public List<string>? ClienteIds { get; init; }

    public List<DemandaResponsavelInput>? Responsaveis { get; init; }

    public List<DemandaSubtarefaUpdateInput>? Subtarefas { get; init; }

    public bool? IsRecorrente { get; init; }

    public bool? IsPrivada { get; init; }

    public List<string>? PrivateViewerIds { get; init; }

    public RecorrenciaInput? Recorrencia { get; init; }
}

public sealed class CreateDemandaFromTemplateRequest
{
    [Required]
    [MinLength(1)]
    public string Assunto { get; init; } = string.Empty;

    public string? Prazo { get; init; }

    public bool? Prioridade { get; init; }

    public bool? IsPrivada { get; init; }

    public List<string>? PrivateViewerIds { get; init; }

    public string? ObservacoesGerais { get; init; }

    public List<string>? ClienteIds { get; init; }

    public List<DemandaResponsavelInput>? Responsaveis { get; init; }

    public List<string>? SetorIds { get; init; }

    public List<DemandaSubtarefaCreateInput>? Subtarefas { get; init; }

    public string? RecorrenciaDataBase { get; init; }
}

public sealed class UpdateObservacaoRequest
{
    [Required]
    [MinLength(1)]
    public string Texto { get; init; } = string.Empty;
}

public sealed class BuscarIaContextRequest
{
    public string? PreviousQuery { get; init; }
    public string? PreviousScope { get; init; }
    public string? PreviousSearchTerm { get; init; }
    public Dictionary<string, object?>? PreviousFilters { get; init; }
}

public sealed class BuscarIaRequest
{
    [Required]
    [MinLength(2)]
    public string Query { get; init; } = string.Empty;

    public string? Scope { get; init; }

    public BuscarIaContextRequest? Context { get; init; }
}

public sealed record DemandaDownloadResult(byte[] Buffer, string Filename, string MimeType);

public sealed class ListDemandasFiltersQuery
{
    public string? ClienteId { get; set; }
    public string? Assunto { get; set; }
    public string? Status { get; set; }
    public bool? OcultarStandby { get; set; }
    public string? TipoRecorrencia { get; set; }
    public string? Protocolo { get; set; }
    public bool? Prioridade { get; set; }
    public string? CriadorId { get; set; }
    public string? ResponsavelPrincipalId { get; set; }
    public bool? ResponsavelApenasPrincipal { get; set; }
    public List<string>? SetorIds { get; set; }
    public string? CondicaoPrazo { get; set; }
    public string? PesquisarTarefaOuObservacao { get; set; }
    public string? PesquisaGeral { get; set; }
    public string? DataCriacaoDe { get; set; }
    public string? DataCriacaoAte { get; set; }
    public string? PrazoDe { get; set; }
    public string? PrazoAte { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
    public string? SortBy { get; set; }
    public string? SortDirection { get; set; }
}
