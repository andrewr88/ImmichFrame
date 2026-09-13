using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using Microsoft.AspNetCore.Authentication;
using System.Reflection;
using ImmichFrame.Core.Logic;
using ImmichFrame.Core.Logic.AccountSelection;
using ImmichFrame.WebApi.Database;
using ImmichFrame.WebApi.Helpers;
using ImmichFrame.WebApi.Helpers.Admin;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Helpers.Profiles;
using ImmichFrame.WebApi.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
//log the version number
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
Console.WriteLine($@"
 _                     _      _    ______                        
(_)                   (_)    | |   |  ___|                       
 _ _ __ ___  _ __ ___  _  ___| |__ | |_ _ __ __ _ _ __ ___   ___ 
| | '_ ` _ \| '_ ` _ \| |/ __| '_ \|  _| '__/ _` | '_ ` _ \ / _ \
| | | | | | | | | | | | | (__| | | | | | | | (_| | | | | | |  __/
|_|_| |_| |_|_| |_| |_|_|\___|_| |_\_| |_|  \__,_|_| |_| |_|\___| Version {version}");
Console.WriteLine();

// Add services to the container.
builder.Services.AddLogging(builder =>
{
    LogLevel level = LogLevel.Information;
    var logLevel = Environment.GetEnvironmentVariable("LOG_LEVEL");
    if (!string.IsNullOrWhiteSpace(logLevel))
    {
        Enum.TryParse(logLevel, true, out level);
    }

    Console.WriteLine($"LogLevel: {level}");
    builder.SetMinimumLevel(level);
    builder.AddSimpleConsole(options =>
    {
        // Customizing the log output format
        options.TimestampFormat = "yy-MM-dd HH:mm:ss "; // Custom timestamp format
        options.SingleLine = true;
    });

    // Disable SpaProxy info logs
    builder.AddFilter("Microsoft.AspNetCore.SpaProxy", LogLevel.Warning);
    // Disable AspNetCore info logs
    builder.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
    // Only show HttpClient request info logs when LOG_LEVEL is Debug or lower
    if (level > LogLevel.Debug)
    {
        builder.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
    }
});


// Setup Config
var configPath = Environment.GetEnvironmentVariable("IMMICHFRAME_CONFIG_PATH") ??
        Directory.EnumerateDirectories(AppDomain.CurrentDomain.BaseDirectory, "*", SearchOption.TopDirectoryOnly)
        .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "Config", StringComparison.OrdinalIgnoreCase))
        ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
builder.Services.AddTransient<ConfigLoader>();

// The one place the configuration directory is named. Everything that reads or writes the settings
// resolves it from here, so a test - or anything else - that moves it moves all of them.
builder.Services.AddSingleton(new ConfigLocation(configPath));

// Settings live in a SQLite database in the configuration directory. An existing Settings.json or
// Settings.yml is imported into it once, on first run; after that the database is the source of
// truth and the file is ignored. ConfigLoader stays the reader for that one import.
builder.Services.AddDbContextFactory<SettingsDbContext>((srv, options) =>
    options.UseSqlite($"Data Source={Path.Combine(srv.GetRequiredService<ConfigLocation>().Directory, "immichframe.db")}"));
builder.Services.AddSingleton<SettingsService>();

// Everything injects IConfigCatalog and gets the forwarding catalog, so the configuration behind it
// can be replaced at runtime. The seed still runs on first use rather than here, so a test that
// registers its own catalog never touches the database.
builder.Services.AddSingleton(srv => new SwappableConfigCatalog(
    () => srv.GetRequiredService<SettingsService>().LoadCatalog(),
    srv.GetRequiredService<ProfileRegistry>));
builder.Services.AddSingleton<IConfigCatalog>(srv => srv.GetRequiredService<SwappableConfigCatalog>());

// Singleton because it serialises saves on one lock: the version token it hands out is only
// meaningful if no second save can slip between a read and the write it authorises.
builder.Services.AddSingleton<AdminConfigService>();

builder.Services.AddHttpClient(); // Ensures IHttpClientFactory is available

// The configuration editor's Immich picker fetches from whatever server an administrator names -
// including one still being typed into the form - so unlike the frame's own client this one is
// bounded on every axis. An authenticated administrator choosing the target is the point, not the
// risk; a typo turning ImmichFrame into an unbounded fetcher of somebody else's network is.
builder.Services.AddHttpClient(AdminImmichAccounts.HttpClientName,
        client => client.Timeout = AdminImmichAccounts.RequestTimeout)
    // Redirects are not followed. Following one lets the named host hand the request - and the
    // X-API-KEY header on it - to a host the administrator never named.
    .ConfigurePrimaryHttpMessageHandler(AdminImmichAccounts.CreatePrimaryHandler)
    .AddHttpMessageHandler(() => new ResponseSizeLimitHandler(AdminImmichAccounts.MaxResponseBytes));

