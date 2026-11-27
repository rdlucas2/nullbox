using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Http;
using Google.Apis.Services;
using Nullbox.Core.Gmail;

namespace Nullbox.Gmail.Auth;

/// <summary>Hands out <see cref="GmailService"/> instances that authenticate with the stored token.</summary>
public interface IGmailClientFactory
{
    GmailService Create(GmailTokenKind kind = GmailTokenKind.Primary);
}

/// <summary>
/// Hands out Gmail clients over the stored tokens. A non-null base URI (testing only, <see cref="GmailOptions.ApiBaseUri"/>)
/// points them at a Gmail API stand-in instead of Google.
/// </summary>
public sealed class GmailClientFactory(GmailCredentialStore store, Google.Apis.Http.IHttpClientFactory? httpClientFactory = null, string? baseUri = null) : IGmailClientFactory
{
    public GmailService Create(GmailTokenKind kind = GmailTokenKind.Primary)
    {
        var initializer = new BaseClientService.Initializer
        {
            HttpClientInitializer = new StoredCredentialInitializer(store, kind),
            HttpClientFactory = httpClientFactory ?? new Google.Apis.Http.HttpClientFactory(),
            ApplicationName = "nullbox",
        };
        if (!string.IsNullOrWhiteSpace(baseUri))
        {
            initializer.BaseUri = baseUri.EndsWith('/') ? baseUri : baseUri + "/";
        }

        return new GmailService(initializer);
    }
}

/// <summary>
/// Resolves the credential per request rather than at construction, so a token uploaded on the settings page is
/// picked up without a restart, and maps Google's <c>invalid_grant</c> to <see cref="GmailReauthorizationRequiredException"/>.
/// </summary>
internal sealed class StoredCredentialInitializer(GmailCredentialStore store, GmailTokenKind kind)
    : IConfigurableHttpClientInitializer, IHttpExecuteInterceptor, IHttpUnsuccessfulResponseHandler
{
    public void Initialize(ConfigurableHttpClient httpClient)
    {
        httpClient.MessageHandler.AddExecuteInterceptor(this);
        httpClient.MessageHandler.AddUnsuccessfulResponseHandler(this);
    }

    public async Task InterceptAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var credential = store.GetCredential(kind);
        try
        {
            await credential.InterceptAsync(request, cancellationToken);
        }
        catch (TokenResponseException ex) when (IsRejected(ex))
        {
            store.MarkRejected(kind, ex.Error.Error);
            throw new GmailReauthorizationRequiredException(kind, ex.Error.Error, ex);
        }
    }

    public async Task<bool> HandleResponseAsync(HandleUnsuccessfulResponseArgs args)
    {
        var credential = store.GetCredential(kind);
        try
        {
            return await credential.HandleResponseAsync(args);
        }
        catch (TokenResponseException ex) when (IsRejected(ex))
        {
            store.MarkRejected(kind, ex.Error.Error);
            throw new GmailReauthorizationRequiredException(kind, ex.Error.Error, ex);
        }
    }

    /// <summary>
    /// Errors that retrying cannot fix. <c>deleted_client</c> means the OAuth client itself is gone (Google deletes
    /// clients left unused for months), so the refresh token is dead too.
    /// </summary>
    private static bool IsRejected(TokenResponseException ex)
        => ex.Error?.Error is "invalid_grant" or "invalid_client" or "unauthorized_client" or "deleted_client";
}
