using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ImmichFrame.Core.Helpers;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Models;
using ImmichFrame.WebApi.Tests.Mocks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests;

/// <summary>
/// The webhook from outside the process: what its receiver is sent, and what the log shows of the
/// client sending it. The payload is a contract with every automation that reads one, and a receiver
/// may well match on it as text, so its bytes are pinned rather than its shape.
/// </summary>
[TestFixture]
public class WebhookTests
{
    // Turns a hang into a failure rather than a stalled run.
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Test]
    public async Task SendWebhookNotification_AssetRequested_PayloadIsByteForByteUnchanged()
    {
        // Arrange - local, like the DateTime.Now a notification is stamped with, so the offset written
        // is the machine's and is worked out below rather than written down. In UTC, where CI and the
        // container run, it reads \u002B00:00: PolymorphicJsonConverter writes each value back out
        // through a JsonDocument, which escapes the '+', where serializing the runtime type directly
        // would leave it bare.
        var requestedAt = new DateTime(2024, 5, 17, 8, 30, 15, DateTimeKind.Local).AddTicks(1234567);
        var notification = new AssetRequestedNotification(Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301"), "Küche")
        {
            DateTime = requestedAt
        };

        // The writer breaks indented lines with Environment.NewLine.
        var expected = $$"""
            {
              "Name": "AssetRequestedNotification",
              "ClientIdentifier": "K\u00FCche",
              "DateTime": "2024-05-17T08:30:15.1234567{{Offset(requestedAt)}}",
              "RequestedAssetId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301"
            }
            """.ReplaceLineEndings(Environment.NewLine);

        // Act
        using var client = new HttpClient();
        var received = await Receive(url => WebhookHelper.SendWebhookNotification(notification, url, client, NullLogger.Instance));

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(received.Method, Is.EqualTo("POST"));
            Assert.That(received.ContentType, Is.EqualTo("application/json; charset=utf-8"));
            Assert.That(received.Body, Is.EqualTo(Encoding.UTF8.GetBytes(expected)),
                $"the payload sent was:{Environment.NewLine}{Encoding.UTF8.GetString(received.Body)}");
        });
    }

    /// <summary>
    /// The client notifications go out on is registered in <c>Program.cs</c>, away from the helper
    /// that relies on it, so what it is registered with is pinned here: a timeout of its own, and none
    /// of the factory's request logging - which writes each request's full URL, the secret a webhook
    /// URL commonly carries in its path included.
    /// </summary>
    [Test]
    public async Task WebhookHttpClient_IsBoundedAndKeepsTheUrlOutOfTheLog()
    {
        // Arrange
        var logs = new LogSink();
        var receiver = new Mock<HttpMessageHandler>().WithServerVersion();
        receiver.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.EndsWith("/secret-id")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.NoContent));

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.UseMockHandler(receiver);
                    services.AddSingleton<IConfigCatalog>(new ConfigCatalog(Settings()));

                    // Every HttpClient entry reaches the sink, whatever LOG_LEVEL is: Program.cs lets the
                    // factory's Information-level request logging through only at Debug or lower, and a
                    // rule naming the sink outranks that one.
                    services.AddSingleton<ILoggerProvider>(logs);
                    services.Configure<LoggerFilterOptions>(options =>
                        options.AddFilter<LogSink>("System.Net.Http.HttpClient", LogLevel.Trace));
                });
            });

        var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(WebhookHelper.HttpClientName);

        // Act
        using var response = await client.PostAsync("http://webhook.example.com/api/webhook/secret-id", new StringContent("{}"));

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(client.Timeout, Is.EqualTo(WebhookHelper.RequestTimeout));
            Assert.That(logs.Messages, Has.None.Contains("secret-id"));
        });
    }

    private static ServerSettings Settings() => new()
    {
        GeneralSettingsImpl = new GeneralSettings(),
        AccountsImpl = new List<ServerAccountSettings>
        {
            new() { ImmichServerUrl = "http://mock-immich-server.com", ApiKey = "test-api-key" }
        }
    };

    // A DateTime's offset as the converter writes it: a negative one as it is, a positive one's '+'
    // escaped.
    private static string Offset(DateTime local)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        return (offset < TimeSpan.Zero ? "-" : @"\u002B") + offset.ToString(@"hh\:mm");
    }

    private sealed record ReceivedRequest(string Method, string? ContentType, byte[] Body);

    /// <summary>
    /// What a receiver listening on a loopback port is sent when <paramref name="send"/> posts to the
    /// URL it is given. Read off a real request, so what is pinned is what crosses the wire, whatever
    /// the sending side looks like.
    /// </summary>
    private static async Task<ReceivedRequest> Receive(Func<string, Task> send)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{FreePort()}/");
        listener.Start();

        var sending = send($"{listener.Prefixes.Single()}api/webhook/frame");
        var context = await listener.GetContextAsync().WaitAsync(WaitLimit);

        using var body = new MemoryStream();
        await context.Request.InputStream.CopyToAsync(body);
        var received = new ReceivedRequest(context.Request.HttpMethod, context.Request.ContentType, body.ToArray());

        context.Response.StatusCode = (int)HttpStatusCode.NoContent;
        context.Response.Close();
        await sending.WaitAsync(WaitLimit);

        return received;
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    /// <summary>
    /// Every message the host logs. Concurrent, because the startup version check logs from a thread
    /// of its own.
    /// </summary>
    private sealed class LogSink : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IEnumerable<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new Logger(_messages);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> _messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                _messages.Enqueue(formatter(state, exception));
        }
    }
}