// Singleton, like the AdminConfigService it resolves saved accounts through: it holds no per-request
// state, and its HttpClients come from IHttpClientFactory.
builder.Services.AddSingleton<AdminImmichAccounts>();

// One set of services per configuration profile, built on first use and cached until the
// configuration is swapped; the pools, HTTP clients and caches inside are far too expensive to
// rebuild per request.
builder.Services.AddSingleton<ProfileRegistry>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentProfile, CurrentProfile>();

// The profile's services are resolved once per request, through a holder rather than directly.
// ProfileServices is IDisposable and belongs to the registry, not to the request: the container
// disposes whatever a scoped factory returns, so registering it here would tear a profile's pools
// down at the end of one request while every other request on that profile still used them.
// ProfileScope is not disposable, so there is nothing for the container to take ownership of here.
// That covers the holder only: every member handed out of it has to clear the same bar on its own
// account, which is why IImmichFrameLogic below goes out through a non-owning forwarder.
builder.Services.AddScoped<ProfileScope>();

// Settings and services are scoped and resolve through the registry, so a request naming a
// profile gets that profile's configuration while controllers go on asking for the same
// interfaces they always have. Sub-settings keep delegating down the chain rather than each
// reaching into the registry, so overriding one of them still overrides those below it.
builder.Services.AddScoped<IServerSettings>(srv => srv.GetRequiredService<ProfileScope>().Services.Settings);
builder.Services.AddScoped<IGeneralSettings>(srv => srv.GetRequiredService<IServerSettings>().GeneralSettings);
builder.Services.AddScoped<IClientSettings>(srv => srv.GetRequiredService<IGeneralSettings>());
builder.Services.AddScoped<IServerBehaviorSettings>(srv => srv.GetRequiredService<IGeneralSettings>());

// Neither of these two is IDisposable, so the container has nothing to capture and the shared
// instance can be handed out as it is. Check that still holds before adding another.
builder.Services.AddScoped<IWeatherService>(srv => srv.GetRequiredService<ProfileScope>().Services.WeatherService);
builder.Services.AddScoped<ICalendarService>(srv => srv.GetRequiredService<ProfileScope>().Services.CalendarService);

// The logic is the exception: it is IDisposable, and the container disposes whatever a scoped
// factory returns even when it built none of it, so handing out the profile's own instance ended
// that profile's pools and caches with the first request that touched them. The forwarder is not
// disposable, so there is nothing for the request's scope to take ownership of.
builder.Services.AddScoped<IImmichFrameLogic>(srv =>
    new NonOwningImmichFrameLogic(srv.GetRequiredService<ProfileScope>().Services.Logic));

builder.Services.AddControllers()
      .AddJsonOptions(options =>
          options.JsonSerializerOptions.Converters.Add(
              new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.SchemaFilter<ImmichFrame.WebApi.Helpers.NoReadOnlySchemaFilter>());

// The admin surface reads its configuration from the environment only, never from
// Settings.json/Settings.yml: the editor these values guard is what rewrites that file, so a
// mistake there must not be able to unlock - or lock everyone out of - the editor itself.
var adminOidcOptions = AdminOidcOptions.FromEnvironment();
builder.Services.AddSingleton(adminOidcOptions);
builder.Services.AddSingleton<IAuthorizationHandler, AdminAllowlistHandler>();

if (adminOidcOptions.TrustProxyHeaders)
{
    // ASP.NET ignores X-Forwarded-* unless the hop they arrived from is listed in KnownProxies or
    // KnownNetworks, and the defaults trust only loopback - which is not where a container sees its
    // reverse proxy - so both lists are cleared. That is exactly why this is opt-in and off by
    // default: with it enabled and no proxy in front, any client can forge X-Forwarded-Proto and
    // X-Forwarded-Host and so choose the scheme and host the OpenID Connect redirect_uri is built
    // from. Turn it on only when something in front is overwriting those headers.
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AllowAnonymous", policy => policy.RequireAssertion(context => true));

    // The scheme is named rather than inherited: the default is still ImmichFrameScheme, whose
    // handler succeeds anonymously whenever no secret is configured, so a policy that inherited the
    // default would treat every caller on an unsecured frame as an authenticated administrator.
    options.AddPolicy(AdminAuthentication.AdminOnlyPolicy, policy =>
    {
        policy.AddAuthenticationSchemes(AdminAuthentication.CookieScheme);
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new AdminAllowlistRequirement());
    });
});

var authenticationBuilder = builder.Services.AddAuthentication("ImmichFrameScheme")
    .AddScheme<AuthenticationSchemeOptions, ImmichFrameAuthenticationHandler>("ImmichFrameScheme", options => { });

