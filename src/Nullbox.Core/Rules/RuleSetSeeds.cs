namespace Nullbox.Core.Rules;

/// <summary>Example rule sets added on first start (both dry runs).</summary>
public static class RuleSetSeeds
{
    public const string PromoCleanup = "promo-cleanup";
    public const string ImportantMailGuard = "important-mail-guard";

    public static IReadOnlyList<string> Names { get; } = [PromoCleanup, ImportantMailGuard];

    public static string Json(string name) => RuleSetSchema.ReadResource($"Nullbox.Core.Rules.Seeds.{name}.json");

    public static RuleSetDocument Load(string name) => RuleJson.ParseOrThrow(Json(name));

    public static IReadOnlyList<RuleSetDocument> All() => [.. Names.Select(Load)];
}
