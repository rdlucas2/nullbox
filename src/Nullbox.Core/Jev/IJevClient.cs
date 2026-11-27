namespace Nullbox.Core.Jev;

/// <summary>Asks Jev to judge one state. Jev supplies judgments only; nullbox code owns the decisions.</summary>
public interface IJevClient
{
    /// <param name="request">The state and questions to send.</param>
    /// <param name="context">Identifies the message and rule-set version, which keys the result cache. Null skips the cache.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<JevResult> EvaluateAsync(JevRequest request, JevCallContext? context = null, CancellationToken cancellationToken = default);
}

/// <summary>What a call is for: re-evaluating the same message with the same rule-set version and model hits the cache.</summary>
public sealed record JevCallContext(string MessageId, string RuleSetVersion);

/// <summary>Usage against the configured Jev budget, for the dashboard warning.</summary>
public sealed record JevBudgetStatus(
    long InputTokensToday,
    long MaxInputTokensPerDay,
    int RequestsLastHour,
    int MaxRequestsPerHour)
{
    public bool OverBudget => InputTokensToday >= MaxInputTokensPerDay || RequestsLastHour >= MaxRequestsPerHour;
}

public interface IJevBudget
{
    Task<JevBudgetStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}
