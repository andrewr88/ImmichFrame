using System.Net;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic;
using ImmichFrame.Core.Logic.AccountSelection;
using ImmichFrame.Core.Tests.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using NUnit.Framework;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace ImmichFrame.Core.Tests.Logic;

[TestFixture]
public class MultiImmichFrameLogicDelegateTests
{
    // With a secret in its path, as a Home Assistant webhook has.
    private const string WebhookUrl = "http://webhook.test/api/webhook/secret-id";

    private readonly Guid _assetId = Guid.NewGuid();
    private Mock<IAccountImmichFrameLogic>[] _accounts;
    private Mock<IGeneralSettings> _generalSettings;
    private Mock<HttpMessageHandler> _webhookReceiver;
    private Mock<IHttpClientFactory> _httpClientFactory;
    private ListLogger<MultiImmichFrameLogicDelegate> _logger;
    private MultiImmichFrameLogicDelegate _sut;

    [SetUp]
    public async Task Setup()
    {
        _accounts = [CreateAccount("http://immich-one"), CreateAccount("http://immich-two")];
        var accounts = _accounts.Select(a => a.Object).ToList();

        _generalSettings = new Mock<IGeneralSettings>();
        _generalSettings.SetupGet(s => s.Webhook).Returns(WebhookUrl);
        var serverSettings = new Mock<IServerSettings>();
        serverSettings.SetupGet(s => s.Accounts).Returns(accounts.Select(a => a.AccountSettings).ToList());
        serverSettings.SetupGet(s => s.GeneralSettings).Returns(_generalSettings.Object);

        // The webhook's receiver is faked below HttpClient, so the helper's own request and response
        // handling still runs. Only the webhook's named client is set up: asked for any other, the
        // factory hands back null and the notification fails.
        _webhookReceiver = new Mock<HttpMessageHandler>();
        _httpClientFactory = new Mock<IHttpClientFactory>();
        _httpClientFactory.Setup(f => f.CreateClient(WebhookHelper.HttpClientName))
            .Returns(() => new HttpClient(_webhookReceiver.Object));

        _logger = new ListLogger<MultiImmichFrameLogicDelegate>();

        // The real tracker and strategy: the fallback to the next account lives in the tracker, and
        // only these Task-returning lookups show whether it holds.
        var tracker = new BloomFilterAssetAccountTracker(NullLogger<BloomFilterAssetAccountTracker>.Instance);
        _sut = new MultiImmichFrameLogicDelegate(
            serverSettings.Object,
            settings => accounts.Single(a => a.AccountSettings == settings),
            _logger,
            logics => new TotalAccountImagesSelectionStrategy(
                NullLogger<TotalAccountImagesSelectionStrategy>.Instance, tracker, logics),
            _httpClientFactory.Object);

        // Both filters claim the asset, as they do for a Bloom false positive or an asset both
        // accounts can see.
        foreach (var account in accounts)
            await tracker.RecordAssetLocation(account, _assetId);
    }

    [TearDown]
    public void TearDown()
    {
        _sut.Dispose();
    }

    private static Mock<IAccountImmichFrameLogic> CreateAccount(string serverUrl)
    {
        var settings = new Mock<IAccountSettings>();
        settings.SetupGet(s => s.ImmichServerUrl).Returns(serverUrl);

        var account = new Mock<IAccountImmichFrameLogic>();
        account.SetupGet(a => a.AccountSettings).Returns(settings.Object);
        account.Setup(a => a.GetTotalAssets()).ReturnsAsync(10);
        return account;
    }

    private static ApiException ImmichError(int status) =>
        new($"Unexpected status code ({status}).", status, string.Empty, new Dictionary<string, IEnumerable<string>>(), null!);

