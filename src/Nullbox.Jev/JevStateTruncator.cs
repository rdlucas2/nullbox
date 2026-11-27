using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nullbox.Core.Jev;

namespace Nullbox.Jev;

/// <summary>Keeps a request inside Jev's context window by trimming the state's longest strings (usually the body).</summary>
public static class JevStateTruncator
{
    public const string Ellipsis = "…";
    private const int MinStringLength = 32;

    /// <summary>Rough token estimate used by the guard: UTF-8 bytes of the serialized request / 4.</summary>
    public static int EstimateTokens(JevRequest request)
        => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, JevJson.Options)) / 4;

    /// <summary>
    /// Returns the request unchanged when it fits <paramref name="maxTokens"/>; otherwise a copy whose state strings
    /// were shortened, longest first. If nothing more can be cut the best effort is returned and Jev decides.
    /// </summary>
    public static JevRequest Truncate(JevRequest request, int maxTokens, out bool truncated)
    {
        truncated = false;
        var maxBytes = (long)maxTokens * 4;
        var size = Size(request);
        if (size <= maxBytes)
        {
            return request;
        }

        if (request.State is JsonValue root && root.GetValueKind() == JsonValueKind.String)
        {
            var text = root.GetValue<string>();
            var keep = (int)Math.Max(0, text.Length - (size - maxBytes) - Ellipsis.Length - 16);
            truncated = keep < text.Length;
            return truncated ? request with { State = JsonValue.Create(text[..keep] + Ellipsis) } : request;
        }

        var state = request.State.DeepClone();
        var current = request with { State = state };

        while (size > maxBytes)
        {
            var longest = LongestString(state);
            if (longest is null)
            {
                break;
            }

            var (value, text) = longest.Value;
            var excess = size - maxBytes;
            // Work in chars; over-cut a little so multi-byte text converges quickly.
            var keep = (int)Math.Max(MinStringLength / 2, text.Length - excess - Ellipsis.Length - 16);
            if (keep >= text.Length)
            {
                break;
            }

            Replace(value, text[..keep] + Ellipsis);
            truncated = true;
            size = Size(current);
        }

        return current;
    }

    private static long Size(JevRequest request) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, JevJson.Options));

    private static (JsonValue Node, string Text)? LongestString(JsonNode? node)
    {
        (JsonValue, string)? best = null;
        Visit(node);
        return best;

        void Visit(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject obj:
                    foreach (var (_, child) in obj)
                    {
                        Visit(child);
                    }

                    break;
                case JsonArray arr:
                    foreach (var child in arr)
                    {
                        Visit(child);
                    }

                    break;
                case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                    var s = v.GetValue<string>();
                    if (s.Length > MinStringLength && (best is null || s.Length > best.Value.Item2.Length))
                    {
                        best = (v, s);
                    }

                    break;
            }
        }
    }

    private static void Replace(JsonValue node, string text)
    {
        switch (node.Parent)
        {
            case JsonObject obj:
                obj[node.GetPropertyName()] = text;
                break;
            case JsonArray arr:
                arr[node.GetElementIndex()] = text;
                break;
            default:
                throw new InvalidOperationException("Root strings are handled before the loop.");
        }
    }
}
