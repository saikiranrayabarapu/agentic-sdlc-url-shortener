using System.Net;
using System.Text.RegularExpressions;

namespace UrlShortener.Api.Domain;

public sealed class UrlValidationOptions
{
    public int MaxLength { get; set; } = 2048;
    /// <summary>Reject loopback/private hosts so the service cannot be used to probe internal networks.</summary>
    public bool BlockPrivateHosts { get; set; } = true;
}

public static class UrlValidator
{
    private static readonly Regex AliasRx = new("^[A-Za-z0-9_-]{4,32}$", RegexOptions.Compiled);
    private static readonly HashSet<string> ReservedAliases = new(StringComparer.OrdinalIgnoreCase) { "api", "health", "openapi", "swagger", "admin" };

    public static bool TryValidateUrl(string? input, UrlValidationOptions options, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;
        if (string.IsNullOrWhiteSpace(input)) { error = "url is required."; return false; }
        if (input.Length > options.MaxLength) { error = $"url exceeds {options.MaxLength} characters."; return false; }
        if (!Uri.TryCreate(input, UriKind.Absolute, out var parsed)) { error = "url must be an absolute URL."; return false; }
        if (parsed.Scheme is not ("http" or "https")) { error = "url scheme must be http or https."; return false; }
        if (string.IsNullOrEmpty(parsed.Host)) { error = "url must have a host."; return false; }
        if (!string.IsNullOrEmpty(parsed.UserInfo)) { error = "url must not embed credentials."; return false; }
        if (options.BlockPrivateHosts && IsPrivateHost(parsed)) { error = "url host is not allowed."; return false; }
        uri = parsed;
        return true;
    }

    public static bool TryValidateAlias(string? alias, out string? error)
    {
        error = null;
        if (alias is null) return true;
        if (!AliasRx.IsMatch(alias)) { error = "customAlias must be 4-32 characters of letters, digits, '-' or '_'."; return false; }
        if (ReservedAliases.Contains(alias)) { error = "customAlias is reserved."; return false; }
        return true;
    }

    private static bool IsPrivateHost(Uri uri)
    {
        if (uri.IsLoopback) return true;
        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return false;
        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254)
                || bytes[0] == 0;
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
    }
}
