using System.Collections.Concurrent;
using System.Net;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Helpers.Profiles;
using ImmichFrame.WebApi.Tests.Controllers;
using ImmichFrame.WebApi.Tests.Mocks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Helpers.Profiles;

/// <summary>
/// <see cref="UnknownProfileMiddleware"/> on its own rather than over the real host, because over the
/// host its absence barely shows: the authentication handler resolving the unknown profile throws,
/// and <see cref="ProfileNotFoundMiddleware"/>, further out, answers that with the same 404. So these
/// pin what only this middleware does - turning the request away before anything after it runs, and
/// telling the operator so at Warning. The one test over the host pins the Warning alone, which is
/// what shows the middleware is in the pipeline at all.
/// </summary>
[TestFixture]
public class UnknownProfileMiddlewareTests
{
    [Test]
    public async Task UnknownProfile_Is404BeforeAnythingDownstreamRuns()
    {
        var log = new WarningLog();
        var downstreamRan = false;
        var middleware = new UnknownProfileMiddleware(_ =>
        {
            downstreamRan = true;
            return Task.CompletedTask;
        }, log);
        var context = Request("?profile=bogus");

        await middleware.InvokeAsync(context, Catalog());

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(downstreamRan, Is.False, "nothing after this middleware may get to resolve the profile");
            Assert.That(log.Lines, Has.Exactly(1).Matches<string>(line => line.Contains("'bogus'")),
                "a mistyped profile is the operator's to hear about, by name");
        });
    }

    /// <summary>
    /// The name is whatever the query string said, so it reaches the log sanitized. Logged raw, an
    /// encoded CR LF would end the Warning early and start a line of the sender's own on the operator's
    /// console - a forged record, in the middle of the frame's log.
    /// </summary>
    [Test]
    public async Task UnknownProfile_IsLoggedWithoutTheLineBreaksItWasSentWith()
    {
        var log = new WarningLog();
        var middleware = new UnknownProfileMiddleware(_ => Task.CompletedTask, log);

        await middleware.InvokeAsync(Request("?profile=bad%0D%0AFORGED"), Catalog());

        Assert.That(log.Lines, Has.Exactly(1).Items);
        Assert.Multiple(() =>
        {
            Assert.That(log.Lines[0], Does.Not.Contain("\r").And.Not.Contain("\n"));
            Assert.That(log.Lines[0], Does.Contain("bad").And.Contain("FORGED"), "the operator is still told what was asked for");
        });
    }

    /// <summary>
    /// The middleware is in the host's pipeline, which is the one thing the tests above cannot show:
    /// they build it by hand. Over the host its 404 is not proof of that - without it the
    /// authentication handler throws resolving the profile, and <see cref="ProfileNotFoundMiddleware"/>
    /// answers with the same 404 and the same words. What only this middleware does is tell the
    /// operator, at Warning; caught out there, a mistyped profile is logged at Debug, as a request
    /// that outlived a configuration swap, which the default log level does not show.
    /// </summary>
    [Test]
    public async Task UnknownProfile_OverTheHost_Is404AndToldToTheOperator()
    {
        // Arrange
        var log = new HostLog();
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.UseMockHandler(new Mock<HttpMessageHandler>().WithServerVersion());
                    services.AddSingleton<IConfigCatalog>(AdminSessionControllerTests.Catalog());
                    services.AddSingleton<ILoggerProvider>(log);
                });
            });

        // Act
        var response = await factory.CreateClient().GetAsync("/api/Config/Version?profile=bogus");

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(log.Warnings, Has.Exactly(1).Contains("'bogus'"),
                "a mistyped profile is the operator's to hear about, by name");
        });
    }

    [Test]
    public async Task ConfiguredProfile_IsPassedOn()
    {
        var log = new WarningLog();
        var downstreamRan = false;
        var middleware = new UnknownProfileMiddleware(_ =>
        {
            downstreamRan = true;
            return Task.CompletedTask;
        }, log);

        await middleware.InvokeAsync(Request("?profile=kitchen"), Catalog());

        Assert.Multiple(() =>
        {
            Assert.That(downstreamRan, Is.True);
            Assert.That(log.Lines, Is.Empty);
        });
    }

    private static DefaultHttpContext Request(string queryString)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/Config";
        context.Request.QueryString = new QueryString(queryString);

        return context;
    }

    private static ConfigCatalog Catalog()
    {
        var settings = Mock.Of<IServerSettings>();

        return new ConfigCatalog(settings, [KeyValuePair.Create("kitchen", settings)]);
    }

    /// <summary>
    /// The Warning lines a whole host logs. Concurrent, because the startup version check logs from a
    /// thread of its own.
    /// </summary>
    private sealed class HostLog : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _warnings = new();

        public IEnumerable<string> Warnings => _warnings;

        public ILogger CreateLogger(string categoryName) => new Logger(_warnings);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> _warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning) _warnings.Enqueue(formatter(state, exception));
            }
        }
    }

    /// <summary>The Warning lines the middleware logs, which is where an unknown profile is reported.</summary>
    private sealed class WarningLog : ILogger<UnknownProfileMiddleware>
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
}
