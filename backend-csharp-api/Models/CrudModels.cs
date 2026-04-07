using System.ComponentModel.DataAnnotations;

namespace LuxusDemandas.Api.Models;

public sealed class CreateUserRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; init; } = string.Empty;

    [MinLength(6)]
    public string? Password { get; init; }

    [Required]
    [MinLength(1)]
    public string Name { get; init; } = string.Empty;

    public List<string>? RoleIds { get; init; }
}

public sealed class UpdateUserRequest
{
    [MinLength(1)]
    public string? Name { get; init; }

    public bool? Active { get; init; }

    public List<string>? RoleIds { get; init; }
}

public sealed class CreateSetorRequest
{
    [Required]
    [MinLength(1)]
    public string Name { get; init; } = string.Empty;

    public string? Slug { get; init; }
}

public sealed class UpdateSetorRequest
{
    [MinLength(1)]
    public string? Name { get; init; }

    [MinLength(1)]
    public string? Slug { get; init; }
}

public sealed class CreateClienteRequest
{
    [Required]
    [MinLength(1)]
    public string Name { get; init; } = string.Empty;

    public string? TipoPessoa { get; init; }

    public string? Documento { get; init; }

    public string? NomeFantasia { get; init; }

    public string? RamoAtividade { get; init; }

    public string? InscricaoEstadual { get; init; }

    public string? Cep { get; init; }

    public string? Endereco { get; init; }

    public string? Numero { get; init; }

    public string? Complemento { get; init; }

    public string? Bairro { get; init; }

    public string? Cidade { get; init; }

    public string? Uf { get; init; }

    public string? Telefone { get; init; }

    public string? Celular { get; init; }

    public string? Contato { get; init; }

    public string? Email { get; init; }

    public string? ObservacoesCadastro { get; init; }

    public bool? Active { get; init; }
}

public sealed class UpdateClienteRequest
{
    [MinLength(1)]
    public string? Name { get; init; }

    public string? TipoPessoa { get; init; }

    public string? Documento { get; init; }

    public string? NomeFantasia { get; init; }

    public string? RamoAtividade { get; init; }

    public string? InscricaoEstadual { get; init; }

    public string? Cep { get; init; }

    public string? Endereco { get; init; }

    public string? Numero { get; init; }

    public string? Complemento { get; init; }

    public string? Bairro { get; init; }

    public string? Cidade { get; init; }

    public string? Uf { get; init; }

    public string? Telefone { get; init; }

    public string? Celular { get; init; }

    public string? Contato { get; init; }

    public string? Email { get; init; }

    public string? ObservacoesCadastro { get; init; }

    public bool? Active { get; init; }
}
