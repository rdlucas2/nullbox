using Microsoft.Extensions.Options;
using Nullbox.Core.Jobs;
using Nullbox.Core.Options;
using Nullbox.Core.Pipeline;

namespace Nullbox.Web.Pipeline;

/// <summary>Deletes decision records older than <see cref="AuditSettings.RetentionDays"/>; does nothing when that is 0 or less.</summary>
public sealed class AuditPurgeJob(IDecisionLog log, IOptions<NullboxOptions> options, TimeProvider time, ILogger<AuditPurgeJob> logger)
    : IRecurringJob
{
    public const string JobId = "audit-purge";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var days = options.Value.Audit.RetentionDays;
        if (days <= 0)
        {
            logger.LogDebug("Audit retention is off; keeping every decision record");
            return;
        }

        var cutoff = time.GetUtcNow().AddDays(-days);
        var removed = await log.PurgeAsync(cutoff, cancellationToken);
        logger.LogInformation("Purged {Count} decision record(s) older than {Cutoff:u}", removed, cutoff);
    }
}
