using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using ImmichFrame.WebApi.Helpers.Admin;
using Moq;
using Moq.Protected;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Helpers.Admin;

/// <summary>
/// The response-size bound on the HTTP client the configuration editor's Immich picker goes out on.
/// It exists because an authenticated administrator chooses the target, so the request is a
/// deliberate server-side fetch of a URL this process did not pick - and the answer to that is a
/// bounded request rather than a refused one.
/// <para>
/// The other bound on that client, its refusal to follow redirects, is pinned where it can be read
/// off the application's own registrations:
/// <c>AdminImmichControllerTests.PickerHttpClient_IsWiredToRefuseRedirects</c>.
/// </para>
/// </summary>
[TestFixture]
public class AdminImmichClientBoundsTests
{
    /// <summary>The client timeout the real-socket tests run under: short, so the suite does not wait on it.</summary>
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The longest any real-socket test waits for an outcome: three times <see cref="ClientTimeout"/>,
    /// which is room for a slow machine and nowhere near "until the server gives up".
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    [Test]
    public void ResponseSizeLimit_StopsABodyThatNeverDeclaredItsLength()
    {
        // The case a Content-Length check cannot catch: a chunked response, or one from something
        // that is not an Immich server at all, streaming until this process runs out of memory.
        using var client = Bounded(limit: 1024, new UnsizedContent(4096));

        var exception = Assert.ThrowsAsync<ResponseTooLargeException>(
            () => client.GetAsync("http://immich.example.com/api/albums"));

        Assert.That(exception!.Limit, Is.EqualTo(1024));
    }

    [Test]
    public void ResponseSizeLimit_LeavesAConnectionThatDropsMidBodyLookingLikeWhatItIs()
    {
        // The other half of what the handler reports. Both an overrun and a mid-body failure are
        // HttpRequestExceptions, so every caller still treats either as an ordinary outcome - but a
        // drop must not come back claiming the server sent too much, the one answer that would send
        // an administrator looking at the wrong thing. The same drop on a real connection, whose
        // stream fails differently, is ResponseSizeLimit_AConnectionThatDropsPartWay_IsAnOrdinaryTransportFailure.
        using var client = Bounded(limit: 1024, new FailingContent());

        var exception = Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("http://immich.example.com/api/albums"));

