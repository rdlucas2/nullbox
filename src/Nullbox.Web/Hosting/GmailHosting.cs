using Microsoft.Extensions.Options;
using Nullbox.Core.Gmail;
using Nullbox.Gmail;
using Nullbox.Gmail.Auth;

namespace Nullbox.Web.Hosting;

public static class GmailHosting
{
    /// <summary>
    /// Registers the token store over <c>{data}/google/</c>, the authenticated client factory, <see cref="IGmailService"/>
    /// and its read-only <see cref="IGmailReader"/>.
    /// Nothing touches Gmail until a job or the settings page asks, so the app starts fine without a token.
    /// </summary>
    public static IServiceCollection AddNullboxGmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GmailOptions>().Bind(configuration.GetSection(GmailOptions.SectionName));
        services.AddSingleton(_ => new GoogleCredentialFiles(NullboxHosting.ResolveDataDirectory(configuration)));
        services.AddSingleton(sp => new GmailCredentialStore(
            sp.GetRequiredService<GoogleCredentialFiles>(),
            sp.GetRequiredService<ILogger<GmailCredentialStore>>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<Google.Apis.Http.IHttpClientFactory>()));
        services.AddSingleton<IGmailCredentialStore>(sp => sp.GetRequiredService<GmailCredentialStore>());
        services.AddSingleton<IGmailClientFactory>(sp => new GmailClientFactory(
            sp.GetRequiredService<GmailCredentialStore>(),
            sp.GetService<Google.Apis.Http.IHttpClientFactory>()));
        services.AddSingleton<IGmailService>(sp => new GmailApiService(
            sp.GetRequiredService<IGmailClientFactory>().Create(GmailTokenKind.Primary),
            sp.GetRequiredService<IOptions<GmailOptions>>(),
            sp.GetRequiredService<ILogger<GmailApiService>>()));
        services.AddSingleton<IGmailReader>(sp => new GmailReader(sp.GetRequiredService<IGmailService>));
        return services;
    }
}
