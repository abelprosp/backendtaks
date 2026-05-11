using System.Net;
using System.Net.Mail;
using LuxusDemandas.Api.Configuration;
using Microsoft.Extensions.Options;

namespace LuxusDemandas.Api.Services;

public sealed class EmailService
{
    private readonly AppOptions _options;

    public EmailService(IOptions<AppOptions> options)
    {
        _options = options.Value;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.SmtpHost) &&
        !string.IsNullOrWhiteSpace(_options.SmtpFromEmail);

    public async Task SendPasswordAccessEmailAsync(
        string toEmail,
        string toName,
        string accessUrl,
        DateTimeOffset expiresAt,
        bool firstAccess,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.SmtpFromEmail, _options.SmtpFromName),
            Subject = firstAccess ? "Primeiro acesso ao Luxus Demandas" : "Redefinicao de senha - Luxus Demandas",
            Body = BuildHtmlBody(toName, accessUrl, expiresAt, firstAccess),
            IsBodyHtml = true,
        };
        message.To.Add(new MailAddress(toEmail, toName));

        using var smtp = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
        {
            EnableSsl = _options.SmtpUseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };

        if (!string.IsNullOrWhiteSpace(_options.SmtpUsername))
        {
            smtp.Credentials = new NetworkCredential(_options.SmtpUsername, _options.SmtpPassword);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await smtp.SendMailAsync(message);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static string BuildHtmlBody(string toName, string accessUrl, DateTimeOffset expiresAt, bool firstAccess)
    {
        var greetingName = string.IsNullOrWhiteSpace(toName) ? "usuario" : WebUtility.HtmlEncode(toName.Trim());
        var actionLabel = firstAccess ? "definir sua senha" : "redefinir sua senha";
        var expiresLabel = expiresAt.ToLocalTime().ToString("dd/MM/yyyy 'as' HH:mm");
        var encodedUrl = WebUtility.HtmlEncode(accessUrl);

        return $"""
                <div style="font-family:Arial,sans-serif;line-height:1.6;color:#1f2937">
                  <p>Ola, {greetingName}.</p>
                  <p>Use o link abaixo para {actionLabel} no Luxus Demandas:</p>
                  <p>
                    <a href="{encodedUrl}" style="display:inline-block;padding:12px 18px;border-radius:10px;background:#15803d;color:#ffffff;text-decoration:none;font-weight:600">
                      {WebUtility.HtmlEncode(firstAccess ? "Definir senha" : "Redefinir senha")}
                    </a>
                  </p>
                  <p>Se preferir, copie e cole este endereco no navegador:</p>
                  <p><a href="{encodedUrl}">{encodedUrl}</a></p>
                  <p>Este link expira em {WebUtility.HtmlEncode(expiresLabel)}.</p>
                  <p>Se voce nao solicitou este acesso, pode ignorar este e-mail.</p>
                </div>
                """;
    }
}
