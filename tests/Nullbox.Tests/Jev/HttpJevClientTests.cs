using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nullbox.Core.Jev;
using Nullbox.Jev;

namespace Nullbox.Tests.Jev;

public class HttpJevClientTests
{
    [Fact]
    public async Task Posts_to_systemone_with_bearer_key_and_parses_the_answer()
    {
        var handler = new ScriptedHandler().Respond(HttpStatusCode.OK, JevTestSupport.Fixture("response.json"));
        var client = JevTestSupport.Client(handler);

        var result = await client.EvaluateAsync(JevTestSupport.SimpleRequest());

        var (request, body) = Assert.Single(handler.Sent);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://jev.test/v1/systemone", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("test-key", request.Headers.Authorization.Parameter);
        var sent = JsonNode.Parse(body)!;
        Assert.Equal("jev-latest", sent["model"]!.GetValue<string>());
        Assert.Equal("noul", sent["questions"]!["is_phishing"]!["type"]!.GetValue<string>());
        Assert.Equal("jev-1.14.0", result.Model);
        Assert.False(result.FromCache);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(529)]
    [InlineData(503)]
    public async Task Retries_transient_statuses_and_honours_retry_after(int status)
    {
        var handler = new ScriptedHandler()
            .Respond((HttpStatusCode)status, "{}", r => r.Headers.Add("retry-after", "0"))
            .Respond(HttpStatusCode.OK, JevTestSupport.Fixture("response.json"));
        var client = JevTestSupport.Client(handler);
        var delays = new List<TimeSpan>();
        client.RetryObserver = delays.Add;

        var result = await client.EvaluateAsync(JevTestSupport.SimpleRequest());

        Assert.Equal(2, handler.Sent.Count);
        Assert.Equal([TimeSpan.Zero], delays);
        Assert.Equal("jev-1.14.0", result.Model);
    }

    [Fact]
    public async Task Long_retry_after_is_capped_at_the_max_delay()
    {
        var handler = new ScriptedHandler()
            .Respond(HttpStatusCode.TooManyRequests, "{}", r => r.Headers.Add("retry-after", "120"))
            .Respond(HttpStatusCode.OK, JevTestSupport.Fixture("response.json"));
        var client = JevTestSupport.Client(handler, JevTestSupport.Options(o => o.RetryMaxDelay = TimeSpan.FromMilliseconds(15)));
        var delays = new List<TimeSpan>();
        client.RetryObserver = delays.Add;

        await client.EvaluateAsync(JevTestSupport.SimpleRequest());

        Assert.Equal([TimeSpan.FromMilliseconds(15)], delays);
    }

    [Fact]
    public async Task Retries_network_errors()
    {
        var handler = new ScriptedHandler()
            .Throw(new HttpRequestException("connection reset"))
            .Respond(HttpStatusCode.OK, JevTestSupport.Fixture("response.json"));

        var result = await JevTestSupport.Client(handler).EvaluateAsync(JevTestSupport.SimpleRequest());

        Assert.Equal(2, handler.Sent.Count);
        Assert.Equal("jev-1.14.0", result.Model);
    }

    [Fact]
    public async Task Gives_up_after_max_retries_with_the_last_status()
    {
        var handler = new ScriptedHandler();
        for (var i = 0; i < 3; i++)
        {
            handler.Respond((HttpStatusCode)529, """{"detail":"overloaded"}""");
        }

        var client = JevTestSupport.Client(handler, JevTestSupport.Options(o => o.MaxRetries = 2));

        var ex = await Assert.ThrowsAsync<JevApiException>(() => client.EvaluateAsync(JevTestSupport.SimpleRequest()));

        Assert.Equal((HttpStatusCode)529, ex.StatusCode);
        Assert.Equal(3, handler.Sent.Count);
    }

