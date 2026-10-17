using LocalRag.Api;
using Microsoft.AspNetCore.Authentication;

namespace LocalRag.Authentication;

public static class ManagerSecurity
{
    public static void AddManagerSecurity(this IServiceCollection services)
    {
        services.AddAuthentication(ManagerEndpoints.RequestScheme)
            .AddPolicyScheme(ManagerEndpoints.RequestScheme, null, options => options.ForwardDefaultSelector = context =>
                context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? LocalTokenAuthenticationHandler.SchemeName : ManagerEndpoints.CookieScheme)
            .AddCookie(ManagerEndpoints.CookieScheme, options =>
            {
                options.Cookie.Name = "LocalRag.Manager"; options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(30); options.SlidingExpiration = false;
                options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
                options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
            })
            .AddScheme<AuthenticationSchemeOptions, LocalTokenAuthenticationHandler>(LocalTokenAuthenticationHandler.SchemeName, _ => { })
            .AddScheme<AuthenticationSchemeOptions, ManagementTokenAuthenticationHandler>(ManagementTokenAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization(options =>
            options.AddPolicy(
                ManagementTokenAuthenticationHandler.PolicyName,
                policy => policy.AddAuthenticationSchemes(ManagementTokenAuthenticationHandler.SchemeName, ManagerEndpoints.CookieScheme)
                    .RequireAuthenticatedUser().RequireAssertion(context => context.User.HasClaim("LocalRagManagement", "true") || context.User.Identities.Any(identity => identity.AuthenticationType == ManagementTokenAuthenticationHandler.SchemeName))));

    }
    public static void UseManagerSecurity(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            context.Response.Headers.CacheControl = "no-store";
            if (context.User.Identity?.IsAuthenticated == true && !context.User.Identities.Any(identity => identity.IsAuthenticated && (identity.AuthenticationType == LocalTokenAuthenticationHandler.SchemeName || identity.AuthenticationType == ManagementTokenAuthenticationHandler.SchemeName)) && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                if (!ManagerEndpoints.SameOrigin(context.Request)) { context.Response.StatusCode = 403; return; }
                try { await context.RequestServices.GetRequiredService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>().ValidateRequestAsync(context); }
                catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException) { context.Response.StatusCode = 403; return; }
            }
            await next();
        });

    }
}
