using System.Net;
using System.Net.Http;
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
        // The other half of the discrimination the handler makes. LoadIntoBufferAsync reports both
        // an overrun and a mid-body IO failure as HttpRequestException; the handler tells them apart
        // by whether one wraps an inner exception. That reading of the framework is what this test
        // pins - a drop must not come back claiming the server sent too much.
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

    private static HttpClient Bounded(long limit, HttpContent content)
    {
        var inner = new Mock<HttpMessageHandler>();
        inner.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        return new HttpClient(new ResponseSizeLimitHandler(limit) { InnerHandler = inner.Object });
    }

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
    }
}
