using System.Text.Json;
using System.Text.Json.Nodes;
using Nullbox.Core.Jev;

namespace Nullbox.Tests.Jev;

public class JevModelTests
{
    [Fact]
    public void Request_fixture_round_trips_to_the_same_json()
    {
        var json = JevTestSupport.Fixture("request.json");

        var request = JsonSerializer.Deserialize<JevRequest>(json, JevJson.Options)!;
        var again = JsonSerializer.SerializeToNode(request, JevJson.Options);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), again), again!.ToJsonString());
        Assert.IsType<JevChoiceQuestion>(request.Questions["category"]);
        Assert.IsType<JevScoreQuestion>(request.Questions["urgency"]);
        Assert.IsType<JevNoulQuestion>(request.Questions["is_phishing"]);
    }

    [Fact]
    public void Response_fixture_parses_every_answer_type()
    {
        var result = JsonSerializer.Deserialize<JevResult>(JevTestSupport.Fixture("response.json"), JevJson.Options)!;

        Assert.Equal("jev-1.14.0", result.Model);
        Assert.Equal(120, result.Usage.InputTokens);
        Assert.Equal(12, result.Usage.OutputTokens);

        var category = result.Answer<JevChoiceAnswer>("category")!;
        Assert.Equal("promo", category.Choice);
        Assert.Equal(0.7, category.Confidence);
        Assert.Equal(0.8, category.Probabilities["promo"]);

        var urgency = result.Answer<JevScoreAnswer>("urgency")!;
        Assert.Equal(1.7, urgency.Score);
        Assert.Equal("Can wait", urgency.Legend["0"]!.GetValue<string>());
        Assert.Equal(0.8, urgency.Probabilities["2"]);

        Assert.Equal(0.02, result.Answer<JevNoulAnswer>("is_phishing")!.Noul);
    }

    [Fact]
    public void Response_fixture_round_trips_to_the_same_json()
    {
        var json = JevTestSupport.Fixture("response.json");
        var result = JsonSerializer.Deserialize<JevResult>(json, JevJson.Options)!;

        var again = JsonSerializer.SerializeToNode(result, JevJson.Options);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), again), again!.ToJsonString());
    }

    [Fact]
    public void Unknown_answer_type_is_kept_raw_instead_of_failing()
    {
        const string json = """{"model":"jev-2","answers":{"where":{"type":"box","box":[1,2,3,4]}},"usage":{"input_tokens":5}}""";

        var result = JsonSerializer.Deserialize<JevResult>(json, JevJson.Options)!;

        var unknown = Assert.IsType<JevUnknownAnswer>(result.Answers["where"]);
        Assert.Equal("box", unknown.Type);
        Assert.Equal(4, unknown.Raw.GetProperty("box").GetArrayLength());
        Assert.Contains("\"box\":[1,2,3,4]", JsonSerializer.Serialize(result, JevJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public void Question_names_and_option_names_are_not_renamed()
    {
        var request = new JevRequest
        {
            State = "x",
            Questions = new Dictionary<string, JevQuestion>
            {
                ["CamelName"] = new JevChoiceQuestion { Criteria = new Dictionary<string, JsonNode?> { ["OptionA"] = "a" } },
            },
        };

        var json = JsonSerializer.Serialize(request, JevJson.Options);

        Assert.Contains("\"CamelName\"", json, StringComparison.Ordinal);
        Assert.Contains("\"OptionA\"", json, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"choice\"", json, StringComparison.Ordinal);
    }
}