        Assert.That(exception, Is.Not.InstanceOf<ResponseTooLargeException>());
    }

    [Test]
    public void ResponseSizeLimit_PassesABodyWithinTheLimitThrough()
    {
        using var client = Bounded(limit: 1024, new UnsizedContent(16));

        var response = client.GetAsync("http://immich.example.com/api/albums").GetAwaiter().GetResult();

        Assert.That(response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult(), Has.Length.EqualTo(16));
    }

    /// <summary>
    /// A length declared over the limit is refused on the header alone. Whatever stands behind the
    /// header may be as long as it says, so reading it to find out would be exactly the work the
    /// limit exists to refuse.
    /// </summary>
    [Test]
    public void ResponseSizeLimit_ADeclaredLengthOverTheLimit_IsRefusedWithoutReadingTheBody()
    {
        var content = new SizedContent(1025);
        using var client = Bounded(limit: 1024, content);

        var exception = Assert.ThrowsAsync<ResponseTooLargeException>(
            () => client.GetAsync("http://immich.example.com/api/albums"));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Limit, Is.EqualTo(1024));
            Assert.That(content.Read, Is.False, "the body was read although its declared length was already over the limit");
        });
    }

    /// <summary>The limit is the most that will be read, not the first length that is refused.</summary>
    [Test]
    public void ResponseSizeLimit_ADeclaredLengthOfExactlyTheLimit_IsReadInFull()
    {
        using var client = Bounded(limit: 1024, new SizedContent(1024));

        var response = client.GetAsync("http://immich.example.com/api/albums").GetAwaiter().GetResult();

        Assert.That(response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult(), Has.Length.EqualTo(1024));
    }

    /// <summary>
    /// A refused response is released here or nowhere: the caller is handed the exception and never
    /// the response, so one left undisposed would hold its connection - and whatever of the body had
    /// been read - until a finalizer got round to it.
    /// </summary>
    [Test]
    public void ResponseSizeLimit_DisposesTheResponseItRefuses()
    {
        var dropped = new FailingContent();
        var announcedTooMuch = new SizedContent(2048);

        using var droppingClient = Bounded(limit: 1024, dropped);
        using var announcingClient = Bounded(limit: 1024, announcedTooMuch);

        Assert.ThrowsAsync<HttpRequestException>(() => droppingClient.GetAsync("http://immich.example.com/api/albums"));
        Assert.ThrowsAsync<ResponseTooLargeException>(() => announcingClient.GetAsync("http://immich.example.com/api/albums"));

        Assert.Multiple(() =>
        {
            Assert.That(dropped.Disposed, Is.True, "a body that failed part-way was not released");
            Assert.That(announcedTooMuch.Disposed, Is.True, "a body refused on its declared length was not released");
        });
    }

    /// <summary>
    /// The threat this handler exists for, on a real connection: a host whose answer never ends. It
    /// is cut off at the limit rather than read until the process runs out of memory, or until the
    /// client's timeout happens to fire first.
    /// </summary>
    [Test]
    public async Task ResponseSizeLimit_ABodyThatNeverEnds_IsCutOffAtTheLimit()
    {
        await using var server = ScriptedServer.Start(async (stream, stop) =>
        {
            await stream.WriteAsync(Ascii("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n"), stop);

            var chunk = Ascii($"1000\r\n{new string('x', 0x1000)}\r\n");
            while (true)
            {
                await stream.WriteAsync(chunk, stop);
            }
        });

        using var client = OverTheNetwork(limit: 64 * 1024, Deadline);

        var exception = Assert.ThrowsAsync<ResponseTooLargeException>(() => Fetch(client, server.Url));

        Assert.That(exception!.Limit, Is.EqualTo(64 * 1024));
    }

    /// <summary>
    /// A real connection's stream reports a body cut short as a bare <see cref="IOException"/>, and
    /// HttpClient passes that on as it is. Every caller of this client takes
    /// <see cref="HttpRequestException"/> as the ordinary "could not reach" outcome and lets anything
    /// else through as a 500, so the drop has to arrive as exactly that - which is what
    /// <c>ThrowsAsync</c> checks, since it matches the type exactly: neither the bare IOException,
    /// nor the server having sent too much.
    /// </summary>
    [Test]
    public async Task ResponseSizeLimit_AConnectionThatDropsPartWay_IsAnOrdinaryTransportFailure()
    {
        await using var server = ScriptedServer.Start(async (stream, stop) =>
        {
            await stream.WriteAsync(Ascii("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n"), stop);
            await stream.WriteAsync(new byte[10], stop);
        });

        using var client = OverTheNetwork(limit: 1024, Deadline);

        Assert.ThrowsAsync<HttpRequestException>(() => Fetch(client, server.Url));
    }

    /// <summary>
    /// The headers arrive, and then the body stops part-way with the connection held open. Nothing
    /// in the framework ends that on its own: SocketsHttpHandler stops watching the request's token
    /// once it has handed the headers back, so a body read without that token outlives the client's
    /// timeout for as long as the far end cares to keep the connection - read through
    /// <c>LoadIntoBufferAsync</c>, a one-second timeout ended only when the server gave up. An
    /// administrator's typo can name such a host as easily as any other.
    /// </summary>
    [Test]
    public async Task ResponseSizeLimit_ABodyThatStallsPartWay_EndsWithTheClientsTimeout()
    {
        await using var server = ScriptedServer.Start(async (stream, stop) =>
        {
            await stream.WriteAsync(Ascii("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n"), stop);
            await stream.WriteAsync(new byte[10], stop);
            await Task.Delay(Timeout.Infinite, stop);
        });

        using var client = OverTheNetwork(limit: 1024, ClientTimeout);

        var sending = Fetch(client, server.Url);

        Assert.That(await Task.WhenAny(sending, Task.Delay(Deadline)), Is.SameAs(sending),
            $"the request was still reading a stalled body {Deadline.TotalSeconds} s after a {ClientTimeout.TotalSeconds} s timeout");

        // HttpClient's own account of its timeout, exactly as a server that never sends its headers
        // produces - which is the outcome every caller of this client already handles.
        var exception = Assert.ThrowsAsync<TaskCanceledException>(() => sending);
        Assert.That(exception!.InnerException, Is.InstanceOf<TimeoutException>());
    }

    private static HttpClient Bounded(long limit, HttpContent content)
    {
        var inner = new Mock<HttpMessageHandler>();
        inner.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        return new HttpClient(new ResponseSizeLimitHandler(limit) { InnerHandler = inner.Object });
    }

    /// <summary>
    /// The handler in front of the picker's own primary handler, chained the way <c>Program.cs</c>
    /// chains them, so that what the handler reads from is a real connection's stream.
    /// </summary>
    private static HttpClient OverTheNetwork(long limit, TimeSpan timeout) =>
        new(new ResponseSizeLimitHandler(limit) { InnerHandler = AdminImmichAccounts.CreatePrimaryHandler() })
        {
            Timeout = timeout
        };

    /// <summary>
    /// <c>ResponseHeadersRead</c>, as the generated Immich client asks for: the body read under test
    /// has to be the handler's own, not one HttpClient makes after the handler has returned.
    /// </summary>
    private static Task<HttpResponseMessage> Fetch(HttpClient client, Uri url) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead);

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    /// <summary>A body of a known size that refuses to say so, the way a chunked response does.</summary>
    private sealed class UnsizedContent(int _bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(new byte[_bytes], 0, _bytes);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>A body that starts arriving and then stops, the way a dropped connection does.</summary>
    private sealed class FailingContent : HttpContent
    {
        public bool Disposed { get; private set; }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(new byte[8], 0, 8);

            throw new IOException("The connection was reset.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A body that declares its length, the way a Content-Length header does, and records whether
    /// anything ever read it or let it go.
    /// </summary>
    private sealed class SizedContent(int _bytes) : HttpContent
    {
        public bool Read { get; private set; }
        public bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Read = true;

            return stream.WriteAsync(new byte[_bytes], 0, _bytes);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _bytes;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// One HTTP/1.1 exchange on a loopback port, scripted byte for byte: the server reads the
    /// request's head, hands the connection to the script, and hangs up when the script returns or
    /// the server is disposed - whichever comes first, so no test leaves a connection open behind it.
    /// <para>
    /// A real socket rather than a mocked handler, because what these tests pin is how the
    /// connection's own stream behaves under the handler, and a mocked content is a different
    /// stream with different manners: it does not, for one, stop listening to the request's token
    /// once the headers are back.
    /// </para>
    /// </summary>
    private sealed class ScriptedServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        private ScriptedServer(Func<NetworkStream, CancellationToken, Task> script)
        {
            _listener.Start();
            _serving = ServeAsync(script);
        }

        public static ScriptedServer Start(Func<NetworkStream, CancellationToken, Task> script) => new(script);

        public Uri Url => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/api/albums");

        private async Task ServeAsync(Func<NetworkStream, CancellationToken, Task> script)
        {
            using var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
            var stream = connection.GetStream();

            // Read to the blank line ending the request's head - a GET has no body - so the request
            // is not still sitting unread when the server hangs up, which would turn the hang-up
            // into a reset.
            var head = new StringBuilder();
            var buffer = new byte[4096];
            while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                {
                    return;
                }

                head.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            await script(stream, _stop.Token);
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
                // Whatever the script was in the middle of - a stall, or a write to a client that had
                // already hung up - ends with the connection, which is the point of stopping it.
            }

            _stop.Dispose();
        }
    }
}
