using System.Globalization;
using System.Text;
using LuxusDemandas.Api.Models;
using LuxusDemandas.Api.Support;

namespace LuxusDemandas.Api.Services;

public sealed class SetoresService
{
    private readonly SupabaseRestService _supabase;

    public SetoresService(SupabaseRestService supabase)
    {
        _supabase = supabase;
    }

    public Task<IReadOnlyList<SetorDto>> ListAsync(CancellationToken cancellationToken) =>
        _supabase.ListSetoresAsync(cancellationToken);

    public async Task<object> CreateAsync(CreateSetorRequest request, CancellationToken cancellationToken)
    {
        var slug = string.IsNullOrWhiteSpace(request.Slug)
            ? Slugify(request.Name)
            : request.Slug.Trim();

        var existing = await _supabase.QuerySingleAsync(
            $"Setor?select=id&slug=eq.{Uri.EscapeDataString(slug)}&limit=1",
            cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException("Já existe um setor com esse slug");
        }

        var created = await _supabase.InsertSingleAsync("Setor", new
        {
            name = request.Name.Trim(),
            slug,
        }, cancellationToken);

        return new
        {
            id = created.GetStringOrEmpty("id"),
            name = created.GetStringOrEmpty("name"),
            slug = created.GetStringOrEmpty("slug"),
        };
    }

    public async Task<object> UpdateAsync(string id, UpdateSetorRequest request, CancellationToken cancellationToken)
    {
        var current = await _supabase.QuerySingleAsync(
            $"Setor?select=id,name,slug&id=eq.{Uri.EscapeDataString(id)}&limit=1",
            cancellationToken);
        if (current is null)
        {
            throw new KeyNotFoundException("Setor não encontrado");
        }

        var updates = new Dictionary<string, object?>();
        if (request.Name is not null)
        {
            updates["name"] = request.Name.Trim();
        }

        if (request.Slug is not null)
        {
            var slug = request.Slug.Trim();
            var existing = await _supabase.QuerySingleAsync(
                $"Setor?select=id&slug=eq.{Uri.EscapeDataString(slug)}&id=neq.{Uri.EscapeDataString(id)}&limit=1",
                cancellationToken);
            if (existing is not null)
            {
                throw new InvalidOperationException("Já existe um setor com esse slug");
            }

            updates["slug"] = slug;
        }

        if (updates.Count == 0)
        {
            return new
            {
                id = current.Value.GetStringOrEmpty("id"),
                name = current.Value.GetStringOrEmpty("name"),
                slug = current.Value.GetStringOrEmpty("slug"),
            };
        }

        var updated = await _supabase.UpdateSingleAsync(
            "Setor",
            $"id=eq.{Uri.EscapeDataString(id)}",
            updates,
            cancellationToken);

        return new
        {
            id = updated?.GetStringOrEmpty("id") ?? current.Value.GetStringOrEmpty("id"),
            name = updated?.GetStringOrEmpty("name") ?? current.Value.GetStringOrEmpty("name"),
            slug = updated?.GetStringOrEmpty("slug") ?? current.Value.GetStringOrEmpty("slug"),
        };
    }

    public async Task<object> RemoveAsync(string id, CancellationToken cancellationToken)
    {
        var current = await _supabase.QuerySingleAsync(
            $"Setor?select=id&id=eq.{Uri.EscapeDataString(id)}&limit=1",
            cancellationToken);
        if (current is null)
        {
            throw new KeyNotFoundException("Setor não encontrado");
        }

        await _supabase.DeleteAsync("Setor", $"id=eq.{Uri.EscapeDataString(id)}", cancellationToken);
        return new { id };
    }

    private static string Slugify(string value)
    {
        var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        var lastWasHyphen = false;

        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasHyphen = false;
                continue;
            }

            if (char.IsWhiteSpace(ch) || ch == '-')
            {
                if (!lastWasHyphen && builder.Length > 0)
                {
                    builder.Append('-');
                    lastWasHyphen = true;
                }
            }
        }

        return builder.ToString().Trim('-') switch
        {
            "" => "setor",
            var slug => slug,
        };
    }
}
