using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

// Fake Gmail API, token endpoint and Jev for deploy/smoke/run-smoke.sh. Every call is recorded at GET /_fake/calls,
// so the smoke test can check what nullbox did (and that a dry run changed nothing). Never for production.
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var calls = new ConcurrentQueue<string>();
string[] messageIds = ["smoke-1", "smoke-2"];

app.Use(async (context, next) =>
{
    calls.Enqueue($"{context.Request.Method} {context.Request.Path}");
    await next(context);
});

app.MapGet("/_fake/calls", () => Results.Ok(calls.ToArray()));
app.MapGet("/healthz", () => Results.Ok("ok"));

app.MapPost("/token", () => Results.Ok(new { access_token = "fake-access-token", expires_in = 3600, token_type = "Bearer" }));

var gmail = app.MapGroup("/gmail/v1/users/me");
gmail.MapGet("/profile", () => Results.Ok(new { emailAddress = "smoke@nullbox.test", messagesTotal = messageIds.Length, historyId = "1000" }));
gmail.MapGet("/history", () => Results.Ok(new { historyId = "1001" }));
gmail.MapGet("/messages", () => Results.Ok(new { messages = messageIds.Select(id => new { id, threadId = id }), resultSizeEstimate = messageIds.Length }));
gmail.MapGet("/messages/{id}", (string id) => Results.Ok(new
{
    id,
    threadId = id,
    labelIds = new[] { "INBOX", "UNREAD", "CATEGORY_PROMOTIONS" },
    snippet = "SMOKE-SNIPPET big sale",
    internalDate = DateTimeOffset.UtcNow.AddDays(-40).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
    payload = new
    {
        mimeType = "text/plain",
        headers = new[]
        {
            new { name = "From", value = "Deals <deals@shop.test>" },
            new { name = "Subject", value = "SMOKE-SUBJECT 50% off" },
            new { name = "Date", value = "Fri, 27 Nov 2026 08:00:00 +0000" },
            new { name = "List-Unsubscribe", value = "<mailto:unsub@shop.test>" },
        },
        body = new { size = 40, data = Convert.ToBase64String(Encoding.UTF8.GetBytes("SMOKE-SECRET-BODY do not log this")).TrimEnd('=').Replace('+', '-').Replace('/', '_') },
    },
}));
gmail.MapPost("/messages/batchModify", () => Results.NoContent());
gmail.MapPost("/messages/{id}/trash", (string id) => Results.Ok(new { id }));
gmail.MapPost("/messages/{id}/untrash", (string id) => Results.Ok(new { id }));
gmail.MapGet("/labels", () => Results.Ok(new { labels = new[] { new { id = "INBOX", name = "INBOX", type = "system" } } }));
gmail.MapPost("/labels", async (HttpRequest request) =>
{
    var body = await JsonNode.ParseAsync(request.Body);
    var name = body?["name"]?.GetValue<string>() ?? "label";
    return Results.Ok(new { id = "Label_" + name, name });
});

// Jev: a middling answer to every question, so the example rule sets match little and change nothing.
app.MapPost("/v1/systemone", async (HttpRequest request) =>
{
    var body = await JsonNode.ParseAsync(request.Body) as JsonObject;
    var answers = new JsonObject();
    foreach (var (name, question) in body?["questions"] as JsonObject ?? [])
    {
        answers[name] = question?["type"]?.GetValue<string>() switch
        {
            "choice" => Choice(question!["criteria"] as JsonObject),
            "score" => new JsonObject { ["type"] = "score", ["score"] = 0.0, ["confidence"] = 0.5, ["probabilities"] = new JsonObject { ["0"] = 1.0 } },
            _ => new JsonObject { ["type"] = "noul", ["noul"] = 0.1 },
        };
    }

    return Results.Ok(new JsonObject { ["model"] = "jev-fake-1.0", ["answers"] = answers, ["usage"] = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 5 } });
});

app.Run();

static JsonObject Choice(JsonObject? criteria)
{
    var first = criteria?.FirstOrDefault().Key ?? "other";
    return new JsonObject
    {
        ["type"] = "choice",
        ["choice"] = first,
        ["confidence"] = 0.4,
        ["probabilities"] = new JsonObject { [first] = 0.4 },
    };
}
