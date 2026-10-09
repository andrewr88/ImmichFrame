using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Logic;
using ImmichFrame.WebApi.Helpers;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests;

/// <summary>
/// Where a redirect may take the API key the frame's own Immich client sends with every request. The
/// key travels in <c>X-API-KEY</c>, a header the framework's own redirect handling knows nothing
/// about - it strips <c>Authorization</c> and leaves everything else on the request - so it is the
/// client's registration in <c>Program.cs</c> that decides whether a server can hand the key to
/// another host with one 302.
/// <para>
/// The first two tests run over real loopback sockets, on a client the application's own
/// registrations build. Every other host in the suite replaces the primary handler with a mock
/// (<c>ImmichApiMock.UseMockHandler</c>), and a mock follows no redirect at all - so nothing there can
/// show the real one following one. The rest pin <see cref="SameServerRedirectHandler"/>'s rules on a
/// scripted server: which redirects count as staying on the server, and what is sent when one does.
/// </para>
/// </summary>
[TestFixture]
public class ImmichHttpClientRedirectTests
{
    private const string ApiKey = "frame-api-key-3c71a9";
    private const string Immich = "http://immich.example.com";

    /// <summary>
    /// The redirect is not followed, and the host it named is never contacted - let alone sent the key.
    /// <para>
    /// The two servers are two names for the loopback interface, which is all a test can listen on.
    /// That is enough: which server a redirect leads to is a matter of the host it names, not of the
    /// address that host resolves to. The redirect names the IPv4 literal, so that a client which did
    /// follow it reaches the other server whatever <c>localhost</c> resolves to first.
    /// </para>
    /// </summary>
    [Test]
    public async Task ImmichAccountHttpClient_ARedirectToAnotherHost_IsNotFollowed()
    {
        // Arrange
        await using var elsewhere = LoopbackServer.Start(_ => Ok("{}"));
        await using var immich = LoopbackServer.Start(request =>
            Redirect(HttpStatusCode.Found, $"http://127.0.0.1:{elsewhere.Port}{request.Path}"));

        using var factory = CreateFactory();
        using var client = FrameClient(factory);

        // Act
        using var response = await client.GetAsync($"http://localhost:{immich.Port}/api/server/version");

        // Assert
        Assert.Multiple(() =>
        {
            // The key really was on the request, so its absence from the other host is the client
            // keeping it back rather than never having had it.
            Assert.That(immich.Received, Has.One.Matches<ReceivedRequest>(request => request.HasHeader("X-API-KEY", ApiKey)));

            // Handed back as it is: the generated client fails on a status it does not expect.
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found));

            Assert.That(elsewhere.Received, Is.Empty, "the redirect to another host was followed");
            Assert.That(elsewhere.Received, Has.None.Matches<ReceivedRequest>(request => request.HasHeader("X-API-KEY")),
                "the other host was sent the API key");
        });
    }

    /// <summary>
    /// What refusing every redirect would have broken: an Immich server moved to another path on the
    /// same server - behind a reverse proxy, most often - still answers, and still gets the key.
    /// </summary>
    [Test]
    public async Task ImmichAccountHttpClient_ARedirectOnTheSameServer_IsFollowedWithTheKey()
    {
        // Arrange
        await using var immich = LoopbackServer.Start(request => request.Path == "/api/server/version"
            ? Redirect(HttpStatusCode.MovedPermanently, "/immich/api/server/version")
            : Ok("""{"major":2,"minor":0,"patch":0}"""));

        using var factory = CreateFactory();
        using var client = FrameClient(factory);

        // Act
        using var response = await client.GetAsync($"http://127.0.0.1:{immich.Port}/api/server/version");

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(immich.Received.Select(request => request.Path),
                Is.EqualTo(new[] { "/api/server/version", "/immich/api/server/version" }));
            Assert.That(immich.Received.Last().HasHeader("X-API-KEY", ApiKey), Is.True,
                "the redirected request went out without the key");
        });
    }

    // On the same server: a path that moved, a host spelled in another case, TLS taken up on each
    // scheme's default port or on a port named for both.
    [TestCase($"{Immich}/api/server/version", "/immich/api/server/version", true)]
    [TestCase($"{Immich}/api/server/version", "http://IMMICH.example.com/api/server/version", true)]
    [TestCase($"{Immich}/api/server/version", "https://immich.example.com/api/server/version", true)]
    [TestCase($"{Immich}:2283/api/server/version", "https://immich.example.com:2283/api/server/version", true)]
    // Not: another host, another port - including a named one given up for a default - TLS given up,
    // and a scheme that is not HTTP at all.
    [TestCase($"{Immich}/api/server/version", "http://elsewhere.example.com/api/server/version", false)]
    [TestCase($"{Immich}/api/server/version", "http://immich.example.com:8080/api/server/version", false)]
    [TestCase($"{Immich}:2283/api/server/version", "https://immich.example.com/api/server/version", false)]
    [TestCase("https://immich.example.com/api/server/version", "http://immich.example.com/api/server/version", false)]
    [TestCase($"{Immich}/api/server/version", "ftp://immich.example.com/api/server/version", false)]
    public async Task Redirect_IsFollowedOnlyWhileItStaysOnTheSameServer(string requested, string location, bool followed)
    {
        // Arrange - the first request redirected, and whatever follows it answered
        ScriptedHandler server = null!;
        server = new ScriptedHandler(_ => server.Received.Count == 1
            ? Redirected(HttpStatusCode.Found, location)
            : new HttpResponseMessage(HttpStatusCode.OK));

        using var client = Through(server);

        // Act
        using var response = await client.GetAsync(requested);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(server.Received, Has.Count.EqualTo(followed ? 2 : 1));
            Assert.That(response.StatusCode, Is.EqualTo(followed ? HttpStatusCode.OK : HttpStatusCode.Found),
                "a redirect not followed is handed back as it is");
        });
    }

    /// <summary>
    /// What is sent on, once a redirect is followed: 303 asks for a GET whatever was sent, a POST meets
    /// 301 and 302 with one too, and 307 and 308 repeat the request exactly - method and body - which
    /// is the whole difference between them and the two before.
    /// </summary>
    [TestCase(HttpStatusCode.MovedPermanently, "GET")]
    [TestCase(HttpStatusCode.Found, "GET")]
    [TestCase(HttpStatusCode.SeeOther, "GET")]
    [TestCase(HttpStatusCode.TemporaryRedirect, "POST")]
    [TestCase(HttpStatusCode.PermanentRedirect, "POST")]
    public async Task Redirect_OfAPost_IsFollowedWithTheMethodItsStatusCallsFor(HttpStatusCode status, string method)
    {
        // Arrange
        const string body = """{"size":1}""";
        var server = new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/api/search/random"
            ? Redirected(status, "/immich/api/search/random")
            : new HttpResponseMessage(HttpStatusCode.OK));

        using var client = Through(server);

        // Act
        using var response = await client.PostAsync($"{Immich}/api/search/random", new StringContent(body));

        // Assert
        var redirected = server.Received.Last();
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(redirected.Method.Method, Is.EqualTo(method));
            Assert.That(redirected.Body, method == "POST" ? Is.EqualTo(body) : Is.Null);
        });
    }

    /// <summary>A server redirecting without end is given up on, and its last redirect handed back.</summary>
    [Test]
    public async Task Redirect_GoingRoundInCircles_IsHandedBackAfterTheLimit()
    {
        // Arrange
        var server = new ScriptedHandler(_ => Redirected(HttpStatusCode.Found, "/api/server/version"));
        using var client = Through(server);

        // Act
        using var response = await client.GetAsync($"{Immich}/api/server/version");

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(server.Received, Has.Count.EqualTo(SameServerRedirectHandler.MaxRedirects + 1));
        });
    }

    /// <summary>
    /// The operator is told which redirect was not followed - from which server, to which - and that is
    /// all: a redirect's target is chosen by whoever sent it, and its user information, path and query
    /// can each carry a secret that has no business in a log.
    /// </summary>
    [Test]
    public async Task Redirect_NotFollowed_IsLoggedNamingTheServersAndNothingMore()
    {
        // Arrange
        var server = new ScriptedHandler(_ => Redirected(HttpStatusCode.Found,
            "https://someone:their-password@elsewhere.example.com:8443/private-path?token=redirect-token"));
        var log = new WarningLog();

        using var client = Through(server, log);

        // Act
        using var response = await client.GetAsync($"{Immich}/api/server/version");

        // Assert
        Assert.That(log.Lines, Has.Exactly(1).Items);
        Assert.Multiple(() =>
        {
            Assert.That(log.Lines[0], Does.Contain(Immich).And.Contain("https://elsewhere.example.com:8443"));
            Assert.That(log.Lines[0], Does.Not.Contain("their-password")
                .And.Not.Contain("private-path")
                .And.Not.Contain("redirect-token"));
        });
    }

    /// <summary>
    /// The application's host, with no account configured so that the startup version check has
    /// nothing to ask - and no mock handler, because the primary handler under test is the real one.
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IConfigCatalog>(new ConfigCatalog(new ServerSettings
                    {
                        GeneralSettingsImpl = new GeneralSettings(),
                        AccountsImpl = new List<ServerAccountSettings>()
                    }));
                });
            });

    /// <summary>
    /// The frame's client, carrying the key the way <c>PooledImmichFrameLogic</c> and the startup
    /// version check have it carry one. A short timeout turns a hang into a failure.
    /// </summary>
    private static HttpClient FrameClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(PooledImmichFrameLogic.HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(10);
        client.UseApiKey(ApiKey);

        return client;
    }

    /// <summary><paramref name="server"/> behind the redirect handler, the way the frame's client chains them.</summary>
    private static HttpClient Through(HttpMessageHandler server, WarningLog? log = null) =>
        new(new SameServerRedirectHandler(log ?? new WarningLog()) { InnerHandler = server });

    private static HttpResponseMessage Redirected(HttpStatusCode status, string location) =>
        new(status) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };

    private static string Redirect(HttpStatusCode status, string location) =>
        $"HTTP/1.1 {(int)status} {status}\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    private static string Ok(string json) =>
        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(json)}\r\nConnection: close\r\n\r\n{json}";

    /// <summary>An Immich server answering through a function, and remembering what it was sent.</summary>
    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> _answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Body)> Received { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Received.Add((request.Method, request.RequestUri!, body));

            return _answer(request);
        }
    }

    /// <summary>The Warning lines the handler logs, which is where a redirect it refused is reported.</summary>
    private sealed class WarningLog : ILogger<SameServerRedirectHandler>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Lines.Add(formatter(state, exception));
        }
    }

    /// <summary>A request as a <see cref="LoopbackServer"/> received it: its path, and its head as it was sent.</summary>
    private sealed record ReceivedRequest(string Path, string Head)
    {
        /// <summary>Whether the request carried header <paramref name="name"/> - with <paramref name="value"/>, if given.</summary>
        public bool HasHeader(string name, string? value = null) =>
            Head.Split("\r\n").Skip(1).Any(line =>
                line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase) &&
                (value is null || line[(name.Length + 1)..].Trim() == value));
    }

    /// <summary>
    /// An HTTP/1.1 server on a loopback port, answering each request with what its script returns for
    /// it and recording every request it is sent. One request per connection - every answer says
    /// <c>Connection: close</c> - which is all a client following or refusing a redirect needs.
    /// </summary>
    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<ReceivedRequest, string> _answer;
        private readonly Task _serving;

        private LoopbackServer(Func<ReceivedRequest, string> answer)
        {
            _answer = answer;
            _listener.Start();
            _serving = ServeAsync();
        }

        public static LoopbackServer Start(Func<ReceivedRequest, string> answer) => new(answer);

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public ConcurrentQueue<ReceivedRequest> Received { get; } = new();

        private async Task ServeAsync()
        {
            while (true)
            {
                using var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                var stream = connection.GetStream();

                // To the blank line ending the request's head - nothing sent here has a body - so the
                // request is not still sitting unread when the server hangs up.
                var head = new StringBuilder();
                var buffer = new byte[4096];
                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                if (head.Length == 0)
                {
                    continue;
                }

                var request = new ReceivedRequest(head.ToString().Split(' ', 3)[1], head.ToString());
                Received.Enqueue(request);

                await stream.WriteAsync(Encoding.ASCII.GetBytes(_answer(request)), _stop.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();

            try
            {
                await _serving;
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                // Waiting for the next connection is what stopping the server ends.
            }

            _stop.Dispose();
        }
    }
}
