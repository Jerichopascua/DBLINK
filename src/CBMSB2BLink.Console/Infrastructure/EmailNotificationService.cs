using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Abstractions;
using CBMSB2BLink.Core.Models;
using CBMSB2BLink.Core.Options;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace CBMSB2BLink.App.Infrastructure;

public sealed class EmailNotificationService : INotificationService
{
    private readonly EmailOptions _options;
    private readonly ILogger<EmailNotificationService> _logger;

    public EmailNotificationService(IOptions<EmailOptions> options, ILogger<EmailNotificationService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendFailureAsync(SyncRunResult result, CancellationToken cancellationToken)
    {
        if (!_options.EnableOnFailure || _options.To.Length == 0)
        {
            _logger.LogInformation("Failure email suppressed (EnableOnFailure={Enabled}, recipients={Count}).",
                _options.EnableOnFailure, _options.To.Length);
            return;
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_options.From));
        foreach (var to in _options.To)
        {
            message.To.Add(MailboxAddress.Parse(to));
        }

        message.Subject = $"[CBMSB2BLink] Sync FAILED for {result.SyncKey} on {result.HostMachine}";
        message.Body = new TextPart("plain")
        {
            Text =
                $"CBMSB2BLink sync run failed.\n\n" +
                $"SyncKey: {result.SyncKey}\n" +
                $"Host: {result.HostMachine}\n" +
                $"Started (UTC): {result.StartedUtc:u}\n" +
                $"Completed (UTC): {result.CompletedUtc:u}\n" +
                $"RecordsRead: {result.RecordsRead}\n" +
                $"RecordsInserted: {result.RecordsInserted}\n\n" +
                $"Error:\n{result.ErrorMessage}\n"
        };

        using var client = new SmtpClient();
        await client.ConnectAsync(_options.SmtpHost, _options.SmtpPort, _options.UseSsl, cancellationToken);

        if (!string.IsNullOrEmpty(_options.SmtpUsername))
        {
            await client.AuthenticateAsync(_options.SmtpUsername, _options.SmtpPassword ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}
