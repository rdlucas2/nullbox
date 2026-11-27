using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Util.Store;
using Nullbox.Core.Gmail;
using Nullbox.Gmail.Auth;

// nullbox-auth: one-time Google consent on a machine with a browser (loopback redirect, like google-auth's
// run_local_server). Writes an authorized-user token.json that nullbox (and emailBlaster) can use in a container.
const string Usage = """
    Usage: nullbox-auth [--credentials PATH] [--out PATH] [--scopes LIST] [--filters]

      --credentials  OAuth desktop client file (default: data/google/credentials.json)
      --out          Token file to write (default: data/google/token.json, or token_filters.json with --filters)
      --scopes       Comma list: modify (default), full, readonly, settings, or full scope URLs
      --filters      Make the optional filters token (gmail.settings.basic) instead of the mail token
    """;

string? credentials = null, output = null, scopes = null;
var filters = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--credentials" when i + 1 < args.Length: credentials = args[++i]; break;
        case "--out" when i + 1 < args.Length: output = args[++i]; break;
        case "--scopes" when i + 1 < args.Length: scopes = args[++i]; break;
        case "--filters": filters = true; break;
        case "-h" or "--help":
            Console.WriteLine(Usage);
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument '{args[i]}'.\n{Usage}");
            return 2;
    }
}

var files = new GoogleCredentialFiles("data");
credentials ??= files.ClientPath;
output ??= files.TokenPath(filters ? GmailTokenKind.Filters : GmailTokenKind.Primary);

try
{
    var client = GoogleOAuthClient.Parse(await File.ReadAllTextAsync(credentials));
    var requested = GmailScopes.Resolve(scopes, filters);

    var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
    {
        ClientSecrets = new ClientSecrets { ClientId = client.ClientId, ClientSecret = client.ClientSecret },
        Scopes = requested,
        DataStore = new NullDataStore(),
        Prompt = "consent", // always return a refresh token, even if this client was approved before
    });

    Console.WriteLine($"Opening a browser for Google consent ({string.Join(' ', requested)}).");
    Console.WriteLine("If it does not open, copy the URL it prints into a browser on this machine.");
    var credential = await new AuthorizationCodeInstalledApp(flow, new LocalServerCodeReceiver())
        .AuthorizeAsync("user", CancellationToken.None);

    var token = AuthorizedUserToken.FromResponse(credential.Token, client.ClientId, client.ClientSecret, requested);
    GoogleCredentialFiles.WriteAtomically(output, token.ToJson());
    Console.WriteLine($"Wrote {output} (owner read/write only). Copy it to the server's data/google/ folder.");
    return 0;
}
catch (Exception ex) when (ex is GmailCredentialFormatException or ArgumentException or FileNotFoundException or DirectoryNotFoundException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
