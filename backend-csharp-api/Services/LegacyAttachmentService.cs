using System.Net;
using System.Text.RegularExpressions;
using LuxusDemandas.Api.Configuration;
using Microsoft.Extensions.Options;

namespace LuxusDemandas.Api.Services;

public sealed class LegacyAttachmentService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppOptions _options;
    private readonly CookieContainer _cookies = new();
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private bool _loggedIn;

    public LegacyAttachmentService(IHttpClientFactory httpClientFactory, IOptions<AppOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.LegacyBaseUrl)
        && !string.IsNullOrWhiteSpace(_options.LegacyEmail)
        && !string.IsNullOrWhiteSpace(_options.LegacyPassword);

    public async Task<LegacyAttachmentDownload> DownloadAsync(LegacyAttachmentRef reference, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Integração com anexos do legado não configurada.");
        }

        await EnsureLoggedInAsync(cancellationToken);
        var client = CreateClient();
        var url = ResolveLegacyUrl(reference.DownloadUrl);
        using var response = await client.GetAsync(url, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _loggedIn = false;
            await EnsureLoggedInAsync(cancellationToken);
            using var retry = await client.GetAsync(url, cancellationToken);
            retry.EnsureSuccessStatusCode();
            return new LegacyAttachmentDownload(
                await retry.Content.ReadAsByteArrayAsync(cancellationToken),
                retry.Content.Headers.ContentType?.MediaType);
        }

        response.EnsureSuccessStatusCode();
        return new LegacyAttachmentDownload(
            await response.Content.ReadAsByteArrayAsync(cancellationToken),
            response.Content.Headers.ContentType?.MediaType);
    }

    public async Task<LegacyAttachmentMetadata> UploadAsync(
        string legacyDemandaId,
        byte[] buffer,
        string originalFilename,
        string displayName,
        string contentType,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Integração com anexos do legado não configurada.");
        }

        await EnsureLoggedInAsync(cancellationToken);
        var client = CreateClient();
        var formHtml = await client.GetStringAsync(ResolveLegacyPath($"/painel/demandas/anexos/{legacyDemandaId}"), cancellationToken);
        var token = ExtractInputValue(formHtml, "_token");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Não foi possível ler o token do formulário de anexos do legado.");
        }

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(token), "_token");
        content.Add(new StringContent(string.IsNullOrWhiteSpace(displayName) ? originalFilename : displayName), "nome");
        content.Add(new StringContent("Incluir"), "enviar");
        var fileContent = new ByteArrayContent(buffer);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        content.Add(fileContent, "imagem", string.IsNullOrWhiteSpace(originalFilename) ? "arquivo" : originalFilename);

        using var response = await client.PostAsync(ResolveLegacyPath($"/painel/demandas/anexos/cadastrar/{legacyDemandaId}"), content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var updatedHtml = await client.GetStringAsync(ResolveLegacyPath($"/painel/demandas/anexos/{legacyDemandaId}"), cancellationToken);
        var anexos = ParseAttachments(updatedHtml, legacyDemandaId);
        var sanitized = Path.GetFileName(originalFilename);
        return anexos
            .FirstOrDefault(item => string.Equals(item.Filename, sanitized, StringComparison.OrdinalIgnoreCase))
            ?? anexos.FirstOrDefault()
            ?? new LegacyAttachmentMetadata(
                string.Empty,
                legacyDemandaId,
                sanitized,
                displayName,
                null,
                BuildLegacyStoragePath(legacyDemandaId, string.Empty, ResolveLegacyPath($"/painel/demandas/anexos/{legacyDemandaId}")));
    }

    public async Task<IReadOnlyList<LegacyAttachmentMetadata>> ListAsync(string legacyDemandaId, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return Array.Empty<LegacyAttachmentMetadata>();
        }

        await EnsureLoggedInAsync(cancellationToken);
        var client = CreateClient();
        var html = await client.GetStringAsync(ResolveLegacyPath($"/painel/demandas/anexos/{legacyDemandaId}"), cancellationToken);
        return ParseAttachments(html, legacyDemandaId);
    }

    public static bool TryParseStoragePath(string? storagePath, out LegacyAttachmentRef reference)
    {
        reference = default;
        var raw = (storagePath ?? string.Empty).Trim();
        if (!raw.StartsWith("legacy://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var match = Regex.Match(raw, @"^legacy://demandas/(?<demanda>[^/]+)/anexos/(?<anexo>[^?]*)(?:\?url=(?<url>.+))?$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        var url = match.Groups["url"].Success ? Uri.UnescapeDataString(match.Groups["url"].Value) : string.Empty;
        reference = new LegacyAttachmentRef(
            Uri.UnescapeDataString(match.Groups["demanda"].Value),
            Uri.UnescapeDataString(match.Groups["anexo"].Value),
            url);
        return true;
    }

    public static string BuildLegacyStoragePath(string legacyDemandaId, string legacyAnexoId, string downloadUrl) =>
        $"legacy://demandas/{Uri.EscapeDataString(legacyDemandaId)}/anexos/{Uri.EscapeDataString(legacyAnexoId ?? string.Empty)}?url={Uri.EscapeDataString(downloadUrl ?? string.Empty)}";

    private async Task EnsureLoggedInAsync(CancellationToken cancellationToken)
    {
        if (_loggedIn) return;
        await _loginLock.WaitAsync(cancellationToken);
        try
        {
            if (_loggedIn) return;
            var client = CreateClient();
            var loginHtml = await client.GetStringAsync(ResolveLegacyPath("/login"), cancellationToken);
            var token = ExtractInputValue(loginHtml, "_token");
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Não foi possível ler o token de login do legado.");
            }

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_token"] = token,
                ["email"] = _options.LegacyEmail,
                ["password"] = _options.LegacyPassword,
            });
            using var response = await client.PostAsync(ResolveLegacyPath("/login"), content, cancellationToken);
            response.EnsureSuccessStatusCode();
            var painel = await client.GetStringAsync(ResolveLegacyPath("/painel"), cancellationToken);
            if (!Regex.IsMatch(painel, "logout|Sair|painel", RegexOptions.IgnoreCase))
            {
                throw new InvalidOperationException("Login no legado não confirmou sessão autenticada.");
            }

            _loggedIn = true;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            AllowAutoRedirect = true,
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(_options.LegacyBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(90),
        };
    }

    private string ResolveLegacyPath(string path) => new Uri(new Uri(_options.LegacyBaseUrl.TrimEnd('/') + "/"), path.TrimStart('/')).ToString();

    private string ResolveLegacyUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var absolute)
            ? absolute.ToString()
            : ResolveLegacyPath(url);

    private static string ExtractInputValue(string html, string name)
    {
        var pattern = $@"<input[^>]*name=[""']{Regex.Escape(name)}[""'][^>]*value=[""'](?<value>[^""']*)[""'][^>]*>";
        return WebUtility.HtmlDecode(Regex.Match(html, pattern, RegexOptions.IgnoreCase).Groups["value"].Value);
    }

    private List<LegacyAttachmentMetadata> ParseAttachments(string html, string legacyDemandaId)
    {
        var rows = Regex.Matches(html, @"<tr[\s\S]*?</tr>", RegexOptions.IgnoreCase)
            .Select(match => match.Value)
            .ToList();
        var result = new List<LegacyAttachmentMetadata>();
        foreach (var row in rows)
        {
            var cells = Regex.Matches(row, @"<td[^>]*>(?<value>[\s\S]*?)</td>", RegexOptions.IgnoreCase)
                .Select(match => StripTags(match.Groups["value"].Value))
                .ToArray();
            if (cells.Length < 4 || Regex.IsMatch(cells[0], "não existem registros", RegexOptions.IgnoreCase))
            {
                continue;
            }

            var hrefs = Regex.Matches(row, @"href=[""'](?<href>[^""']+)[""']", RegexOptions.IgnoreCase)
                .Select(match => ResolveLegacyUrl(WebUtility.HtmlDecode(match.Groups["href"].Value)))
                .ToArray();
            var downloadUrl = hrefs.FirstOrDefault(href => Regex.IsMatch(href, @"/assets/uploads/imgs/demandas/|/download|arquivo", RegexOptions.IgnoreCase));
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                continue;
            }

            var filename = cells.Length > 3 && !string.IsNullOrWhiteSpace(cells[3])
                ? cells[3]
                : Path.GetFileName(new Uri(downloadUrl).LocalPath);
            var legacyAnexoId = cells[0];
            result.Add(new LegacyAttachmentMetadata(
                legacyAnexoId,
                legacyDemandaId,
                filename,
                cells.Length > 1 ? cells[1] : null,
                cells.Length > 4 ? cells[4] : null,
                BuildLegacyStoragePath(legacyDemandaId, legacyAnexoId, downloadUrl)));
        }

        return result;
    }

    private static string StripTags(string html) =>
        WebUtility.HtmlDecode(Regex.Replace(html.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase), "<[^>]+>", " "))
            .Replace('\u00a0', ' ')
            .Trim();
}

public readonly record struct LegacyAttachmentRef(string DemandaId, string AnexoId, string DownloadUrl);

public sealed record LegacyAttachmentDownload(byte[] Buffer, string? ContentType);

public sealed record LegacyAttachmentMetadata(
    string LegacyAnexoId,
    string LegacyDemandaId,
    string Filename,
    string? DisplayName,
    string? CreatedAt,
    string StoragePath);
