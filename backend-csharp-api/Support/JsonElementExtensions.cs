using System.Text.Json;

namespace LuxusDemandas.Api.Support;

public static class JsonElementExtensions
{
    public static string GetStringOrEmpty(this JsonElement row, string propertyName) =>
        row.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString() ?? string.Empty
            : string.Empty;

    public static string? GetNullableString(this JsonElement row, string propertyName) =>
        row.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString()
            : null;

    public static bool GetBooleanOrDefault(this JsonElement row, string propertyName, bool defaultValue = false)
    {
        if (!row.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return defaultValue;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed) => parsed,
            _ => defaultValue,
        };
    }

    public static int? GetNullableInt32(this JsonElement row, string propertyName)
    {
        if (!row.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => null,
        };
    }

    public static JsonElement? GetOptionalProperty(this JsonElement row, string propertyName) =>
        row.TryGetProperty(propertyName, out var property) ? property : null;

    public static IReadOnlyList<JsonElement> GetArrayOrEmpty(this JsonElement row, string propertyName)
    {
        if (!row.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<JsonElement>();
        }

        return property.EnumerateArray().ToArray();
    }
}
