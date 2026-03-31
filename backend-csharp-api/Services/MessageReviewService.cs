using System.Text;
using System.Text.Json;
using LuxusDemandas.Api.Configuration;
using LuxusDemandas.Api.Models;
using Microsoft.Extensions.Options;

namespace LuxusDemandas.Api.Services;

public sealed class MessageReviewService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppOptions _options;
    private readonly string _knowledgeContext;

    public MessageReviewService(
        IHttpClientFactory httpClientFactory,
        IOptions<AppOptions> options,
        IHostEnvironment environment)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _knowledgeContext = LoadKnowledgeContext(environment.ContentRootPath);
    }

    public async Task<RevisarMensagemResponse> ReviewAsync(RevisarMensagemRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.OpenAiApiKey))
        {
            throw new InvalidOperationException("Revisão por IA não configurada. Defina OPENAI_API_KEY no servidor.");
        }

        var canal = NormalizeCanal(request.Canal);
        var client = _httpClientFactory.CreateClient();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.OpenAiApiKey);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                model = "gpt-4o-mini",
                temperature = 0.2,
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content =
                            "Você é um revisor e redator do Grupo Luxus. " +
                            "Sua tarefa é revisar mensagens para clientes, corrigindo gramática, ortografia, concordância, pontuação e clareza sem mudar a intenção principal. " +
                            "Adapte o texto ao canal informado. " +
                            "Para WhatsApp, mantenha um tom humano, cordial, direto e fácil de ler. " +
                            "Para e-mail, mantenha um tom profissional, organizado e claro. " +
                            "Não invente preços, prazos, políticas, promessas, benefícios ou fatos que não estejam no texto do usuário ou no contexto institucional. " +
                            "Use português do Brasil. " +
                            "Retorne apenas JSON válido no formato " +
                            "{\"textoRevisado\":\"...\",\"resumo\":\"...\",\"assuntoSugerido\":\"... ou null\",\"observacoes\":[\"...\"]}. " +
                            "Resumo deve explicar em uma ou duas frases o que foi ajustado. " +
                            "Observacoes deve listar pontos curtos da revisão. " +
                            "Contexto institucional do Grupo Luxus:\n" + _knowledgeContext
                    },
                    new
                    {
                        role = "user",
                        content = JsonSerializer.Serialize(new
                        {
                            canal,
                            objetivo = request.Objetivo,
                            instrucoesAdicionais = request.InstrucoesAdicionais,
                            manterTomOriginal = request.ManterTomOriginal,
                            texto = request.Texto.Trim(),
                        })
                    }
                }
            }),
            Encoding.UTF8,
            "application/json");

        using var response = await client.SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("A IA de revisão não respondeu corretamente no momento.");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var content = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "{}";

        var cleaned = content
            .Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("```", string.Empty, StringComparison.Ordinal)
            .Trim();

        try
        {
            using var parsed = JsonDocument.Parse(cleaned);
            var root = parsed.RootElement;
            var textoRevisado = root.TryGetProperty("textoRevisado", out var revisadoElement)
                ? (revisadoElement.GetString() ?? string.Empty).Trim()
                : string.Empty;
            var resumo = root.TryGetProperty("resumo", out var resumoElement)
                ? (resumoElement.GetString() ?? string.Empty).Trim()
                : string.Empty;
            var assuntoSugerido = root.TryGetProperty("assuntoSugerido", out var assuntoElement)
                ? assuntoElement.GetString()
                : null;
            var observacoes = root.TryGetProperty("observacoes", out var observacoesElement) && observacoesElement.ValueKind == JsonValueKind.Array
                ? observacoesElement.EnumerateArray()
                    .Select(item => item.GetString()?.Trim())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Cast<string>()
                    .ToList()
                : new List<string>();

            if (string.IsNullOrWhiteSpace(textoRevisado))
            {
                throw new InvalidOperationException("A IA não devolveu um texto revisado válido.");
            }

            return new RevisarMensagemResponse(
                canal,
                request.Texto.Trim(),
                textoRevisado,
                string.IsNullOrWhiteSpace(resumo) ? "Texto corrigido e ajustado para o canal informado." : resumo,
                string.IsNullOrWhiteSpace(assuntoSugerido) ? null : assuntoSugerido.Trim(),
                observacoes);
        }
        catch (JsonException)
        {
            return new RevisarMensagemResponse(
                canal,
                request.Texto.Trim(),
                cleaned,
                "Texto revisado pela IA.",
                canal == "email" ? "Sugestão de assunto indisponível" : null,
                Array.Empty<string>());
        }
    }

    private static string NormalizeCanal(string? canal)
    {
        var normalized = (canal ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "email" => "email",
            "whatsapp" or "whats" or "zap" => "whatsapp",
            "mensagem_geral" or "geral" or "outro" => "mensagem_geral",
            _ => "mensagem_geral",
        };
    }

    private static string LoadKnowledgeContext(string contentRootPath)
    {
        var knowledgeDir = Path.Combine(contentRootPath, "Knowledge");
        if (!Directory.Exists(knowledgeDir))
        {
            return string.Empty;
        }

        var files = Directory.GetFiles(knowledgeDir, "*.md")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var builder = new StringBuilder();
        foreach (var file in files)
        {
            var content = File.ReadAllText(file).Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine().AppendLine("---").AppendLine();
            }

            builder.Append(content);
        }

        return builder.ToString();
    }
}
