using UrlShortener.Api.Domain;

namespace UrlShortener.Api.Tests;

public sealed class ShortCodeGeneratorTests
{
    private readonly ShortCodeGenerator _generator = new();

    [Fact]
    public void Generates_seven_base62_characters_by_default()
    {
        var code = _generator.Generate();
        Assert.Equal(7, code.Length);
        Assert.All(code, c => Assert.Contains(c, ShortCodeGenerator.Alphabet));
    }

    [Fact]
    public void Generates_unique_codes()
    {
        var codes = Enumerable.Range(0, 2000).Select(_ => _generator.Generate()).ToHashSet();
        Assert.Equal(2000, codes.Count);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(33)]
    public void Rejects_out_of_range_lengths(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _generator.Generate(length));
    }
}

public sealed class UrlValidatorTests
{
    private readonly UrlValidationOptions _options = new();

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/path?q=1#frag")]
    [InlineData("https://sub.example.org:8443/x")]
    public void Accepts_public_http_urls(string url)
    {
        Assert.True(UrlValidator.TryValidateUrl(url, _options, out var uri, out var error), error);
        Assert.NotNull(uri);
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("example.com")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://192.168.1.10/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://user:secret@example.com/")]
    public void Rejects_unsafe_or_malformed_urls(string url)
    {
        Assert.False(UrlValidator.TryValidateUrl(url, _options, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Rejects_urls_longer_than_the_limit()
    {
        var url = "https://example.com/" + new string('a', 2048);
        Assert.False(UrlValidator.TryValidateUrl(url, _options, out _, out _));
    }

    [Fact]
    public void Private_host_check_can_be_disabled()
    {
        var relaxed = new UrlValidationOptions { BlockPrivateHosts = false };
        Assert.True(UrlValidator.TryValidateUrl("http://localhost:5000/", relaxed, out _, out _));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("good_alias-1", true)]
    [InlineData("abc", false)]
    [InlineData("has space", false)]
    [InlineData("health", false)]
    public void Alias_rules(string? alias, bool expected)
    {
        Assert.Equal(expected, UrlValidator.TryValidateAlias(alias, out _));
    }
}
