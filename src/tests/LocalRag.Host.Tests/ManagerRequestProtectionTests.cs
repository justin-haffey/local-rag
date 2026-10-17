using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using LocalRag.Api;
using LocalRag.Authentication;
using LocalRag.Configuration;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class ManagerRequestProtectionTests
{
    [Fact]
    public async Task ExpiredBrowserCookieReturnsAuthenticationFailureBeforeProtectedActions()
    {
        var clock = new ManualTimeProvider();
        await using var app = await CreateAppAsync(clock);
        using var client = Client(app);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/test/sign-in")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/test/normal")).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/test/normal")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/test/mutate", null)).StatusCode);
    }

    [Fact]
    public async Task RealCookieRequestsRequireCsrfAndSameOriginEvenWithInvalidBearerHeader()
    {
        await using var app = await CreateAppAsync();
        using var client = Client(app);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/test/sign-in")).StatusCode);
        var token = await client.GetStringAsync("/test/csrf");
        using var missingCsrf = new HttpRequestMessage(HttpMethod.Post, "/test/mutate");
        missingCsrf.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(missingCsrf)).StatusCode);
        using var invalidBearer = new HttpRequestMessage(HttpMethod.Post, "/test/mutate");
        invalidBearer.Headers.Add("Authorization", "Bearer invalid-test-token");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(invalidBearer)).StatusCode);
        using var wrongOrigin = new HttpRequestMessage(HttpMethod.Post, "/test/mutate");
        wrongOrigin.Headers.Add("X-CSRF-TOKEN", token); wrongOrigin.Headers.Add("Origin", "http://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(wrongOrigin)).StatusCode);
        using var valid = new HttpRequestMessage(HttpMethod.Post, "/test/mutate");
        valid.Headers.Add("X-CSRF-TOKEN", token); valid.Headers.Add("Origin", client.BaseAddress.GetLeftPart(UriPartial.Authority));
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(valid)).StatusCode);
    }

    [Fact]
    public async Task BearerClientsRemainSupportedAndOrdinaryCookieCannotAccessManagement()
    {
        await using var app = await CreateAppAsync();
        using var client = Client(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/test/mutate", null)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("bearer", "management-test-token");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/test/mutate", null)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("bearer", "normal-test-token");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/test/normal")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/test/sign-in-ordinary")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/test/management")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/test/normal")).StatusCode);
    }

    [Fact]
    public async Task CookieAttributesAndEncodedContentAndSecurityHeadersArePresent()
    {
        await using var app = await CreateAppAsync();
        using var client = Client(app);
        var response = await client.GetAsync("/test/sign-in");
        var cookie = string.Join(";", response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("normal-test-token", cookie, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    private static HttpClient Client(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new(new HttpClientHandler { CookieContainer = new(), AllowAutoRedirect = false }) { BaseAddress = new(address) };
    }

    private static async Task<WebApplication> CreateAppAsync(TimeProvider? clock = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.Configure<LocalRagOptions>(options => { });
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new LocalRagOptions
        {
            Authentication = new() { Token = "normal-test-token" },
            Management = new() { Enabled = true, Token = "management-test-token" }
        }));
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
        builder.Services.AddManagerSecurity();
        builder.Services.Configure<CookieAuthenticationOptions>(ManagerEndpoints.CookieScheme, options => options.TimeProvider = clock ?? TimeProvider.System);
        var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); app.UseManagerSecurity();
        foreach (var privileged in new[] { true, false })
        {
            app.MapGet(privileged ? "/test/sign-in" : "/test/sign-in-ordinary", async (HttpContext context) =>
            {
                var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "test-browser") };
                if (privileged) claims.Add(new("LocalRagManagement", "true"));
                await context.SignInAsync(ManagerEndpoints.CookieScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, ManagerEndpoints.CookieScheme)),
                    new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30) });
                return Results.NoContent();
            });
        }
        app.MapGet("/test/csrf", (HttpContext context, IAntiforgery antiforgery) => antiforgery.GetAndStoreTokens(context).RequestToken!).RequireAuthorization();
        app.MapPost("/test/mutate", () => Results.NoContent()).RequireAuthorization(ManagementTokenAuthenticationHandler.PolicyName);
        app.MapGet("/test/management", () => Results.Ok()).RequireAuthorization(ManagementTokenAuthenticationHandler.PolicyName);
        app.MapGet("/test/normal", () => Results.Ok()).RequireAuthorization();
        await app.StartAsync();
        return app;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
