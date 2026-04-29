using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LuxusDemandas.Api.Configuration;
using LuxusDemandas.Api.Models;
using LuxusDemandas.Api.Security;
using LuxusDemandas.Api.Support;
using Microsoft.Extensions.Options;

namespace LuxusDemandas.Api.Services;

public sealed class SupabaseRestService
{
    private readonly HttpClient _httpClient;
    private readonly AppOptions _options;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public SupabaseRestService(HttpClient httpClient, IOptions<AppOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;

        if (string.IsNullOrWhiteSpace(_options.SupabaseUrl) || string.IsNullOrWhiteSpace(_options.SupabaseServiceRoleKey))
        {
            return;
        }

        _httpClient.BaseAddress = new Uri($"{_options.SupabaseUrl.TrimEnd('/')}/rest/v1/");
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("apikey", _options.SupabaseServiceRoleKey);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.SupabaseServiceRoleKey);
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.SupabaseUrl) &&
        !string.IsNullOrWhiteSpace(_options.SupabaseServiceRoleKey);

    public async Task<TokenUser?> FindUserByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var normalizedEmail = email.ToLowerInvariant().Trim();
        var rows = await GetAsync<JsonElement[]>($"User?select=id,email,password_hash,name,active,needs_password_setup&email=eq.{Uri.EscapeDataString(normalizedEmail)}&limit=1", cancellationToken);
        var row = rows.FirstOrDefault();
        if (row.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return await BuildTokenUserAsync(row, cancellationToken);
    }

    public async Task<TokenUser?> FindUserByIdAsync(string id, CancellationToken cancellationToken)
    {
        var rows = await GetAsync<JsonElement[]>($"User?select=id,email,password_hash,name,active,needs_password_setup&id=eq.{Uri.EscapeDataString(id)}&limit=1", cancellationToken);
        var row = rows.FirstOrDefault();
        if (row.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return await BuildTokenUserAsync(row, cancellationToken);
    }

    public async Task<IReadOnlyList<UserDropdownDto>> ListUsersForDropdownAsync(CancellationToken cancellationToken)
    {
        var rows = await QueryAllRowsAsync("User?select=id,name,email&active=eq.true&order=name.asc", cancellationToken);
        return rows
            .Select(row => new UserDropdownDto(
                row.GetStringOrEmpty("id"),
                row.GetStringOrEmpty("name"),
                row.GetStringOrEmpty("email")))
            .ToList();
    }

    public async Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken cancellationToken)
    {
        var rows = await GetAsync<JsonElement[]>("Role?select=id,name,slug&order=name.asc", cancellationToken);
        return rows
            .Select(row => new RoleDto(
                row.GetStringOrEmpty("id"),
                row.GetStringOrEmpty("name"),
                row.GetStringOrEmpty("slug")))
            .ToList();
    }

    public async Task<IReadOnlyList<UserListDto>> ListAllUsersAsync(CancellationToken cancellationToken)
    {
        var users = await QueryAllRowsAsync("User?select=id,name,email,active,created_at,password_hash,needs_password_setup&order=name.asc", cancellationToken);
        var roleLinks = await QueryAllRowsAsync("user_role?select=user_id,role_id", cancellationToken);
        var roles = await ListRolesAsync(cancellationToken);
        var roleMap = roles.ToDictionary(role => role.Id, role => role);

        var rolesByUser = new Dictionary<string, List<RoleDto>>();
        foreach (var link in roleLinks)
        {
            var userId = link.GetStringOrEmpty("user_id");
            var roleId = link.GetStringOrEmpty("role_id");
            if (!roleMap.TryGetValue(roleId, out var role))
            {
                continue;
            }

            if (!rolesByUser.TryGetValue(userId, out var list))
            {
                list = [];
                rolesByUser[userId] = list;
            }
            list.Add(role);
        }

        return users
            .Select(row =>
            {
                var id = row.GetStringOrEmpty("id");
                rolesByUser.TryGetValue(id, out var mappedRoles);
                return new UserListDto(
                    id,
                    row.GetStringOrEmpty("name"),
                    row.GetStringOrEmpty("email"),
                    row.GetBooleanOrDefault("active"),
                    row.GetNullableString("created_at"),
                    mappedRoles ?? [],
                    row.GetBooleanOrDefault("needs_password_setup")
                    || PasswordAccessDefaults.IsPlaceholderHash(row.GetNullableString("password_hash"), _options.LegacyImportedPasswordHash));
            })
            .ToList();
    }

    public async Task<IReadOnlyList<SetorDto>> ListSetoresAsync(CancellationToken cancellationToken)
    {
        var rows = await QueryAllRowsAsync("Setor?select=id,name,slug&order=name.asc", cancellationToken);
        return rows
            .Select(row => new SetorDto(
                row.GetStringOrEmpty("id"),
                row.GetStringOrEmpty("name"),
                row.GetStringOrEmpty("slug")))
            .ToList();
    }

    public async Task<IReadOnlyList<ClienteDto>> ListClientesAsync(bool activeOnly, CancellationToken cancellationToken)
    {
        var query = activeOnly
            ? "Cliente?select=id,name,active,tipo_pessoa,documento,nome_fantasia,ramo_atividade,inscricao_estadual,cep,endereco,numero,complemento,bairro,cidade,uf,telefone,celular,contato,email,observacoes_cadastro,legacy_id&active=eq.true&order=name.asc"
            : "Cliente?select=id,name,active,tipo_pessoa,documento,nome_fantasia,ramo_atividade,inscricao_estadual,cep,endereco,numero,complemento,bairro,cidade,uf,telefone,celular,contato,email,observacoes_cadastro,legacy_id&order=name.asc";
        var rows = await QueryAllRowsAsync(query, cancellationToken);
        return rows
            .Select(row => new ClienteDto(
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
                row.GetNullableString("legacy_id")))
            .ToList();
    }

    public async Task<JsonElement[]> QueryRowsAsync(string pathAndQuery, CancellationToken cancellationToken) =>
        await GetAsync<JsonElement[]>(pathAndQuery, cancellationToken);

    public async Task<JsonElement[]> QueryAllRowsAsync(string pathAndQuery, CancellationToken cancellationToken, int batchSize = 1000)
    {
        var rows = new List<JsonElement>();
        var offset = 0;

        while (true)
        {
            var batch = await GetAsync<JsonElement[]>(AppendLimitAndOffset(pathAndQuery, batchSize, offset), cancellationToken);
            if (batch.Length == 0)
            {
                break;
            }

            rows.AddRange(batch.Select(item => item.Clone()));
            if (batch.Length < batchSize)
            {
                break;
            }

            offset += batchSize;
        }

        return rows.ToArray();
    }

    public async Task<JsonElement?> QuerySingleAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        var rows = await GetAsync<JsonElement[]>(pathAndQuery, cancellationToken);
        var row = rows.FirstOrDefault();
        return row.ValueKind == JsonValueKind.Undefined ? null : row;
    }

    public async Task<JsonElement> InsertSingleAsync(string table, object payload, CancellationToken cancellationToken)
    {
        var response = await SendJsonAsync(HttpMethod.Post, $"{table}?select=*", payload, cancellationToken, "return=representation");
        return ExtractSingleRow(response);
    }

    public async Task InsertManyAsync(string table, object payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, table)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Prefer", "return=minimal");
        _ = await SendAsync(request, cancellationToken);
    }

    public async Task<JsonElement?> UpdateSingleAsync(string table, string filterQuery, object payload, CancellationToken cancellationToken)
    {
        var response = await SendJsonAsync(new HttpMethod("PATCH"), $"{table}?{filterQuery}&select=*", payload, cancellationToken, "return=representation");
        return ExtractSingleOrDefaultRow(response);
    }

    public async Task DeleteAsync(string table, string filterQuery, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{table}?{filterQuery}");
        await SendAsync(request, cancellationToken);
    }

    public async Task<T> RpcAsync<T>(string functionName, object? payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"rpc/{functionName}")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload ?? new { }, JsonOptions), Encoding.UTF8, "application/json"),
        };
        using var response = await SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<T>(body, JsonOptions)
               ?? throw new InvalidOperationException("Resposta vazia do Supabase RPC.");
    }

    public async Task<JsonElement[]> ListBucketsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildStorageUri("bucket"));
        using var response = await SendStorageAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<JsonElement[]>(body, JsonOptions) ?? Array.Empty<JsonElement>();
    }

    public async Task CreateBucketAsync(string bucketName, bool isPublic, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildStorageUri("bucket"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { id = bucketName, name = bucketName, @public = isPublic }, JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };
        _ = await SendStorageAsync(request, cancellationToken);
    }

    public async Task UploadObjectAsync(
        string bucketName,
        string objectPath,
        byte[] content,
        string contentType,
        bool upsert,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildStorageUri($"object/{EscapePath(bucketName, objectPath)}"))
        {
            Content = new ByteArrayContent(content),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        request.Headers.Add("x-upsert", upsert ? "true" : "false");
        _ = await SendStorageAsync(request, cancellationToken);
    }

    public async Task<(byte[] Buffer, string ContentType)> DownloadObjectAsync(
        string bucketName,
        string objectPath,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildStorageUri($"object/authenticated/{EscapePath(bucketName, objectPath)}"));
        using var response = await SendStorageAsync(request, cancellationToken);
        var buffer = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return (buffer, contentType);
    }

    private async Task<TokenUser> BuildTokenUserAsync(JsonElement row, CancellationToken cancellationToken)
    {
        var userId = row.GetStringOrEmpty("id");
        var roleLinks = await GetAsync<JsonElement[]>($"user_role?select=role_id&user_id=eq.{Uri.EscapeDataString(userId)}", cancellationToken);
        var roleIds = roleLinks
            .Select(link => link.GetStringOrEmpty("role_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToArray();

        var roles = roleIds.Length == 0
            ? []
            : await GetAsync<JsonElement[]>($"Role?select=id,name,slug&id=in.({string.Join(",", roleIds.Select(Uri.EscapeDataString))})", cancellationToken);

        var mappedRoles = roles
            .Select(role => new RoleLink(new RoleDto(
                role.GetStringOrEmpty("id"),
                role.GetStringOrEmpty("name"),
                role.GetStringOrEmpty("slug"))))
            .ToList();

        return new TokenUser(
            row.GetStringOrEmpty("id"),
            row.GetStringOrEmpty("email"),
            row.GetStringOrEmpty("name"),
            row.GetBooleanOrDefault("active"),
            row.GetStringOrEmpty("password_hash"),
            row.GetBooleanOrDefault("needs_password_setup"),
            mappedRoles);
    }

    private async Task<T> GetAsync<T>(string pathAndQuery, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("SUPABASE_URL e SUPABASE_SERVICE_ROLE_KEY precisam estar configurados.");
        }

        using var response = await _httpClient.GetAsync(pathAndQuery, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Supabase REST error: {response.StatusCode} - {body}");
        }

        return JsonSerializer.Deserialize<T>(body, JsonOptions)
               ?? throw new InvalidOperationException("Resposta vazia do Supabase REST.");
    }

    private async Task<JsonElement> SendJsonAsync(
        HttpMethod method,
        string pathAndQuery,
        object payload,
        CancellationToken cancellationToken,
        string? preferHeader = null)
    {
        using var request = new HttpRequestMessage(method, pathAndQuery)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(preferHeader))
        {
            request.Headers.Add("Prefer", preferHeader);
        }

        using var response = await SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<JsonElement>(body, JsonOptions);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("SUPABASE_URL e SUPABASE_SERVICE_ROLE_KEY precisam estar configurados.");
        }

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            response.Dispose();
            throw new InvalidOperationException($"Supabase REST error: {response.StatusCode} - {body}");
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendStorageAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("SUPABASE_URL e SUPABASE_SERVICE_ROLE_KEY precisam estar configurados.");
        }

        request.Headers.Remove("apikey");
        request.Headers.Remove("Authorization");
        request.Headers.Add("apikey", _options.SupabaseServiceRoleKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.SupabaseServiceRoleKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            response.Dispose();
            throw new InvalidOperationException($"Supabase Storage error: {response.StatusCode} - {body}");
        }

        return response;
    }

    private Uri BuildStorageUri(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(_options.SupabaseUrl))
        {
            throw new InvalidOperationException("SUPABASE_URL precisa estar configurado.");
        }

        return new Uri($"{_options.SupabaseUrl.TrimEnd('/')}/storage/v1/{relativePath.TrimStart('/')}");
    }

    private static string EscapePath(string bucketName, string objectPath)
    {
        var objectSegments = objectPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        return $"{Uri.EscapeDataString(bucketName)}/{string.Join("/", objectSegments)}";
    }

    private static JsonElement ExtractSingleRow(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            return element;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var row = element.EnumerateArray().FirstOrDefault();
            if (row.ValueKind != JsonValueKind.Undefined)
            {
                return row;
            }
        }

        throw new InvalidOperationException("Nenhuma linha foi retornada pelo Supabase.");
    }

    private static JsonElement? ExtractSingleOrDefaultRow(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            return element;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var row = element.EnumerateArray().FirstOrDefault();
            return row.ValueKind == JsonValueKind.Undefined ? null : row;
        }

        return null;
    }

    private static string AppendLimitAndOffset(string pathAndQuery, int limit, int offset)
    {
        var sanitized = System.Text.RegularExpressions.Regex.Replace(
            pathAndQuery,
            @"([?&])(limit|offset)=\d+(&)?",
            match =>
            {
                var suffix = match.Groups[3].Success ? match.Groups[1].Value : string.Empty;
                return suffix;
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        sanitized = sanitized.TrimEnd('&', '?');
        var separator = sanitized.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{sanitized}{separator}limit={limit}&offset={offset}";
    }
}
