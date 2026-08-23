using System;
using System.Linq;
using System.Threading.Tasks;
using CBMSB2BLink.App.Infrastructure;
using CBMSB2BLink.Core;
using CBMSB2BLink.Core.Abstractions;
using CBMSB2BLink.Core.Models;
using CBMSB2BLink.Core.Options;
using CBMSB2BLink.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

namespace CBMSB2BLink.App;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var host = Host.CreateDefaultBuilder(args)
            .UseSerilog((context, services, loggerConfiguration) =>
                loggerConfiguration.ReadFrom.Configuration(context.Configuration))
            .ConfigureServices((context, services) =>
            {
                services.AddOptions<SyncOptions>()
                    .Bind(context.Configuration.GetSection(SyncOptions.SectionName))
                    .ValidateOnStart();
                services.AddSingleton<IValidateOptions<SyncOptions>, SyncOptionsValidator>();

                services.AddOptions<EmailOptions>()
                    .Bind(context.Configuration.GetSection(EmailOptions.SectionName));

                services.AddCbmsB2BLinkData();
                services.AddSingleton<IRunLock, FileRunLock>();
                services.AddSingleton<INotificationService, EmailNotificationService>();
                services.AddSingleton<SyncEngine>();
            })
            .Build();

        try
        {
            using var cts = new System.Threading.CancellationTokenSource();
            System.Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cts.Cancel();
            };

            var syncOptions = host.Services.GetRequiredService<IOptions<SyncOptions>>().Value;
            cts.CancelAfter(TimeSpan.FromSeconds(syncOptions.MaxRunDurationSeconds));

            var engine = host.Services.GetRequiredService<SyncEngine>();
            var results = await engine.RunAsync(cts.Token);

            return results.Any(r => r.Status == SyncRunStatus.Failed) ? 1 : 0;
        }
        catch (OptionsValidationException ex)
        {
            Log.Fatal(ex, "Configuration validation failed: {Message}", string.Join("; ", ex.Failures));
            return 1;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "CBMSB2BLink terminated unexpectedly.");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
