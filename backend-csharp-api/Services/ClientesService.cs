using System.Text.Json;
using System.Text.RegularExpressions;
using LuxusDemandas.Api.Models;
using LuxusDemandas.Api.Support;

namespace LuxusDemandas.Api.Services;

public sealed class ClientesService
{
    private const string ClienteSelectFields =
        "id,name,active,tipo_pessoa,documento,nome_fantasia,ramo_atividade,inscricao_estadual,cep,endereco,numero,complemento,bairro,cidade,uf,telefone,celular,contato,email,observacoes_cadastro,legacy_id";

    private readonly SupabaseRestService _supabase;

    public ClientesService(SupabaseRestService supabase)
    {
        _supabase = supabase;
    }

    public Task<IReadOnlyList<ClienteDto>> ListAsync(bool activeOnly, CancellationToken cancellationToken) =>
        _supabase.ListClientesAsync(activeOnly, cancellationToken);

    public async Task<ClienteDto> CreateAsync(CreateClienteRequest request, CancellationToken cancellationToken)
    {
        var tipoPessoa = NormalizeTipoPessoa(request.TipoPessoa);
        var documento = NormalizeDocumento(request.Documento, tipoPessoa);
        await EnsureDocumentoDisponivelAsync(documento, null, cancellationToken);

        var created = await _supabase.InsertSingleAsync("Cliente", new
        {
            name = request.Name.Trim(),
            tipo_pessoa = tipoPessoa,
            documento,
            nome_fantasia = NormalizeOptionalText(request.NomeFantasia),
            ramo_atividade = NormalizeOptionalText(request.RamoAtividade),
            inscricao_estadual = NormalizeOptionalText(request.InscricaoEstadual),
            cep = NormalizeDigitsOnly(request.Cep),
            endereco = NormalizeOptionalText(request.Endereco),
            numero = NormalizeOptionalText(request.Numero),
            complemento = NormalizeOptionalText(request.Complemento),
            bairro = NormalizeOptionalText(request.Bairro),
            cidade = NormalizeOptionalText(request.Cidade),
            uf = NormalizeUf(request.Uf),
            telefone = NormalizeOptionalText(request.Telefone),
            celular = NormalizeOptionalText(request.Celular),
            contato = NormalizeOptionalText(request.Contato),
            email = NormalizeEmail(request.Email),
            observacoes_cadastro = NormalizeOptionalText(request.ObservacoesCadastro),
            active = request.Active ?? true,
        }, cancellationToken);

        return MapCliente(created);
    }

    public async Task<ClienteDto> UpdateAsync(string id, UpdateClienteRequest request, CancellationToken cancellationToken)
    {
        var current = await _supabase.QuerySingleAsync(
            $"Cliente?select={ClienteSelectFields}&id=eq.{Uri.EscapeDataString(id)}&limit=1",
            cancellationToken);
        if (current is null)
        {
            throw new KeyNotFoundException("Cliente nao encontrado");
        }

        var tipoPessoaAtual = current.Value.GetNullableString("tipo_pessoa");
        var documentoAtual = current.Value.GetNullableString("documento");
        var tipoPessoa = request.TipoPessoa is not null
            ? NormalizeTipoPessoa(request.TipoPessoa)
            : tipoPessoaAtual;
        var documento = request.Documento is not null
            ? NormalizeDocumento(request.Documento, tipoPessoa)
            : documentoAtual;
        if (request.TipoPessoa is not null && request.Documento is null && !string.IsNullOrWhiteSpace(documentoAtual))
        {
            documento = NormalizeDocumento(documentoAtual, tipoPessoa);
        }

        await EnsureDocumentoDisponivelAsync(documento, id, cancellationToken);

        var updates = new Dictionary<string, object?>();
        if (request.Name is not null)
        {
            updates["name"] = request.Name.Trim();
        }

        if (request.TipoPessoa is not null)
        {
            updates["tipo_pessoa"] = tipoPessoa;
        }

        if (request.Documento is not null || (request.TipoPessoa is not null && !string.IsNullOrWhiteSpace(documentoAtual)))
        {
            updates["documento"] = documento;
        }

        if (request.NomeFantasia is not null)
        {
            updates["nome_fantasia"] = NormalizeOptionalText(request.NomeFantasia);
        }

        if (request.RamoAtividade is not null)
        {
            updates["ramo_atividade"] = NormalizeOptionalText(request.RamoAtividade);
        }

        if (request.InscricaoEstadual is not null)
        {
            updates["inscricao_estadual"] = NormalizeOptionalText(request.InscricaoEstadual);
        }

        if (request.Cep is not null)
        {
            updates["cep"] = NormalizeDigitsOnly(request.Cep);
        }

        if (request.Endereco is not null)
        {
            updates["endereco"] = NormalizeOptionalText(request.Endereco);
        }

        if (request.Numero is not null)
        {
            updates["numero"] = NormalizeOptionalText(request.Numero);
        }

        if (request.Complemento is not null)
        {
            updates["complemento"] = NormalizeOptionalText(request.Complemento);
        }

        if (request.Bairro is not null)
        {
            updates["bairro"] = NormalizeOptionalText(request.Bairro);
        }

        if (request.Cidade is not null)
        {
            updates["cidade"] = NormalizeOptionalText(request.Cidade);
        }

        if (request.Uf is not null)
        {
            updates["uf"] = NormalizeUf(request.Uf);
        }

        if (request.Telefone is not null)
        {
            updates["telefone"] = NormalizeOptionalText(request.Telefone);
        }

        if (request.Celular is not null)
        {
            updates["celular"] = NormalizeOptionalText(request.Celular);
        }

        if (request.Contato is not null)
        {
            updates["contato"] = NormalizeOptionalText(request.Contato);
        }

        if (request.Email is not null)
        {
            updates["email"] = NormalizeEmail(request.Email);
        }

        if (request.ObservacoesCadastro is not null)
        {
            updates["observacoes_cadastro"] = NormalizeOptionalText(request.ObservacoesCadastro);
        }

        if (request.Active.HasValue)
        {
            updates["active"] = request.Active.Value;
        }

        if (updates.Count == 0)
        {
            return MapCliente(current.Value);
        }

        var updated = await _supabase.UpdateSingleAsync(
            "Cliente",
            $"id=eq.{Uri.EscapeDataString(id)}",
            updates,
            cancellationToken);

        return updated is not null ? MapCliente(updated.Value) : MapCliente(current.Value);
    }

