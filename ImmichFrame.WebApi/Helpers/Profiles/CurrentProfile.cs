namespace ImmichFrame.WebApi.Helpers.Profiles;

/// <summary>
/// The configuration profile the request in flight is asking for, or null for the default.
/// </summary>
public interface ICurrentProfile
{
    string? Name { get; }
}

/// <inheritdoc cref="ICurrentProfile"/>
/// <remarks>
/// Read from the query string rather than a route or a header, because the asset endpoints are
/// fetched as bare URLs - an &lt;img src&gt; or &lt;video src&gt; cannot carry a header - and
/// because it matches how clientIdentifier is already threaded through every endpoint.
/// </remarks>
public sealed class CurrentProfile(IHttpContextAccessor _httpContextAccessor) : ICurrentProfile
{
    public const string QueryParameterName = "profile";

    public string? Name
    {
        get
        {
            // No HttpContext outside a request - startup work, background tasks - so fall back to
            // the default configuration.
            var request = _httpContextAccessor.HttpContext?.Request;

            return request?.Query[QueryParameterName].FirstOrDefault();
        }
    }
}