    [Fact]
    public async Task Max_tokens_error_is_typed_and_not_retried()
    {
        var handler = new ScriptedHandler().Respond(HttpStatusCode.BadRequest, """{"detail":{"code":"max_tokens_exceeded"}}""");

        await Assert.ThrowsAsync<JevMaxTokensExceededException>(() => JevTestSupport.Client(handler).EvaluateAsync(JevTestSupport.SimpleRequest()));

        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task Unauthorized_is_typed_and_not_retried()
    {
        var handler = new ScriptedHandler().Respond(HttpStatusCode.Unauthorized, """{"detail":"bad key"}""");

        await Assert.ThrowsAsync<JevUnauthorizedException>(() => JevTestSupport.Client(handler).EvaluateAsync(JevTestSupport.SimpleRequest()));

        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task Validation_error_lists_each_detail()
    {
        const string body = """{"detail":[{"loc":["body","questions","x","criteria"],"msg":"field required","type":"missing"}]}""";
        var handler = new ScriptedHandler().Respond(HttpStatusCode.UnprocessableEntity, body);

        var ex = await Assert.ThrowsAsync<JevValidationException>(() => JevTestSupport.Client(handler).EvaluateAsync(JevTestSupport.SimpleRequest()));

        Assert.Equal(["body.questions.x.criteria: field required"], ex.Details);
        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task Slow_answer_times_out()
    {
        var handler = new ScriptedHandler().Hang();
        var client = JevTestSupport.Client(handler, JevTestSupport.Options(o =>
        {
            o.MaxRetries = 0;
            o.Timeout = TimeSpan.FromMilliseconds(50);
        }));

        await Assert.ThrowsAsync<TimeoutException>(() => client.EvaluateAsync(JevTestSupport.SimpleRequest()));
    }

    [Fact]
    public async Task Missing_api_key_fails_before_any_call()
    {
        var handler = new ScriptedHandler();
        var client = JevTestSupport.Client(handler, JevTestSupport.Options(o => o.ApiKey = null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EvaluateAsync(JevTestSupport.SimpleRequest()));

        Assert.Empty(handler.Sent);
    }

    [Fact]
    public async Task Oversized_body_is_truncated_to_fit_the_token_limit()
    {
        var handler = new ScriptedHandler().Respond(HttpStatusCode.OK, JevTestSupport.Fixture("response.json"));
        var client = JevTestSupport.Client(handler, JevTestSupport.Options(o => o.MaxRequestTokens = 500));

        await client.EvaluateAsync(JevTestSupport.SimpleRequest(new string('x', 20_000)));

        var sent = Assert.Single(handler.Sent).Body;
        Assert.True(sent.Length / 4 <= 500, $"sent {sent.Length} bytes");
        var body = JsonNode.Parse(sent)!["state"]!["body"]!.GetValue<string>();
        Assert.EndsWith("…", body, StringComparison.Ordinal);
        Assert.Equal("a@example.com", JsonNode.Parse(sent)!["state"]!["from"]!.GetValue<string>());
    }

    [Fact]
    public void Truncator_leaves_small_requests_alone()
    {
        var request = JevTestSupport.SimpleRequest();

        var same = JevStateTruncator.Truncate(request, 60_000, out var truncated);

        Assert.False(truncated);
        Assert.Same(request, same);
    }

    [Fact]
    public void Truncator_cuts_a_root_string_state()
    {
        var request = JevTestSupport.SimpleRequest() with { State = new string('y', 10_000) };

        var cut = JevStateTruncator.Truncate(request, 300, out var truncated);

        Assert.True(truncated);
        Assert.True(JevStateTruncator.EstimateTokens(cut) <= 300);
        Assert.EndsWith("…", cut.State.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void Retry_after_accepts_http_dates()
    {
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(now.AddSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), HttpJevClient.RetryAfter(response, now));
    }

    [Fact]
    public void Sent_json_uses_snake_case_fields()
    {
        var json = JsonSerializer.Serialize(JevTestSupport.Result(), JevJson.Options);

        Assert.Contains("\"input_tokens\":100", json, StringComparison.Ordinal);
    }
}