    // Answers a POST to the configured webhook only, so a notification sent anywhere else gets no
    // response and fails.
    private void ReceiverAnswers(HttpStatusCode status, HttpContent? body = null) =>
        _webhookReceiver.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Post && r.RequestUri == new Uri(WebhookUrl)),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(status) { Content = body });

    private void VerifyWebhookRequests(Times times) =>
        _webhookReceiver.Protected().Verify("SendAsync", times, ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());

    private List<string> Warnings() =>
        _logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Text).ToList();

    private List<string> Logged() => _logger.Entries.Select(e => e.Text).ToList();

    private sealed class TestNotification : IWebhookNotification
    {
        public string Name { get; set; } = nameof(TestNotification);
        public DateTime DateTime { get; set; } = DateTime.Now;
    }

    // Ten assets for each account, which the tests tell apart by id.
    private Dictionary<Guid, Mock<IAccountImmichFrameLogic>> EachAccountHandsOutTen()
    {
        var accountOf = new Dictionary<Guid, Mock<IAccountImmichFrameLogic>>();
        foreach (var account in _accounts)
        {
            var assets = Enumerable.Range(0, 10).Select(_ => new AssetResponseDto { Id = Guid.NewGuid() }).ToList();
            account.Setup(a => a.GetAssets()).ReturnsAsync(assets);
            foreach (var asset in assets)
                accountOf[asset.Id] = account;
        }

        return accountOf;
    }

    /// <summary>
    /// The frame fetches each asset from the server named on it, so an asset has to carry the server
    /// of the account it came from.
    /// </summary>
    [Test]
    public async Task GetAssets_EachAssetCarriesItsOwnAccountsServer()
    {
        // Arrange
        var accountOf = EachAccountHandsOutTen();

        // Act
        var batch = (await _sut.GetAssets()).ToList();

        // Assert
        Assert.That(batch, Has.Count.EqualTo(20));
        Assert.That(batch.Select(asset => asset.ImmichServerUrl),
            Is.EqualTo(batch.Select(asset => accountOf[asset.Id].Object.AccountSettings.ImmichServerUrl)));
    }

    /// <summary>
    /// The strategy hands each account's share back in a block, and the frame shows a batch in order:
    /// left as it came, every batch showed one account's photos and then the other's.
    /// </summary>
    [Test]
    public async Task GetAssets_MixesTheAccountsTogether()
    {
        // Arrange
        var accountOf = EachAccountHandsOutTen();

        // Act - three batches, so that a shuffle leaving one in blocks by chance - 2 in 184,756 for
        // two accounts of ten - cannot fail the test
        var changesOfAccount = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            var accounts = (await _sut.GetAssets()).Select(asset => accountOf[asset.Id]).ToList();
            changesOfAccount.Add(accounts.Zip(accounts.Skip(1)).Count(pair => pair.First != pair.Second));
        }

        // Assert - in blocks, a batch changes account once
        Assert.That(changesOfAccount, Has.Some.GreaterThan(1));
    }

    [Test]
    public async Task GetAssetInfoById_CarriesTheServerOfTheAccountThatAnswered()
    {
        // Arrange
        IAccountImmichFrameLogic? answered = null;
        foreach (var account in _accounts)
        {
            account.Setup(a => a.GetAssetInfoById(_assetId)).ReturnsAsync(() =>
            {
                answered = account.Object;
                return new AssetResponseDto { Id = _assetId };
            });
        }

        // Act
        var asset = await _sut.GetAssetInfoById(_assetId);

        // Assert
        Assert.That(asset.ImmichServerUrl, Is.EqualTo(answered!.AccountSettings.ImmichServerUrl));
    }

    [Test]
    public async Task GetAssetInfoById_FirstAccountFailsAsynchronously_ServedByNextAccount()
    {
        // Arrange - the tracker's enumeration order is not fixed, so whichever account is asked
        // first fails, and it fails the way a real lookup does: inside the Task it returns.
        var calls = 0;
        foreach (var account in _accounts)
        {
            account.Setup(a => a.GetAssetInfoById(_assetId)).Returns(async () =>
            {
                await Task.Yield();
                if (Interlocked.Increment(ref calls) == 1) throw new AssetNotFoundException();
                return new AssetResponseDto { Id = _assetId };
            });
        }

        // Act
        var asset = await _sut.GetAssetInfoById(_assetId);

        // Assert
        Assert.That(asset.Id, Is.EqualTo(_assetId));
    }

    [Test]
    public async Task GetAlbumInfoById_FirstAccountThrowsSynchronously_ServedByNextAccount()
    {
        // Arrange - the other way an account can fail: throwing before it hands back a Task at all.
        IEnumerable<AlbumResponseDto> albums = [new AlbumResponseDto()];
        var calls = 0;
        foreach (var account in _accounts)
        {
            account.Setup(a => a.GetAlbumInfoById(_assetId)).Returns(() =>
                Interlocked.Increment(ref calls) == 1
                    ? throw new HttpRequestException("Connection refused")
                    : Task.FromResult(albums));
        }

        // Act
        var result = await _sut.GetAlbumInfoById(_assetId);

        // Assert
        Assert.That(result, Is.SameAs(albums));
    }

    /// <summary>
    /// Immich answers an album lookup for an asset the account does not hold with no albums rather
    /// than an error. An account the tracker names wrongly - a Bloom false positive - so ended the
    /// lookup with nothing, and the account holding the asset was never asked.
    /// </summary>
    [Test]
    public async Task GetAlbumInfoById_FirstAccountAnswersNoAlbums_ServedByNextAccount()
    {
        // Arrange - whichever account is asked first answers as one not holding the asset does
        IEnumerable<AlbumResponseDto> albums = [new AlbumResponseDto()];
        var calls = 0;
        foreach (var account in _accounts)
        {
            account.Setup(a => a.GetAlbumInfoById(_assetId)).ReturnsAsync(() =>
                Interlocked.Increment(ref calls) == 1 ? [] : albums);
        }

        // Act
        var result = await _sut.GetAlbumInfoById(_assetId);

        // Assert
        Assert.That(result, Is.SameAs(albums));
    }

    [Test]
    public async Task GetAlbumInfoById_InNoAlbumOnAnyAccount_AnswersNoAlbumsOnceEveryAccountIsAsked()
    {
        // Arrange - an asset in no album at all, which is an answer rather than a failure
        foreach (var account in _accounts)
            account.Setup(a => a.GetAlbumInfoById(_assetId)).ReturnsAsync([]);

        // Act
        var result = await _sut.GetAlbumInfoById(_assetId);

        // Assert
        Assert.That(result, Is.Empty);
        foreach (var account in _accounts)
            account.Verify(a => a.GetAlbumInfoById(_assetId), Times.Once);
    }

    [Test]
    public void GetAssetFacesById_EveryAccountFails_ThrowsAssetNotFound()
    {
        // Arrange
        foreach (var account in _accounts)
            account.Setup(a => a.GetAssetFacesById(_assetId)).ThrowsAsync(ImmichError(400));

        // Act / Assert
        Assert.That(async () => await _sut.GetAssetFacesById(_assetId), Throws.TypeOf<AssetNotFoundException>());
        foreach (var account in _accounts)
            account.Verify(a => a.GetAssetFacesById(_assetId), Times.Once);
    }

    [Test]
    public void GetAsset_RangeNotSatisfiable_ReachesCallerUnchanged()
    {
        // Arrange - a 416 means the account holds the asset and the range lies outside it. It is
        // no false positive, and AssetController answers exactly this exception with a 416, so it
        // must not come back as AssetNotFoundException.
        const string pastTheEnd = "bytes=999999999-";
        foreach (var account in _accounts)
        {
            account.Setup(a => a.GetAsset(_assetId, AssetTypeEnum.VIDEO, pastTheEnd, It.IsAny<CancellationToken>()))
                .ThrowsAsync(ImmichError(416));
        }

        // Act
        var thrown = Assert.ThrowsAsync<ApiException>(() => _sut.GetAsset(_assetId, AssetTypeEnum.VIDEO, pastTheEnd));

        // Assert
        Assert.That(thrown!.StatusCode, Is.EqualTo(416));
    }

    [Test]
    public void GetAsset_CallerGivesUp_ReachesCallerAsACancellationWithNoOtherAccountAsked()
    {
        // Arrange - the caller has hung up, and the account asked first says so as its request to
        // Immich does, by throwing the cancellation. That is no false positive either: there is
        // nobody left to find the asset for, so neither another account nor a 404 should follow.
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        foreach (var account in _accounts)
        {
            account.Setup(a => a.GetAsset(_assetId, AssetTypeEnum.VIDEO, null, caller.Token))
                .ThrowsAsync(new TaskCanceledException(null, null, caller.Token));
        }

        // Act
        Assert.That(async () => await _sut.GetAsset(_assetId, AssetTypeEnum.VIDEO, null, caller.Token),
            Throws.InstanceOf<OperationCanceledException>());

        // Assert
        var asked = _accounts.Sum(account =>
            account.Invocations.Count(invocation => invocation.Method.Name == nameof(IImmichFrameLogic.GetAsset)));
        Assert.That(asked, Is.EqualTo(1));
    }

    [Test]
    public void SendWebhookNotification_WebhookUnreachable_CompletesAndLogsAWarning()
    {
        // Arrange - nothing listens on port 1, so the connection is refused at once, as it is for a
        // receiver that is down or a host that is misconfigured. AssetController discards the task
        // this returns, so an exception escaping it was observed by nothing - the log included. A
        // real client, so the failure is the one HttpClient really throws.
        _generalSettings.SetupGet(s => s.Webhook).Returns("http://127.0.0.1:1/api/webhook/secret-id");
        using var network = new HttpClient();
        _httpClientFactory.Setup(f => f.CreateClient(WebhookHelper.HttpClientName)).Returns(network);

        // Act / Assert
        Assert.That(async () => await _sut.SendWebhookNotification(new TestNotification()), Throws.Nothing);
        Assert.Multiple(() =>
        {
            Assert.That(Warnings(), Has.One.Contains(nameof(HttpRequestException)));
            Assert.That(Logged(), Has.None.Contains("secret-id"));
        });
    }

    [Test]
    public async Task SendWebhookNotification_ReceiverRejectsIt_LogsAWarningWithTheStatus()
    {
        // Arrange
        ReceiverAnswers(HttpStatusCode.NotFound);

        // Act
        await _sut.SendWebhookNotification(new TestNotification());

        // Assert - the status is what an operator needs; the URL, which holds the secret, is not
        VerifyWebhookRequests(Times.Once());
        Assert.Multiple(() =>
        {
            Assert.That(Warnings(), Has.One.Contains("404"));
            Assert.That(Logged(), Has.None.Contains("secret-id"));
        });
    }

    [Test]
    public async Task SendWebhookNotification_ReceiverAcceptsIt_LogsOnlyAtDebug()
    {
        // Arrange
        ReceiverAnswers(HttpStatusCode.NoContent);

        // Act
        await _sut.SendWebhookNotification(new TestNotification());

        // Assert - reported through the logger, so LOG_LEVEL decides whether it is shown at all
        VerifyWebhookRequests(Times.Once());
        Assert.That(_logger.Entries.Select(e => e.Level), Is.EqualTo(new[] { LogLevel.Debug }));
    }

    /// <summary>
    /// Nothing reads what a receiver answers but its status, so nothing waits for the rest. The body
    /// was read in full before the status was looked at - a notification for every asset served, each
    /// holding its connection, and the body in memory, for as long as the receiver took to send it.
    /// </summary>
    [Test]
    public async Task SendWebhookNotification_ReceiverAnswersWithABody_LeavesItUnreadAndLetsGoOfIt()
    {
        // Arrange
        var body = new WatchedBody();
        ReceiverAnswers(HttpStatusCode.OK, new StreamContent(body));

        // Act
        await _sut.SendWebhookNotification(new TestNotification());

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(body.WasRead, Is.False, "the body was read");
            Assert.That(body.IsDisposed, Is.True, "the response was not let go of");
            Assert.That(_logger.Entries.Select(e => e.Level), Is.EqualTo(new[] { LogLevel.Debug }));
        });
    }

    /// <summary>
    /// A response body that records whether anything read it, and whether it was disposed - which
    /// it is when the response it belongs to is.
    /// </summary>
    private sealed class WatchedBody : Stream
    {
        public bool WasRead { get; private set; }
        public bool IsDisposed { get; private set; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            WasRead = true;
            return 0;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
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

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task SendWebhookNotification_NoWebhookConfigured_SendsNothing(string? webhook)
    {
        // Arrange
        _generalSettings.SetupGet(s => s.Webhook).Returns(webhook);

        // Act
        await _sut.SendWebhookNotification(new TestNotification());

        // Assert
        VerifyWebhookRequests(Times.Never());
        Assert.That(Logged(), Is.Empty);
    }
}
