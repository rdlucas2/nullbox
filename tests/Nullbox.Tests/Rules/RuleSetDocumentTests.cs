using System.Text.Json.Nodes;
using Nullbox.Core.Gmail;
using Nullbox.Core.Jev;
using Nullbox.Core.Rules;

namespace Nullbox.Tests.Rules;

public sealed class RuleSetDocumentTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Rules", name));

    private static RuleSetDocument Minimal(params DecisionRule[] decisions) => new()
    {
        Name = "test",
        Questions = new Dictionary<string, JevQuestion>
        {
            ["category"] = new JevChoiceQuestion { Instructions = "Kind?", Criteria = new Dictionary<string, JsonNode?> { ["promo"] = "Ads", ["personal"] = "People" } },
            ["urgency"] = new JevScoreQuestion { Criteria = ["low", "mid", "high"] },
            ["spam"] = new JevNoulQuestion { Instructions = "Is spam" },
        },
        Decisions = decisions,
    };

    private static IReadOnlyList<RuleSetError> ErrorsFor(string when, params string[] actions)
        => RuleSetValidator.Validate(Minimal(new DecisionRule { When = when, Actions = actions.Length == 0 ? ["none"] : actions }));

    // ---- seeds, round trip and the Jev request ------------------------------------------------

    [Fact]
    public void Seeds_are_valid_and_round_trip()
    {
        foreach (var name in RuleSetSeeds.Names)
        {
            var json = RuleSetSeeds.Json(name);
            var document = RuleJson.Parse(json, out var errors);

            Assert.Empty(errors);
            Assert.NotNull(document);
            Assert.Equal(name, document.Name);
            Assert.Equal(RuleSetMode.DryRun, document.Mode);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(RuleJson.Serialize(document))), $"{name} should round-trip unchanged");
        }
    }

    [Fact]
    public void Jev_request_for_the_promo_seed_matches_the_fixture()
    {
        var message = new GmailMessage(
            Id: "18c2f",
            ThreadId: "18c2f",
            From: "Deals <deals@shop.example>",
            Subject: "48 hours only: 30% off everything",
            Date: "Tue, 24 Nov 2026 09:15:00 +0000",
            ListUnsubscribe: "<mailto:unsubscribe@shop.example>",
            LabelIds: ["INBOX", "UNREAD", "CATEGORY_PROMOTIONS"],
            Snippet: "Our biggest sale of the season starts now",
            InternalDate: new DateTimeOffset(2026, 11, 24, 9, 15, 0, TimeSpan.Zero),
            Body: "never sent: the promo rule set does not include the body");

        var request = JevRequestBuilder.Build(RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup), message, new DateTimeOffset(2026, 11, 27, 8, 0, 0, TimeSpan.Zero));
        var actual = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(request, JevJson.Options));

        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(Fixture("promo-cleanup.request.json")), actual),
            actual!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void Request_matches_the_documented_jev_shapes()
    {
        var request = JsonNode.Parse(Fixture("promo-cleanup.request.json"))!;
        var guard = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(
            JevRequestBuilder.Build(RuleSetSeeds.Load(RuleSetSeeds.ImportantMailGuard), Message(body: "Hi"), DateTimeOffset.UnixEpoch),
            JevJson.Options))!;

        Assert.Equal("jev-latest", request["model"]!.GetValue<string>());
        Assert.Equal("choice", request["questions"]!["category"]!["type"]!.GetValue<string>());
        Assert.IsType<JsonObject>(request["questions"]!["category"]!["criteria"]);
        Assert.Equal("noul", request["questions"]!["from_a_person"]!["type"]!.GetValue<string>());
        Assert.Equal("score", guard["questions"]!["urgency"]!["type"]!.GetValue<string>());
        Assert.IsType<JsonArray>(guard["questions"]!["urgency"]!["criteria"]);
        Assert.IsType<JsonObject>(guard["questions"]!["needs_reply"]!["criteria"]);
        Assert.NotNull(guard["questions"]!["needs_reply"]!["criteria"]!["true"]);
    }

    [Fact]
    public void State_sends_only_the_template_fields_with_the_body_cut()
    {
        var template = new StateTemplate { Fields = [StateFields.Body, StateFields.AgeDays, StateFields.Labels], BodyMaxChars = 10 };

        var state = JevRequestBuilder.BuildState(template, Message(body: new string('x', 50)), new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(["body", "age_days", "labels"], state.Select(p => p.Key));
        Assert.Equal(new string('x', 9) + "…", state["body"]!.GetValue<string>());
        Assert.Equal(3, state["age_days"]!.GetValue<int>());
    }

    private static GmailMessage Message(string? body) => new(
        "id", null, "a@b.example", "Hi", null, null, ["INBOX"], "snippet", new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero), body);

    // ---- schema -------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"questions":{"a":{"type":"noul"}}}""", "")]
    [InlineData("""{"name":"Bad Name","questions":{"a":{"type":"noul"}}}""", "/name")]
    [InlineData("""{"name":"x","questions":{}}""", "/questions")]
    [InlineData("""{"name":"x","mode":"yolo","questions":{"a":{"type":"noul"}}}""", "/mode")]
    [InlineData("""{"name":"x","questions":{"a":{"type":"choice","criteria":{"only":"one"}}}}""", "/questions/a/criteria")]
    [InlineData("""{"name":"x","questions":{"a":{"type":"score","criteria":"low"}}}""", "/questions/a/criteria")]
    [InlineData("""{"name":"x","questions":{"a":{"type":"box"}}}""", "/questions/a/type")]
    [InlineData("""{"name":"x","questions":{"Bad":{"type":"noul"}}}""", "/questions")]
    [InlineData("""{"name":"x","questions":{"a":{"type":"noul"}},"decisions":[{"when":"a.noul > 0.5","actions":["delete"]}]}""", "/decisions/0/actions/0")]
    [InlineData("""{"name":"x","questions":{"a":{"type":"noul"}},"state":{"fields":["password"]}}""", "/state/fields/0")]
    [InlineData("""{"name":"x","questions":{"a":{"type":"noul"}},"extra":1}""", "/extra")]
    [InlineData("""{"name":"x","questions":{"a":{"type":"noul","criteria":{"maybe":"?"}}}}""", "/questions/a/criteria/maybe")]
    public void Schema_rejects_malformed_documents(string json, string path)
    {
        var errors = RuleSetSchema.Validate(json);

        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Path == path);
        Assert.Null(RuleJson.Parse(json, out _));
    }

    [Fact]
    public void Schema_accepts_structured_instructions_and_criteria()
    {
        const string json = """
            {
              "name": "structured",
              "questions": {
                "kind": { "criteria": { "a": { "examples": ["x"] }, "b": ["y", "z"] }, "type": "choice", "instructions": { "focus": "sender" } },
                "level": { "type": "score", "criteria": [null, { "label": "high" }] }
              },
              "decisions": [ { "when": "kind.choice == \"a\" || level.score > 0.5", "actions": ["label:x"] } ]
            }
            """;

        var document = RuleJson.Parse(json, out var errors);

        Assert.Empty(errors);
        Assert.IsType<JevChoiceQuestion>(document!.Questions["kind"]);
    }

    [Fact]
    public void Invalid_json_is_reported_not_thrown()
    {
        Assert.Null(RuleJson.Parse("{ not json", out var errors));
        Assert.StartsWith("Not valid JSON", Assert.Single(errors).Message);
    }

    [Fact]
    public void Published_schema_is_valid_json_and_names_every_state_field()
    {
        var schema = JsonNode.Parse(RuleSetSchema.Text)!;
        var fields = schema["properties"]!["state"]!["properties"]!["fields"]!["items"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>());

        Assert.Equal(StateFields.All, fields);
    }

    // ---- references between decisions and questions -------------------------------------------

    [Theory]
    [InlineData("category.choice == \"promo\" && category.confidence >= 0.8")]
    [InlineData("category.probabilities.personal < 0.1")]
    [InlineData("urgency.score >= 1.5 && urgency.probabilities.2 > 0.5")]
    [InlineData("spam.noul >= 0.9")]
    [InlineData("spam.noul < 0.5 && category.choice != \"personal\"")] // screen with a noul first
    public void Valid_conditions_pass(string when)
        => Assert.Empty(ErrorsFor(when));

    [Theory]
    [InlineData("nope.noul > 0.5", "no question named 'nope'")]
    [InlineData("category.choice == \"news\"", "'news' is not an option")]
    [InlineData("category.choice > 1", "compare a choice with == or !=")]
    [InlineData("category.probabilities.news > 0.5", "'news' is not an option")]
    [InlineData("category.score > 1", "'score' is not an answer field here")]
    [InlineData("category.confidence > 1.5", "between 0 and 1")]
    [InlineData("urgency.score > 3", "between 0 and 2")]
    [InlineData("urgency.probabilities.3 > 0.1", "numbered 0 to 2")]
    [InlineData("urgency.score == \"high\"", "compare with a number")]
    [InlineData("spam.confidence > 0.5", "use noul")]
    [InlineData("spam.noul >", "Expected a number")]
    public void Conditions_must_refer_to_real_questions_fields_and_options(string when, string message)
    {
        var error = Assert.Single(ErrorsFor(when));

        Assert.Equal("/decisions/0/when", error.Path);
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData(new[] { "none", "star" }, "none cannot be combined")]
    [InlineData(new[] { "star", "star" }, "only be listed once")]
    [InlineData(new[] { "label:" }, "needs a label name")]
    public void Actions_are_checked(string[] actions, string message)
        => Assert.Contains(ErrorsFor("spam.noul > 0.5", actions), e => e.Message.Contains(message, StringComparison.Ordinal));

    [Fact]
    public void Validation_errors_block_parsing_and_name_the_path()
    {
        const string json = """{"name":"x","questions":{"a":{"type":"noul"}},"decisions":[{"when":"b.noul > 0.5","actions":["star"]}]}""";

        var ex = Assert.Throws<RuleSetValidationException>(() => RuleJson.ParseOrThrow(json));

        Assert.Equal("/decisions/0/when", Assert.Single(ex.Errors).Path);
    }

    [Fact]
    public void Hash_ignores_the_schema_pointer_and_formatting()
    {
        var seed = RuleSetSeeds.Load(RuleSetSeeds.PromoCleanup);

        Assert.Equal(RuleJson.Hash(seed), RuleJson.Hash(seed with { Schema = null }));
        Assert.NotEqual(RuleJson.Hash(seed), RuleJson.Hash(seed with { Mode = RuleSetMode.Live }));
    }
}
