using CBMSB2BLink.Monitoring.Api;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, loggerConfiguration) =>
    loggerConfiguration.ReadFrom.Configuration(context.Configuration));

builder.Services.AddOptions<ConnectionStringsOptions>()
    .Bind(builder.Configuration.GetSection(ConnectionStringsOptions.SectionName));

builder.Services.AddOptions<DashboardOptions>()
    .Bind(builder.Configuration.GetSection(DashboardOptions.SectionName));

builder.Services.PostConfigure<DashboardOptions>(options =>
{
    if (options.SyncKeys.Length == 0)
    {
        options.SyncKeys = new[] { "BCB_NEW" };
    }
});

builder.Services.AddSingleton<SyncStatusReader>();

var app = builder.Build();

var cbmsConnectionString = app.Services.GetRequiredService<IOptions<ConnectionStringsOptions>>().Value.Cbms;
if (string.IsNullOrWhiteSpace(cbmsConnectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Cbms is required.");
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/status", async (string? syncKey, SyncStatusReader reader, CancellationToken cancellationToken) =>
{
    var key = syncKey ?? reader.ConfiguredSyncKeys.FirstOrDefault() ?? "BCB_NEW";
    var status = await reader.GetStatusAsync(key, cancellationToken);
    return status is null ? Results.NotFound(new { message = $"No sync history for '{key}'." }) : Results.Ok(status);
});

app.MapGet("/api/runs", async (string? syncKey, int? take, SyncStatusReader reader, CancellationToken cancellationToken) =>
{
    var key = syncKey ?? reader.ConfiguredSyncKeys.FirstOrDefault() ?? "BCB_NEW";
    var runs = await reader.GetRecentRunsAsync(key, Math.Clamp(take ?? 50, 1, 500), cancellationToken);
    return Results.Ok(runs);
});

app.MapGet("/api/sync-keys", (SyncStatusReader reader) => Results.Ok(reader.ConfiguredSyncKeys));

app.Run();
