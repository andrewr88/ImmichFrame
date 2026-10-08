using ImmichFrame.Core.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;

namespace ImmichFrame.WebApi.Helpers;

public class ImmichFrameAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IServerSettings _settings;

    public ImmichFrameAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IServerSettings settings)
        : base(options, logger, encoder)
    {
        _settings = settings;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Read per request rather than in the constructor: the settings belong to the request's
        // configuration profile, so capturing a value at construction time bakes in whichever
        // profile happened to build the handler.
        var authenticationSecret = _settings.GeneralSettings.AuthenticationSecret;

        var endpoint = Context.GetEndpoint();
        var authorizeAttribute = endpoint?.Metadata?.GetMetadata<IAuthorizeData>();
        var allowAnonymous = endpoint?.Metadata?.GetMetadata<IAllowAnonymous>();

        // Whitespace is "no secret configured", not a secret. Anything narrower makes the empty
        // string a working credential and locks out every real client: the header parse below
        // trims "Bearer " down to "", which then compares equal to the configured secret, while a
        // frame sending its actual token is refused. A settings file can reach that state by hand,
        // so the check belongs here rather than only in whatever wrote the file.
        if (string.IsNullOrWhiteSpace(authenticationSecret) || authorizeAttribute == null || allowAnonymous != null)
        {
            // No auth is required
            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "anonymous") };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader))
        {
            return Task.FromResult(AuthenticateResult.Fail("Missing Authorization Header"));
        }

        if (authHeader.StartsWith("Bearer ", System.StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader.Substring("Bearer ".Length).Trim();

            // Constant time, so how long a wrong token takes to refuse says nothing about how much
            // of it was right. Only the length can still show - FixedTimeEquals refuses a length
            // mismatch at once - and that is accepted: it is the content that a timing attack would
            // otherwise recover one byte at a time.
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(authenticationSecret)))
            {
                var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "authenticatedUser") };
                var identity = new ClaimsIdentity(claims, Scheme.Name);
                var principal = new ClaimsPrincipal(identity);
                var ticket = new AuthenticationTicket(principal, Scheme.Name);

                return Task.FromResult(AuthenticateResult.Success(ticket));
            }

            return Task.FromResult(AuthenticateResult.Fail("The AuthenticationSecret was not correct!"));
        }

        return Task.FromResult(AuthenticateResult.Fail("Invalid Authorization Header"));
    }
}