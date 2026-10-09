using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Models;
using ImmichFrame.WebApi.Controllers;
using ImmichFrame.WebApi.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Controllers;

/// <summary>
/// <see cref="AssetController.GetAsset"/> called directly against a fake
/// <see cref="IImmichFrameLogic"/>, for what a test host cannot show: what the action does once the
/// client has hung up. Kestrel goes on accepting writes to a response whose client has gone - it
/// throws them away without a word, and fails one only when it is handed a cancelled token - and
/// TestServer does not behave that way, so the response here is written into a stream that does.
/// The round trip through a real host is <see cref="AssetControllerTests"/>' business.
/// </summary>
[TestFixture]
public class AssetControllerGetAssetTests
{
    private readonly Guid _assetId = Guid.NewGuid();
    private Mock<IImmichFrameLogic> _logic;

    [SetUp]
    public void Setup()
    {
        _logic = new Mock<IImmichFrameLogic>();
        _logic.Setup(logic => logic.SendWebhookNotification(It.IsAny<IWebhookNotification>()))
            .Returns(Task.CompletedTask);
    }

    /// <summary>
    /// A frame hangs up on a video whenever it moves on before the video has finished downloading -
    /// and a browser's first request for one asks for the whole of it, bytes=0- onwards. The range is
    /// copied out of Immich's answer by this action rather than by ASP.NET, so it is this action that
    /// has to notice: copying on regardless pulled the rest of the video from Immich, every byte of it
    /// thrown away, for every slide a frame moved on from.
    /// </summary>
    [Test]
    public void GetAsset_ClientHangsUpPartWayThroughARange_StopsReadingTheVideoFromImmich()
    {
        // Arrange - a video many times the size of one piece of the copy, and a client that is gone as
        // soon as the first piece has been written to it
        const long length = 10 * 1024 * 1024;
        var video = new UpstreamVideo(length);
        var owner = new TrackingDisposable();
        _logic.Setup(logic => logic.GetAsset(_assetId, AssetTypeEnum.VIDEO, "bytes=0-", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssetResponse
            {
                FileName = $"{_assetId}.mp4",
                ContentType = "video/mp4",
                FileStream = video,
                ContentRange = $"bytes 0-{length - 1}/{length}",
                IsPartial = true,
                Owner = owner,
                ContentLength = length
            });

        using var requestAborted = new CancellationTokenSource();
        var controller = CreateController("bytes=0-", requestAborted);

        // Act
        IActionResult? result = null;
        Assert.That(async () => result = await controller.GetAsset(_assetId, assetType: AssetTypeEnum.VIDEO),
            Throws.Nothing, "a client that has gone is no error of the action's");

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(video.BytesRead, Is.LessThan(length), "the rest of the video is left on Immich");
            Assert.That(result, Is.InstanceOf<EmptyResult>());
            Assert.That(owner.Disposed, Is.True, "and Immich's response let go of");
        });
    }

    /// <summary>
    /// The same hang-up a step earlier, while Immich is still to answer. The request to Immich goes
    /// with it, and what reaches the action is that cancellation - no error of the action's either,
    /// and no showing for the webhook to be told of, since nothing was shown.
    /// </summary>
    [Test]
    public void GetAsset_ClientHangsUpWhileImmichIsStillToAnswer_AnswersNothingAndNotifiesNobody()
    {
        // Arrange - Immich is asked on the request's own token, and is cancelled with it
        using var requestAborted = new CancellationTokenSource();
        requestAborted.Cancel();
        _logic.Setup(logic => logic.GetAsset(_assetId, AssetTypeEnum.VIDEO, "bytes=0-", requestAborted.Token))
            .ThrowsAsync(new TaskCanceledException(null, null, requestAborted.Token));
        var controller = CreateController("bytes=0-", requestAborted);

        // Act
        IActionResult? result = null;
        Assert.That(async () => result = await controller.GetAsset(_assetId, assetType: AssetTypeEnum.VIDEO),
            Throws.Nothing, "a client that has gone is no error of the action's");

        // Assert
        Assert.That(result, Is.InstanceOf<EmptyResult>());
        _logic.Verify(logic => logic.SendWebhookNotification(It.IsAny<IWebhookNotification>()), Times.Never);
    }

    private AssetController CreateController(string range, CancellationTokenSource requestAborted)
    {
        var httpContext = new DefaultHttpContext { RequestAborted = requestAborted.Token };
        httpContext.Request.Headers.Range = range;
        httpContext.Response.Body = new HungUpResponseBody(requestAborted);

        return new AssetController(NullLogger<AssetController>.Instance, _logic.Object, new GeneralSettings())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    /// <summary>
    /// A video as Immich streams it: not seekable, cancelled as a network read is, and counting how
    /// much of it has been read.
    /// </summary>
    private sealed class UpstreamVideo(long length) : Stream
    {
        public long BytesRead { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, length - BytesRead);
            buffer[..count].Clear();
            BytesRead += count;
            return count;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A response body as Kestrel's is once its client has hung up: every write is accepted and thrown
    /// away, and only one handed a cancelled token fails - and, as Kestrel's always does, it refuses a
    /// synchronous one. This client hangs up, aborting the request, as soon as the first write reaches
    /// it.
    /// </summary>
    private sealed class HungUpResponseBody(CancellationTokenSource requestAborted) : Stream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requestAborted.Cancel();
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
