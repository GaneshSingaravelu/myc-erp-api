namespace ERP.Infrastructure.Email;

public sealed class EmailSettings
{
    public const string SectionName = "Email";

    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 587;
    public string SenderEmail { get; init; } = "noreply@erp.local";
    public string SenderName { get; init; } = "ERP System";
    public string? UserName { get; init; }
    public string? Password { get; init; }
    public bool UseSsl { get; init; } = true;
}
