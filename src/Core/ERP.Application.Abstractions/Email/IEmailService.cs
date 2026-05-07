namespace ERP.Application.Abstractions.Email;

public interface IEmailService
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

public record EmailMessage(
    string To,
    string Subject,
    string HtmlBody,
    string? PlainTextBody = null,
    IReadOnlyList<EmailAttachment>? Attachments = null);

public record EmailAttachment(string FileName, byte[] Content, string ContentType);
