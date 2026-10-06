using System.Security.Cryptography;
using System.Text;

namespace UrlShortener.Api.Domain;

/// <summary>
/// Generates random Base62 codes. 7 characters gives 62^7 ≈ 3.5e12 combinations; collisions are handled by the
/// caller retrying on a unique-index violation rather than by coordination, which keeps the generator stateless.
/// </summary>
public sealed class ShortCodeGenerator
{
    public const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    public const int DefaultLength = 7;

    public string Generate(int length = DefaultLength)
    {
        if (length is < 4 or > 32) throw new ArgumentOutOfRangeException(nameof(length), "Code length must be between 4 and 32.");
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }
        return sb.ToString();
    }
}
