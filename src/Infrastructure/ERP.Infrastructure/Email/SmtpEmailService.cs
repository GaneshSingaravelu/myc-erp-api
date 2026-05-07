using ERP.Application.Abstractions.Email;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ERP.Infrastructure.Email;

public sealed class SmtpEmailService : IEmailService
{
    private readonly EmailSettings _settings;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(IOptions<EmailSettings> settings, ILogger<SmtpEmailService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var email = new MimeMessage();
        email.From.Add(new MailboxAddress(_settings.SenderName, _settings.SenderEmail));
        email.To.Add(MailboxAddress.Parse(message.To));
        email.Subject = message.Subject;

        var builder = new BodyBuilder
        {
            HtmlBody = message.HtmlBody,
            TextBody = message.PlainTextBody
        };

        if (message.Attachments is { Count: > 0 })
        {
            foreach (var attachment in message.Attachments)
            {
                builder.Attachments.Add(attachment.FileName, attachment.Content,
                    ContentType.Parse(attachment.ContentType));
            }
        }

        email.Body = builder.ToMessageBody();

        using var client = new SmtpClient();

        var secureSocket = _settings.UseSsl
            ? SecureSocketOptions.StartTls
            : SecureSocketOptions.None;

        await client.ConnectAsync(_settings.Host, _settings.Port, secureSocket, ct);

        if (!string.IsNullOrEmpty(_settings.UserName))
        {
            await client.AuthenticateAsync(_settings.UserName, _settings.Password ?? string.Empty, ct);
        }

        await client.SendAsync(email, ct);
        await client.DisconnectAsync(true, ct);

        _logger.LogInformation("Email sent to {Recipient} — Subject: {Subject}", message.To, message.Subject);
    }
}
