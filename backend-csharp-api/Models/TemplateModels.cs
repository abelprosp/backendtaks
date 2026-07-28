using System.ComponentModel.DataAnnotations;

namespace LuxusDemandas.Api.Models;

public sealed class TemplateResponsavelInput
{
    [Required]
    public string UserId { get; init; } = string.Empty;

    public bool? IsPrincipal { get; init; }
}

public sealed class TemplateSubtarefaInput
{
    [Required]
    [MinLength(1)]
    public string Titulo { get; init; } = string.Empty;

    public int? Ordem { get; init; }

    public string? ResponsavelUserId { get; init; }
}

public sealed class CreateTemplateRequest
{
    [Required]
    [MinLength(1)]
    public string Name { get; init; } = string.Empty;

    public string? Descricao { get; init; }

    public string? AssuntoTemplate { get; init; }

    public bool? PrioridadeDefault { get; init; }

    public string? ObservacoesGeraisTemplate { get; init; }

    public bool? IsRecorrenteDefault { get; init; }

    public string? RecorrenciaTipo { get; init; }

    public string? RecorrenciaDataBaseDefault { get; init; }

    [Range(0, int.MaxValue)]
    public int? RecorrenciaPrazoReaberturaDias { get; init; }

    public List<string>? SetorIds { get; init; }

    public List<string>? ClienteIds { get; init; }

    public List<TemplateResponsavelInput>? Responsaveis { get; init; }

    public List<TemplateSubtarefaInput>? Subtarefas { get; init; }
}

public sealed class UpdateTemplateRequest
{
    [MinLength(1)]
    public string? Name { get; init; }

    public string? Descricao { get; init; }

    public string? AssuntoTemplate { get; init; }

    public bool? PrioridadeDefault { get; init; }

    public string? ObservacoesGeraisTemplate { get; init; }

    public bool? IsRecorrenteDefault { get; init; }

    public string? RecorrenciaTipo { get; init; }

    public string? RecorrenciaDataBaseDefault { get; init; }

    [Range(0, int.MaxValue)]
    public int? RecorrenciaPrazoReaberturaDias { get; init; }

    public List<string>? SetorIds { get; init; }

    public List<string>? ClienteIds { get; init; }

    public List<TemplateResponsavelInput>? Responsaveis { get; init; }

    public List<TemplateSubtarefaInput>? Subtarefas { get; init; }
}

public sealed record TemplateDemandaResponsavel(string UserId, bool IsPrincipal);

public sealed record TemplateDemandaSubtarefa(string Titulo, string? ResponsavelUserId);

public sealed record TemplateDemandaSource(
    string Id,
    string Name,
    bool PrioridadeDefault,
    string? ObservacoesGeraisTemplate,
    bool IsRecorrenteDefault,
    string? RecorrenciaTipo,
    string? RecorrenciaDataBaseDefault,
    int? RecorrenciaPrazoReaberturaDias,
    IReadOnlyList<string> SetorIds,
    IReadOnlyList<string> ClienteIds,
    IReadOnlyList<TemplateDemandaResponsavel> Responsaveis,
    IReadOnlyList<TemplateDemandaSubtarefa> Subtarefas);