    public async Task<object> RemoveAsync(string id, CancellationToken cancellationToken)
    {
        var current = await _supabase.QuerySingleAsync(
            $"Cliente?select=id&id=eq.{Uri.EscapeDataString(id)}&limit=1",
            cancellationToken);
        if (current is null)
        {
            throw new KeyNotFoundException("Cliente nao encontrado");
        }

        await _supabase.DeleteAsync("Cliente", $"id=eq.{Uri.EscapeDataString(id)}", cancellationToken);
        return new { id };
    }

    private async Task EnsureDocumentoDisponivelAsync(string? documento, string? clienteId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(documento))
        {
            return;
        }

        var query = string.IsNullOrWhiteSpace(clienteId)
            ? $"Cliente?select=id&documento=eq.{Uri.EscapeDataString(documento)}&limit=1"
            : $"Cliente?select=id&documento=eq.{Uri.EscapeDataString(documento)}&id=neq.{Uri.EscapeDataString(clienteId)}&limit=1";
        var existing = await _supabase.QuerySingleAsync(query, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException("Ja existe um cliente com este documento.");
        }
    }

    private static ClienteDto MapCliente(JsonElement row) =>
        new(
            row.GetStringOrEmpty("id"),
            row.GetStringOrEmpty("name"),
            row.GetBooleanOrDefault("active"),
            row.GetNullableString("tipo_pessoa"),
            row.GetNullableString("documento"),
            row.GetNullableString("nome_fantasia"),
            row.GetNullableString("ramo_atividade"),
            row.GetNullableString("inscricao_estadual"),
            row.GetNullableString("cep"),
            row.GetNullableString("endereco"),
            row.GetNullableString("numero"),
            row.GetNullableString("complemento"),
            row.GetNullableString("bairro"),
            row.GetNullableString("cidade"),
            row.GetNullableString("uf"),
            row.GetNullableString("telefone"),
            row.GetNullableString("celular"),
            row.GetNullableString("contato"),
            row.GetNullableString("email"),
            row.GetNullableString("observacoes_cadastro"),
            row.GetNullableString("legacy_id"));

    private static string? NormalizeTipoPessoa(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "pf" => "pf",
            "pj" => "pj",
            _ => throw new InvalidOperationException("Tipo de pessoa invalido. Use PF ou PJ."),
        };
    }

    private static string? NormalizeDocumento(string? value, string? tipoPessoa)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = Regex.Replace(value, "[^0-9]", string.Empty);
        if (string.IsNullOrWhiteSpace(digits))
        {
            return null;
        }

        if (string.Equals(tipoPessoa, "pf", StringComparison.Ordinal))
        {
            if (digits.Length != 11)
            {
                throw new InvalidOperationException("CPF invalido. Informe 11 digitos.");
            }

            return digits;
        }

        if (string.Equals(tipoPessoa, "pj", StringComparison.Ordinal))
        {
            if (digits.Length != 14)
            {
                throw new InvalidOperationException("CNPJ invalido. Informe 14 digitos.");
            }

            return digits;
        }

        return digits.Length switch
        {
            11 => digits,
            14 => digits,
            _ => throw new InvalidOperationException("Documento invalido. Informe um CPF ou CNPJ valido."),
        };
    }

    private static string? NormalizeOptionalText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static string? NormalizeDigitsOnly(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = Regex.Replace(value, "[^0-9]", string.Empty);
        return string.IsNullOrWhiteSpace(digits) ? null : digits;
    }

    private static string? NormalizeUf(string? value)
    {
        var normalized = NormalizeOptionalText(value);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized.ToUpperInvariant();
    }

    private static string? NormalizeEmail(string? value)
    {
        var normalized = NormalizeOptionalText(value);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized.ToLowerInvariant();
    }
}
