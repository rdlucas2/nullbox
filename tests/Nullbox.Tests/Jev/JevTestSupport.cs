using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Nullbox.Core.Jev;
using Nullbox.Jev;

namespace Nullbox.Tests.Jev;

/// <summary>Answers each request from a queue of scripted responses and records what was sent. No network.</summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _script = new();

    public List<(HttpRequestMessage Request, string Body)> Sent { get; } = [];

    public ScriptedHandler Respond(HttpStatusCode status, string body = "{}", Action<HttpResponseMessage>? configure = null)
    {
        _script.Enqueue((_, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            configure?.Invoke(response);
            return Task.FromResult(response);
        });
        return this;
    }

    public ScriptedHandler Hang()
    {
        _script.Enqueue(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        return this;
    }

    public ScriptedHandler Throw(Exception ex)
    {
        _script.Enqueue((_, _) => Task.FromException<HttpResponseMessage>(ex));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Sent.Add((request, body));
        if (_script.Count == 0)
        {
            throw new InvalidOperationException("No scripted response left.");
        }

        return await _script.Dequeue()(request, cancellationToken);
    }
}

internal static class JevTestSupport
{
    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Jev", name));

    public static JevOptions Options(Action<JevOptions>? configure = null)
    {
        var options = new JevOptions
        {
            ApiKey = "test-key",
            BaseUrl = new Uri("https://jev.test/"),
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            RetryMaxDelay = TimeSpan.FromMilliseconds(20),
            Timeout = TimeSpan.FromSeconds(5),
        };
        configure?.Invoke(options);
        return options;
    }

    public static HttpJevClient Client(ScriptedHandler handler, JevOptions? options = null)
        => new(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(options ?? Options()), NullLogger<HttpJevClient>.Instance);

    public static JevRequest SimpleRequest(string body = "hello") => new()
    {
        State = new JsonObject { ["from"] = "a@example.com", ["body"] = body },
        Questions = new Dictionary<string, JevQuestion>
        {
            ["is_phishing"] = new JevNoulQuestion { Instructions = "This message looks like phishing" },
        },
    };

    public static JevResult Result(long inputTokens = 100) => new()
    {
        Model = "jev-1.14.0",
        Usage = new JevUsage { InputTokens = inputTokens, OutputTokens = 1 },
        Answers = new Dictionary<string, JevAnswer> { ["is_phishing"] = new JevNoulAnswer { Noul = 0.02 } },
    };
}
