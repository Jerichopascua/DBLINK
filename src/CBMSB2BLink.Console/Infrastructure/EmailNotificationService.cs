using System.Linq;
using System.Collections.Generic;
using System.Text;
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

    public async Task SendFailureAsync(IReadOnlyList<SyncRunResult> failedResults, CancellationToken cancellationToken)
    {
        if (!_options.EnableOnFailure || _options.To.Length == 0 || failedResults.Count == 0)
        {
            _logger.LogInformation(
                "Failure email suppressed (EnableOnFailure={Enabled}, recipients={Count}, failedJobs={FailedCount}).",
                _options.EnableOnFailure, _options.To.Length, failedResults.Count);
            return;
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_options.From));
        foreach (var to in _options.To)
        {
            message.To.Add(MailboxAddress.Parse(to));
        }

        var jobList = string.Join(", ", failedResults.Select(r => r.SyncKey));
        message.Subject = $"[CBMSB2BLink] Sync FAILED for {failedResults.Count} job(s): {jobList}";

        var body = new StringBuilder();
        body.AppendLine("CBMSB2BLink sync run had one or more failed jobs.");
        body.AppendLine();
        foreach (var result in failedResults)
        {
            body.AppendLine($"JobKey: {result.SyncKey}");
            body.AppendLine($"Host: {result.HostMachine}");
            body.AppendLine($"Started (UTC): {result.StartedUtc:u}");
            body.AppendLine($"Completed (UTC): {result.CompletedUtc:u}");
            body.AppendLine($"RecordsRead: {result.RecordsRead}");
            body.AppendLine($"RecordsInserted: {result.RecordsInserted}");
            body.AppendLine("Error:");
            body.AppendLine(result.ErrorMessage);
            body.AppendLine(new string('-', 40));
        }

        message.Body = new TextPart("plain") { Text = body.ToString() };

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
