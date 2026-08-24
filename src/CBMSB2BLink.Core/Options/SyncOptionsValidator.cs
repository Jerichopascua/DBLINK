using Microsoft.Extensions.Options;

namespace CBMSB2BLink.Core.Options;

/// <summary>
/// SyncOptions.Jobs is a nested collection — the built-in ValidateDataAnnotations()
/// does not recurse into nested objects/collections, so [Required]/[Range] attributes
/// on SyncJobOptions/SourceJobOptions/TargetJobOptions would silently never run.
/// Validation for every job's required fields lives here instead.
/// </summary>
public sealed class SyncOptionsValidator : IValidateOptions<SyncOptions>
{
    public ValidateOptionsResult Validate(string? name, SyncOptions options)
    {
        if (options.MaxRunDurationSeconds is < 1 or > 86_400)
        {
            return ValidateOptionsResult.Fail("Sync:MaxRunDurationSeconds must be between 1 and 86400.");
        }

        if (options.Jobs is null || options.Jobs.Count == 0)
        {
            return ValidateOptionsResult.Fail("Sync:Jobs must have at least one job configured.");
        }

        foreach (var job in options.Jobs)
        {
            if (string.IsNullOrWhiteSpace(job.JobKey))
            {
                return ValidateOptionsResult.Fail("Sync:Jobs has an entry with an empty JobKey.");
            }

            var prefix = $"Sync:Jobs[{job.JobKey}]";

            if (string.IsNullOrWhiteSpace(job.Source?.ConnectionString))
            {
                return ValidateOptionsResult.Fail($"{prefix}: Source:ConnectionString is required.");
            }

            if (string.IsNullOrWhiteSpace(job.Source.CommandText))
            {
                return ValidateOptionsResult.Fail($"{prefix}: Source:CommandText is required.");
            }

            if (string.IsNullOrWhiteSpace(job.Target?.ConnectionString))
            {
                return ValidateOptionsResult.Fail($"{prefix}: Target:ConnectionString is required.");
            }

            if (string.IsNullOrWhiteSpace(job.Target.Table))
            {
                return ValidateOptionsResult.Fail($"{prefix}: Target:Table is required.");
            }

            if (job.Target.Columns is null || job.Target.Columns.Count == 0)
            {
                return ValidateOptionsResult.Fail($"{prefix}: Target:Columns must have at least one column.");
            }

            if (job.BatchSize is < 1 or > 100_000)
            {
                return ValidateOptionsResult.Fail($"{prefix}: BatchSize must be between 1 and 100000.");
            }

            if (job.BatchAllowedMaxRecord < job.BatchSize || job.BatchAllowedMaxRecord > 10_000_000)
            {
                return ValidateOptionsResult.Fail($"{prefix}: BatchAllowedMaxRecord must be at least BatchSize ({job.BatchSize}) and at most 10000000.");
            }

            if (job.CommandTimeoutSeconds is < 1 or > 3600)
            {
                return ValidateOptionsResult.Fail($"{prefix}: CommandTimeoutSeconds must be between 1 and 3600.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
