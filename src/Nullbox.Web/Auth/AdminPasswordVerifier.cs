using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Nullbox.Core.Options;

namespace Nullbox.Web.Auth;

/// <summary>Checks a submitted password against <see cref="NullboxOptions.AdminPassword"/> in constant time.</summary>
public sealed class AdminPasswordVerifier(IOptions<NullboxOptions> options)
{
    /// <summary>False when no admin password is configured: an unset password never lets anyone in.</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(options.Value.AdminPassword);

    public bool Verify(string? candidate)
    {
        var expected = options.Value.AdminPassword;
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        // Hash both sides so the comparison is fixed-length and leaks neither length nor content.
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        return CryptographicOperations.FixedTimeEquals(expectedHash, candidateHash);
    }
}
