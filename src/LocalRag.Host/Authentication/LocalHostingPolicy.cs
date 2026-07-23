using System.Net;

namespace LocalRag.Authentication;

public static class LocalHostingPolicy
{
    public static void ValidateConfiguredBindings(IConfiguration configuration)
    {
        var configuredUrls = new List<string>();
        var urls = configuration["urls"] ?? configuration["ASPNETCORE_URLS"];
        if (!string.IsNullOrWhiteSpace(urls))
        {
            configuredUrls.AddRange(urls.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        configuredUrls.AddRange(configuration.GetSection("Kestrel:Endpoints")
            .GetChildren()
            .Select(endpoint => endpoint["Url"])
            .Where(url => !string.IsNullOrWhiteSpace(url))!);

        foreach (var rawUrl in configuredUrls)
        {
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var url) || !IsLoopbackHost(url.Host))
            {
                throw new InvalidOperationException("Local RAG only permits loopback server bindings.");
            }
        }
    }

    public static bool IsLoopbackRequest(HttpContext context) =>
        IsLoopbackHost(context.Request.Host.Host) &&
        context.Connection.RemoteIpAddress is { } remoteAddress &&
        IPAddress.IsLoopback(remoteAddress);

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
}
