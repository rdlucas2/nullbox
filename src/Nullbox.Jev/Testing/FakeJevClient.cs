using System.Collections.Concurrent;
using Nullbox.Core.Jev;

namespace Nullbox.Jev.Testing;

/// <summary>
/// Scriptable <see cref="IJevClient"/> for tests and offline runs: answers come from a responder function,
/// and every call is recorded. Never touches the network.
/// </summary>
public sealed class FakeJevClient : IJevClient
{
    private readonly ConcurrentQueue<FakeJevCall> _calls = new();

    public FakeJevClient(Func<JevRequest, JevResult>? responder = null)
    {
        Responder = responder ?? DefaultResponder;
    }

    public Func<JevRequest, JevResult> Responder { get; set; }

    public IReadOnlyList<FakeJevCall> Calls => [.. _calls];

    public Task<JevResult> EvaluateAsync(JevRequest request, JevCallContext? context = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _calls.Enqueue(new FakeJevCall(request, context));
        return Task.FromResult(Responder(request));
    }

    /// <summary>
    /// Neutral answers: first option of each choice (confidence 1), lowest score level, noul 0.
    /// Usage is the request's token estimate, so the cost guard sees realistic numbers.
    /// </summary>
    public static JevResult DefaultResponder(JevRequest request) => new()
    {
        Model = "jev-fake",
        Usage = new JevUsage { InputTokens = JevStateTruncator.EstimateTokens(request) },
        Answers = request.Questions.ToDictionary(
            q => q.Key,
            q => q.Value switch
            {
                JevChoiceQuestion c => (JevAnswer)new JevChoiceAnswer
                {
                    Choice = c.Criteria.Keys.First(),
                    Confidence = 1,
                    Probabilities = c.Criteria.Keys.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i == 0 ? 1.0 : 0.0),
                },
                JevScoreQuestion => new JevScoreAnswer { Score = 0, Confidence = 1 },
                _ => new JevNoulAnswer { Noul = 0 },
            }),
    };
}

public sealed record FakeJevCall(JevRequest Request, JevCallContext? Context);
