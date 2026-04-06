using System.Text.RegularExpressions;
using LuxusDemandas.Api.Models;
using LuxusDemandas.Api.Support;

namespace LuxusDemandas.Api.Services;

public sealed class ClientesService
{
    private readonly SupabaseRestService _supabase;

    public ClientesService(SupabaseRestService supabase)
    {
        _supabase = supabase;
    }

    public Task<IReadOnlyList<ClienteDto>> ListAsync(bool activeOnly, CancellationToken cancellationToken) =>
        _supabase.ListClientesAsync(activeOnly, cancellationToken);

    public async Task<object> CreateAsync(CreateClienteRequest request, CancellationToken cancellationToken)
    {
        var tipoPessoa = NormalizeTipoPessoa(request.TipoPessoa);
        var documento = NormalizeDocumento(request.Documento, tipoPessoa);
        await EnsureDocumentoDisponivelAsync(documento, null, cancellationToken);

        var created = await _supabase.InsertSingleAsync("Cliente", new
        {
            name = request.Name.Trim(),
            tipo_pessoa = tipoPessoa,
            documento,
            active = request.Active ?? true,
        }, cancellationToken);

        return new
        {
            id = created.GetStringOrEmpty("id"),
            name = created.GetStringOrEmpty("name"),
            active = created.GetBooleanOrDefault("active"),
            tipoPessoa = created.GetNullableString("tipo_pessoa"),
            documento = created.GetNullableString("documento"),
        };
    }

    public async Task<object> UpdateAsync(string id, UpdateClienteRequest request, CancellationToken cancellationToken)
    {
        var current = await _supabase.QuerySingleAsync(
            $"Cliente?select=id,name,active,tipo_pessoa,documento&id=eq.{Uri.EscapeDataString(id)}&limit=1",
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

        if (request.Documento is not null)
        {
            updates["documento"] = documento;
        }

        if (request.Active.HasValue)
        {
            updates["active"] = request.Active.Value;
        }

        if (updates.Count == 0)
        {
            return new
            {
                id = current.Value.GetStringOrEmpty("id"),
                name = current.Value.GetStringOrEmpty("name"),
                active = current.Value.GetBooleanOrDefault("active"),
                tipoPessoa = tipoPessoaAtual,
                documento = documentoAtual,
            };
        }

        var updated = await _supabase.UpdateSingleAsync(
            "Cliente",
            $"id=eq.{Uri.EscapeDataString(id)}",
            updates,
            cancellationToken);

        return new
        {
            id = updated?.GetStringOrEmpty("id") ?? current.Value.GetStringOrEmpty("id"),
            name = updated?.GetStringOrEmpty("name") ?? current.Value.GetStringOrEmpty("name"),
            active = updated?.GetBooleanOrDefault("active") ?? current.Value.GetBooleanOrDefault("active"),
            tipoPessoa = updated?.GetNullableString("tipo_pessoa") ?? tipoPessoa,
            documento = updated?.GetNullableString("documento") ?? documento,
        };
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
}
