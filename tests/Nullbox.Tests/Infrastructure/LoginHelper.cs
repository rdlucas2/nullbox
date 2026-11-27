using System.Text.RegularExpressions;

namespace Nullbox.Tests.Infrastructure;

public static partial class LoginHelper
{
    /// <summary>GETs the login page and returns the antiforgery form field it issued (the cookie stays in the client).</summary>
    public static async Task<KeyValuePair<string, string>> GetAntiforgeryFieldAsync(HttpClient client)
    {
        var page = await client.GetStringAsync("/login");
        var token = TokenField().Match(page);
        Assert.True(token.Success, "login page should carry an antiforgery token");
        return new(token.Groups["name"].Value, token.Groups["value"].Value);
    }

    /// <summary>POSTs the password to /login with a fresh antiforgery token.</summary>
    public static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string password, string? returnUrl = null)
    {
        var form = new Dictionary<string, string>([await GetAntiforgeryFieldAsync(client)])
        {
            ["password"] = password,
        };
        if (returnUrl is not null)
        {
            form["returnUrl"] = returnUrl;
        }

        return await client.PostAsync("/login", new FormUrlEncodedContent(form));
    }

    [GeneratedRegex("""name="(?<name>__RequestVerificationToken)" value="(?<value>[^"]+)""")]
    private static partial Regex TokenField();
}
