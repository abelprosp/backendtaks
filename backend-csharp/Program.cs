using System.Collections;
using System.Diagnostics;
using System.Net;

var builder = WebApplication.CreateBuilder(args);
var externalPort = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{externalPort}");

builder.Services.AddHttpClient("node-proxy")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All,
    });
builder.Services.AddSingleton<NodeBackendSupervisor>();
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<NodeBackendSupervisor>());

var app = builder.Build();

app.MapGet("/health", async (NodeBackendSupervisor supervisor, CancellationToken cancellationToken) =>
{
    var nodeHealth = await supervisor.GetHealthAsync(cancellationToken);
    var statusCode = nodeHealth.Status == "ok" ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
    return Results.Json(new
    {
        status = nodeHealth.Status,
        host = "aspnet-core",
        node = nodeHealth,
        timestamp = DateTimeOffset.UtcNow,
    }, statusCode: statusCode);
});

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/health"))
    {
        await next();
        return;
    }

    var supervisor = context.RequestServices.GetRequiredService<NodeBackendSupervisor>();
    if (!supervisor.IsRunning)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new
        {
            message = "Backend Node interno indisponivel.",
            timestamp = DateTimeOffset.UtcNow,
        }, cancellationToken: context.RequestAborted);
        return;
    }

    var clientFactory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
    using var client = clientFactory.CreateClient("node-proxy");
    using var proxiedRequest = ProxyHttpRequestFactory.Create(context, supervisor.BaseAddress);
    using var proxiedResponse = await client.SendAsync(
        proxiedRequest,
        HttpCompletionOption.ResponseHeadersRead,
        context.RequestAborted);

    ProxyHttpRequestFactory.CopyResponse(context, proxiedResponse);
    await proxiedResponse.Content.CopyToAsync(context.Response.Body);
});

await app.RunAsync();

internal sealed class NodeBackendSupervisor : IHostedService, IDisposable
{
    private readonly ILogger<NodeBackendSupervisor> _logger;
    private readonly IHttpClientFactory _clientFactory;
    private Process? _process;

    public NodeBackendSupervisor(ILogger<NodeBackendSupervisor> logger, IHttpClientFactory clientFactory)
    {
        _logger = logger;
        _clientFactory = clientFactory;
    }

    public string NodeBackendPort => Environment.GetEnvironmentVariable("NODE_BACKEND_PORT") ?? "5000";
    public string NodeBackendPath => Environment.GetEnvironmentVariable("NODE_BACKEND_PATH") ?? "/app/node-backend";
    public string NodeEntryPoint => Path.Combine(NodeBackendPath, "dist", "src", "main.js");
    public Uri BaseAddress => new($"http://127.0.0.1:{NodeBackendPort}");
    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(NodeEntryPoint))
        {
            throw new FileNotFoundException($"Nao encontrei o entrypoint do backend Node em {NodeEntryPoint}.");
        }

        if (IsRunning)
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = "dist/src/main.js",
            WorkingDirectory = NodeBackendPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = Convert.ToString(entry.Key);
            var value = Convert.ToString(entry.Value);
            if (!string.IsNullOrWhiteSpace(key) && value is not null)
            {
                startInfo.Environment[key] = value;
            }
        }

        startInfo.Environment["PORT"] = NodeBackendPort;
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://0.0.0.0:{Environment.GetEnvironmentVariable("PORT") ?? "8080"}";

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                _logger.LogInformation("[node] {Message}", args.Data);
            }
        };
        _process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                _logger.LogError("[node] {Message}", args.Data);
            }
        };
        _process.Exited += (_, _) =>
        {
            _logger.LogWarning("Processo Node interno finalizado com codigo {ExitCode}", _process?.ExitCode);
        };

        if (!_process.Start())
        {
            throw new InvalidOperationException("Falha ao iniciar o backend Node interno.");
        }

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitForNodeAsync(cancellationToken);
        _logger.LogInformation("Backend Node interno disponivel em {Address}", BaseAddress);
    }

    public async Task<NodeHealthSnapshot> GetHealthAsync(CancellationToken cancellationToken)
    {
        if (!IsRunning)
        {
            return new NodeHealthSnapshot("down", false, null, "Processo Node nao iniciado.");
        }

        try
        {
            using var client = _clientFactory.CreateClient("node-proxy");
            using var response = await client.GetAsync(new Uri(BaseAddress, "/health"), cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            return new NodeHealthSnapshot(response.IsSuccessStatusCode ? "ok" : "degraded", response.IsSuccessStatusCode, payload, null);
        }
        catch (Exception ex)
        {
            return new NodeHealthSnapshot("down", false, null, ex.Message);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_process is null || _process.HasExited)
        {
            return;
        }

        try
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erro ao encerrar o backend Node interno.");
        }
    }

    public void Dispose()
    {
        _process?.Dispose();
    }

    private async Task WaitForNodeAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var client = _clientFactory.CreateClient("node-proxy");

        while (!linked.IsCancellationRequested)
        {
            if (_process is { HasExited: true })
            {
                throw new InvalidOperationException($"Backend Node encerrou durante o bootstrap (exit code {_process.ExitCode}).");
            }

            try
            {
                using var response = await client.GetAsync(new Uri(BaseAddress, "/health"), linked.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
                // aguarda o proximo poll
            }

            await Task.Delay(1000, linked.Token);
        }

        throw new TimeoutException("Tempo esgotado aguardando o backend Node ficar saudavel.");
    }
}

internal sealed record NodeHealthSnapshot(string Status, bool IsHealthy, string? Payload, string? Error);

internal static class ProxyHttpRequestFactory
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
        "Host",
    };

    public static HttpRequestMessage Create(HttpContext context, Uri baseAddress)
    {
        var targetUri = new Uri(baseAddress, $"{context.Request.Path}{context.Request.QueryString}");
        var requestMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);

        if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            requestMessage.Content = new StreamContent(context.Request.Body);
        }

        foreach (var header in context.Request.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key))
            {
                continue;
            }

            var added = requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            if (!added && requestMessage.Content is not null)
            {
                requestMessage.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        if (context.Connection.RemoteIpAddress is not null)
        {
            requestMessage.Headers.TryAddWithoutValidation("X-Forwarded-For", context.Connection.RemoteIpAddress.ToString());
        }

        requestMessage.Headers.TryAddWithoutValidation("X-Forwarded-Proto", context.Request.Scheme);
        requestMessage.Headers.TryAddWithoutValidation("X-Forwarded-Host", context.Request.Host.Value);

        return requestMessage;
    }

    public static void CopyResponse(HttpContext context, HttpResponseMessage responseMessage)
    {
        context.Response.StatusCode = (int)responseMessage.StatusCode;

        foreach (var header in responseMessage.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key))
            {
                continue;
            }
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in responseMessage.Content.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key))
            {
                continue;
            }
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        context.Response.Headers.Remove("transfer-encoding");
    }
}
