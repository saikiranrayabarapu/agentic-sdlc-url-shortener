using System.Security.Cryptography;
using System.Text;

namespace UrlShortener.Api.Domain;

/// <summary>
/// Anonymous ownership without accounts. Creating a link issues a random manage token that is returned exactly once;
/// only its SHA-256 is stored. Deleting the link requires presenting that token, so a short code alone, which is
/// public by design, never grants control over the link.
/// </summary>
public static class ManageToken
{
    public const string HeaderName = "X-Manage-Token";

    /// <summary>192 bits of entropy, 32 characters in base64url: unguessable, URL- and header-safe.</summary>
    private const int TokenBytes = 24;

    public static string Generate() => Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>Constant-time comparison of the presented token's hash against the stored one, so timing never leaks how close a guess was.</summary>
    public static bool Matches(string? presented, string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(presented) || string.IsNullOrEmpty(storedHash)) return false;
        var a = Encoding.ASCII.GetBytes(Hash(presented.Trim()));
        var b = Encoding.ASCII.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
