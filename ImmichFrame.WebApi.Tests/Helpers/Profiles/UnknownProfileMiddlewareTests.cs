using ImmichFrame.Core.Interfaces;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Helpers.Profiles;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Helpers.Profiles;

/// <summary>
/// <see cref="UnknownProfileMiddleware"/> on its own rather than over the real host, because over the
/// host its absence does not show: the authentication handler resolving the unknown profile throws,
/// and <see cref="ProfileNotFoundMiddleware"/>, further out, answers that with the same 404. So these
/// pin what only this middleware does - turning the request away before anything after it runs, and
/// telling the operator so at Warning.
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
