using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace UrlShortener.Api.Tests;

/// <summary>
/// TestServer has no real sockets, so every request appears to come from the same (null) address. This filter lets a
/// test choose the client address per request, which is what per-client rate limiting partitions on. It runs ahead of
/// the application's own pipeline and is registered only by the test factory.
/// </summary>
public sealed class TestClientIpStartupFilter : IStartupFilter
{
    public const string HeaderName = "X-Test-Client-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, pipeline) =>
        {
            if (context.Request.Headers.TryGetValue(HeaderName, out var raw) && IPAddress.TryParse(raw.ToString(), out var ip))
                context.Connection.RemoteIpAddress = ip;
            await pipeline(context);
        });
        next(app);
    };
}
