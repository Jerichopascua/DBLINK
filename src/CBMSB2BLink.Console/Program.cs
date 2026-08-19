using System;
using System.ComponentModel.DataAnnotations;
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

// CBMSB2BLink is a Windows Task Scheduler console app (DPAPI secret protection is
// Windows-only); this silences the cross-platform CA1416 analyzer for that call site.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

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
                services.AddOptions<ConnectionStringsOptions>()
                    .Bind(context.Configuration.GetSection(ConnectionStringsOptions.SectionName))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();

                services.AddOptions<SyncOptions>()
                    .Bind(context.Configuration.GetSection(SyncOptions.SectionName))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();

                services.AddOptions<EmailOptions>()
                    .Bind(context.Configuration.GetSection(EmailOptions.SectionName));

                // Decrypt "DPAPI:<blob>" connection strings after binding, before any repository uses them.
                services.PostConfigure<ConnectionStringsOptions>(options =>
                {
                    if (DpapiProtector.IsProtected(options.CcrisB2B))
                    {
                        options.CcrisB2B = DpapiProtector.Unprotect(options.CcrisB2B);
                    }

                    if (DpapiProtector.IsProtected(options.Cbms))
                    {
                        options.Cbms = DpapiProtector.Unprotect(options.Cbms);
                    }
                });

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
            var result = await engine.RunAsync(cts.Token);

            return result.Status == SyncRunStatus.Failed ? 1 : 0;
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