// Registered whether or not OpenID Connect is configured: the cookie handler has no external
// dependency, and a scheme that always exists is what lets /api/admin/session report an
// unconfigured surface instead of throwing on a scheme nobody registered.
authenticationBuilder.AddCookie(AdminAuthentication.CookieScheme, options =>
{
    options.Cookie.Name = "immichframe.admin";
    options.Cookie.HttpOnly = true;
    // Lax rather than Strict. This cookie is issued at the end of a redirect chain the identity
    // provider started, and a Strict cookie is withheld on a top-level navigation initiated from
    // another site - so the browser would arrive back at /admin without the session it had just
    // been given and look signed out. Lax is sent on exactly that top-level GET while still not
    // travelling with a cross-site POST, which is what makes the sign-out endpoint not worth a CSRF
    // token. (The handshake's own correlation cookie is a separate cookie with its own SameSite
    // setting, OpenIdConnectOptions.CorrelationCookie; this setting does not affect it.)
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;

    // Everything behind this scheme is an /api/admin endpoint the SPA calls with fetch, so answer
    // with a status code instead of the cookie handler's default redirect to a login page that does
    // not exist in this application.
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});

// Only when the surface is fully configured, allowlist included: a handshake that can only ever
// mint a cookie no policy will accept is not worth being reachable.
if (adminOidcOptions.IsEnabled)
{
    authenticationBuilder.AddOpenIdConnect(AdminAuthentication.OidcScheme, options =>
    {
        options.Authority = adminOidcOptions.Authority;
        options.ClientId = adminOidcOptions.ClientId;
        options.ClientSecret = adminOidcOptions.ClientSecret;
        options.SignInScheme = AdminAuthentication.CookieScheme;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        // Nothing here calls the identity provider on the user's behalf, so the tokens are not kept
        // in the cookie; the claims that decide access are copied out of the id token instead.
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.CallbackPath = AdminAuthentication.CallbackPath;
        options.SignedOutCallbackPath = AdminAuthentication.SignedOutCallbackPath;
        options.RemoteSignOutPath = AdminAuthentication.RemoteSignOutPath;
        // openid and profile are already there by default; the allowlist can also name an address.
        options.Scope.Add("email");
    });
}

var app = builder.Build();

// First in the pipeline, before anything reads Request.Scheme or Request.Host - which is what the
// OpenID Connect handler builds redirect_uri from. Behind a TLS-terminating proxy without this the
// redirect_uri is built as http:// and the identity provider rejects it.
if (adminOidcOptions.TrustProxyHeaders)
{
    app.UseForwardedHeaders();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseStaticFiles();
if (app.Environment.IsProduction())
{
    app.UseDefaultFiles();
}

if (app.Environment.IsDevelopment())
{
    var root = Directory.GetCurrentDirectory();
    var dotenv = Path.Combine(root, "..", "docker", ".env");

    dotenv = Path.GetFullPath(dotenv);
    DotEnv.Load(dotenv);
}

// app.UseHttpsRedirection();
// Outside everything that resolves a profile, including UnknownProfileMiddleware's own catalog
// lookup: a configuration saved through the admin editor can drop a profile out from under a
// request that was already admitted, and that is a 404, not a 500.
app.UseMiddleware<ProfileNotFoundMiddleware>();

// Ahead of authentication: the auth handler resolves the profile's settings, so an unknown
// profile has to be turned away before it gets there.
app.UseMiddleware<UnknownProfileMiddleware>();
app.UseMiddleware<CustomAuthenticationMiddleware>();

// Ahead of authentication and authorization: an admin endpoint on an installation with no admin
// surface must look absent, and [Authorize] running first would answer 401 instead - telling an
// anonymous caller that an admin API is here.
app.UseMiddleware<AdminSurfaceMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapFallbackToFile("/index.html");

// After every endpoint is mapped, and before the first request: the admin prefix is exempt from the
// frame's shared-secret scheme, so an endpoint there without authorization would answer to anyone.
// Failing to boot, naming the route, beats discovering it in production.
AdminEndpointGuard.Validate(((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints));

// Migrates the settings database and imports an existing Settings.json/yml on first run, so the
// catalog below is seeded from the database rather than from the file. Skipped when a test has
// registered its own IConfigCatalog: there is then nothing to import and no SQLite file to create.
if (app.Services.GetService<SwappableConfigCatalog>() is not null)
{
    await app.Services.GetRequiredService<SettingsService>().InitializeAsync();
}

// Deliberately not awaited: an unreachable Immich server must not delay startup, otherwise
// the admin UI needed to fix that very server stays unreachable too.
_ = Task.Run(async () =>
{
    try
    {
        var immichServersOk = await ImmichServerVersionChecker.CheckServerVersions(app.Services, app.Logger);
        if (!immichServersOk)
        {
            app.Logger.LogCritical("One or more Immich servers are unreachable or unsupported (see log above). The slideshow may not work — fix the account settings via the admin UI at /admin.");
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogCritical("Immich server version check failed: {Message}", ex.Message);
    }
});

app.Run();

// Make Program public for WebApplicationFactory
public partial class Program { }
