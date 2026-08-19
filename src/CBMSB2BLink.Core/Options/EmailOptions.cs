namespace CBMSB2BLink.Core.Options;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public bool EnableOnFailure { get; set; } = true;

    public string SmtpHost { get; set; } = string.Empty;

    public int SmtpPort { get; set; } = 25;

    public bool UseSsl { get; set; }

    public string? SmtpUsername { get; set; }

    public string? SmtpPassword { get; set; }

    public string From { get; set; } = "CBMSB2BLink@bank.local";

    public string[] To { get; set; } = System.Array.Empty<string>();
}
