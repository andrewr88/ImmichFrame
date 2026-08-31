using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.WebApi.Models;
using Microsoft.AspNetCore.Authentication;
using System.Reflection;
using ImmichFrame.Core.Logic;
using ImmichFrame.Core.Logic.AccountSelection;
using ImmichFrame.WebApi.Helpers;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Helpers.Profiles;

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
builder.Services.AddSingleton<IConfigCatalog>(srv => srv.GetRequiredService<ConfigLoader>().LoadCatalog(configPath));

builder.Services.AddHttpClient(); // Ensures IHttpClientFactory is available

// One set of services per configuration profile, built on first use and cached for the life of
// the process; the pools, HTTP clients and caches inside are far too expensive to rebuild.
builder.Services.AddSingleton<ProfileRegistry>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentProfile, CurrentProfile>();

ProfileServices CurrentProfileServices(IServiceProvider srv) =>
    srv.GetRequiredService<ProfileRegistry>().For(srv.GetRequiredService<ICurrentProfile>().Name);

// Settings and services are scoped and resolve through the registry, so a request naming a
// profile gets that profile's configuration while controllers go on asking for the same
// interfaces they always have. Sub-settings keep delegating down the chain rather than each
// reaching into the registry, so overriding one of them still overrides those below it.
builder.Services.AddScoped<IServerSettings>(srv => CurrentProfileServices(srv).Settings);
builder.Services.AddScoped<IGeneralSettings>(srv => srv.GetRequiredService<IServerSettings>().GeneralSettings);
builder.Services.AddScoped<IClientSettings>(srv => srv.GetRequiredService<IGeneralSettings>());
builder.Services.AddScoped<IServerBehaviorSettings>(srv => srv.GetRequiredService<IGeneralSettings>());

builder.Services.AddScoped<IWeatherService>(srv => CurrentProfileServices(srv).WeatherService);
builder.Services.AddScoped<ICalendarService>(srv => CurrentProfileServices(srv).CalendarService);
builder.Services.AddScoped<IImmichFrameLogic>(srv => CurrentProfileServices(srv).Logic);

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.SchemaFilter<ImmichFrame.WebApi.Helpers.NoReadOnlySchemaFilter>());

builder.Services.AddAuthorization(options => { options.AddPolicy("AllowAnonymous", policy => policy.RequireAssertion(context => true)); });

builder.Services.AddAuthentication("ImmichFrameScheme")
    .AddScheme<AuthenticationSchemeOptions, ImmichFrameAuthenticationHandler>("ImmichFrameScheme", options => { });

var app = builder.Build();

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
// Ahead of authentication: the auth handler resolves the profile's settings, so an unknown
// profile has to be turned away before it gets there.
app.UseMiddleware<UnknownProfileMiddleware>();
app.UseMiddleware<CustomAuthenticationMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapFallbackToFile("/index.html");

var immichStartupAllowed = await ImmichServerVersionChecker.CheckServerVersions(app.Services, app.Logger);
if (!immichStartupAllowed)
{
    app.Logger.LogCritical("ImmichFrame cannot start: Immich server requirements are not satisfied (see log above). Shutting down.");
    Environment.Exit(1);
}

app.Run();

// Make Program public for WebApplicationFactory
public partial class Program { }
