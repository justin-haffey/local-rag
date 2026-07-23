using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using LocalRag.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LocalRag.Authentication;

public sealed class ManagementTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<LocalRagOptions> localRagOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    public const string SchemeName = "ManagementToken";
    public const string PolicyName = "LocalRagManagement";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var management = localRagOptions.Value.Management;
        var supplied = Request.Headers.Authorization.ToString();
        if (!management.Enabled || string.IsNullOrWhiteSpace(management.Token) ||
            !supplied.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.Fail("Management access is unavailable."));
        }

        var expectedBytes = Encoding.UTF8.GetBytes(management.Token);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied["Bearer ".Length..]);
        if (expectedBytes.Length != suppliedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
        {
            return Task.FromResult(AuthenticateResult.Fail("The management bearer token is invalid."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "local-management")],
            SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
