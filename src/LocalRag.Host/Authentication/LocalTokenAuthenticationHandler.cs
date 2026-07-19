using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using LocalRag.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LocalRag.Authentication;

public sealed class LocalTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<LocalRagOptions> localRagOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    public const string SchemeName = "LocalToken";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = localRagOptions.Value.Authentication.Token;
        var supplied = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(expected) || !supplied.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.Fail("A local bearer token is required."));
        }

        var token = supplied["Bearer ".Length..];
        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(token)))
        {
            return Task.FromResult(AuthenticateResult.Fail("The local bearer token is invalid."));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "local-extension")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
