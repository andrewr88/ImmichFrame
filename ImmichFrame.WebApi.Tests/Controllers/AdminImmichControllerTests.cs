using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImmichFrame.Core.Api;
using ImmichFrame.WebApi.Helpers.Admin;
using ImmichFrame.WebApi.Services;
using ImmichFrame.WebApi.Helpers.Config;
using ImmichFrame.WebApi.Models;
using ImmichFrame.WebApi.Tests.Mocks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Newtonsoft.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Controllers;

/// <summary>
/// The picker the configuration editor reads album, person and tag names through, over the real host
/// with a real settings file - the only way to exercise what the endpoint is actually for: that a
/// saved account is named by the handle the configuration read issued rather than by anything the
/// browser was told, and that an administrator typing a URL and a key into the form gets a usable
/// answer or a usable error, never a 500.
/// </summary>
[TestFixture]
public class AdminImmichControllerTests
{
    private const string AdminSubject = "8ab0c1e2-user";
    private const string OtherSubject = "not-an-admin";
    private const string FrameSecret = "frame-secret";

    private const string SavedHost = "saved-immich.example.com";
    private const string TypedHost = "typed-immich.example.com";
    private const string SavedServer = $"http://{SavedHost}";
    private const string TypedServer = $"http://{TypedHost}";

    private const string AlbumsUrl = "/api/admin/immich/albums";
    private const string PeopleUrl = "/api/admin/immich/people";
    private const string TagsUrl = "/api/admin/immich/tags";

    private const string AlbumId = "11111111-1111-4111-8111-111111111111";
    private const string PersonId = "22222222-2222-4222-8222-222222222222";
    private const string TagId = "33333333-3333-4333-8333-333333333333";

    private static readonly JsonSerializerOptions Camel = new(JsonSerializerDefaults.Web);

    private const string SettingsJson = $$"""
        {
          "General": {
            "Interval": 45,
            "AuthenticationSecret": "{{FrameSecret}}"
          },
          "Accounts": [
            {
              "ImmichServerUrl": "{{SavedServer}}",
              "ApiKey": "saved-api-key"
            }
          ]
        }
        """;

    private const string KitchenHost = "kitchen-immich.example.com";

    /// <summary>
    /// <see cref="SettingsJson"/> plus a profile declaring an account of its own, on a server of its
    /// own - so that an account resolved against the wrong configuration asks a different host, and
    /// the test can tell.
    /// </summary>
    private const string ProfileSettingsJson = $$"""
        {
          "General": {
            "Interval": 45,
            "AuthenticationSecret": "{{FrameSecret}}"
          },
          "Accounts": [
            {
              "ImmichServerUrl": "{{SavedServer}}",
              "ApiKey": "saved-api-key"
            }
          ],
          "Profiles": {
            "kitchen": {
              "Accounts": [
                {
                  "ImmichServerUrl": "http://{{KitchenHost}}",
                  "ApiKey": "kitchen-api-key"
                }
              ]
            }
          }
        }
        """;

    private string _directory = null!;

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"immichframe-admin-immich-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "Settings.json"), SettingsJson);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public async Task Albums_FromASavedAccount_AreTrimmedToWhatAPickerDraws()
    {
        var handler = Immich();
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var response = await Post(client, AlbumsUrl, await SavedAccount(client));
        var body = await response.Content.ReadAsStringAsync();
        var albums = JsonNode.Parse(body)?.AsArray();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((string?)albums?[0]?["id"], Is.EqualTo(AlbumId));
            Assert.That((string?)albums?[0]?["albumName"], Is.EqualTo("Holidays 2024"));
            Assert.That((long?)albums?[0]?["assetCount"], Is.EqualTo(12));

            // Trimmed, not forwarded: these lists get long and none of this reaches the browser.
            Assert.That(body, Does.Not.Contain("albumUsers"));
            Assert.That(body, Does.Not.Contain("description"));
        });
    }

    [TestCase(AlbumsUrl, "/api/albums")]
    [TestCase(PeopleUrl, "/api/people")]
    [TestCase(TagsUrl, "/api/tags")]
    public async Task Lists_FromInlineCredentials_AskTheServerBeingTypedWithTheKeyBeingTyped(
        string url, string immichPath)
    {
        var handler = Immich();
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var response = await Post(client, url, new { serverUrl = TypedServer, apiKey = "typed-api-key" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // The point of the inline path is first-run setup: nothing is saved, so the request has to go
        // to the host in the form, carrying the key in the form - not to the saved account.
        handler.Protected().Verify("SendAsync", Times.Once(),
            ItExpr.Is<HttpRequestMessage>(request =>
                request.RequestUri!.AbsolutePath == immichPath &&
                request.RequestUri.Host == TypedHost &&
                request.Headers.GetValues("X-API-KEY").Single() == "typed-api-key"),
            ItExpr.IsAny<CancellationToken>());

        handler.Protected().Verify("SendAsync", Times.Never(),
            ItExpr.Is<HttpRequestMessage>(request =>
                request.RequestUri!.AbsolutePath == immichPath &&
                request.RequestUri.Host == SavedHost),
            ItExpr.IsAny<CancellationToken>());
    }

    [Test]
    public async Task Tags_CarryTheValueTheAssetPoolMatchesOn_NotTheNameOrTheId()
    {
        var handler = Immich();
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var response = await Post(client, TagsUrl, await SavedAccount(client));
        var tags = JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsArray();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // TagAssetsPool keys every tag by TagResponseDto.Value and looks the configured strings up
            // in that dictionary, so an editor that submitted the name or the id would write a
            // configuration that loads, validates and then selects nothing. The three are deliberately
            // all different here, so a field swapped for either of the others fails this.
            Assert.That((string?)tags?[0]?["value"], Is.EqualTo("trips/summer 2024"));
            Assert.That((string?)tags?[0]?["name"], Is.EqualTo("summer 2024"));
            Assert.That((string?)tags?[0]?["id"], Is.EqualTo(TagId));
        });
    }

    [Test]
    public async Task People_SpanningSeveralPages_AreAllReturned()
    {
        // Two pages: one full, one partial. Immich pages this endpoint and the others it does not, so
        // a picker that read only the first page would hide most of a real library's faces.
        var handler = Immich(peoplePages: 2);
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var response = await Post(client, PeopleUrl, await SavedAccount(client));
        var people = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(people?["people"]?.AsArray().Count, Is.EqualTo(PageSize + 1));
            Assert.That((bool?)people?["truncated"], Is.False);
            Assert.That((long?)people?["total"], Is.EqualTo(PageSize + 1));
        });
    }

    [Test]
    public async Task People_BeyondTheCap_SayThatTheListWasCut()
    {
        // A server that always claims another page: the cap is what stops the read, and the response
        // has to admit it. An administrator who cannot find a person needs to know the list was cut
        // short rather than conclude the person is not in Immich.
        var handler = Immich(peoplePages: int.MaxValue);
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var response = await Post(client, PeopleUrl, await SavedAccount(client));
        var people = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((bool?)people?["truncated"], Is.True);
            Assert.That(people?["people"]?.AsArray().Count, Is.EqualTo(MaxPeople));
        });
    }

    /// <summary>
    /// A key Immich does not know (401), or one without the permission the call needs (403). Either
    /// way it is the administrator's to put right, and either way it is said as exactly that.
    /// </summary>
    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.Forbidden)]
    public async Task Albums_WhenImmichRejectsTheApiKey_SayThatAndNothingElse(HttpStatusCode rejection)
    {
        var handler = Immich(albums: _ => new HttpResponseMessage(rejection)
        {
            Content = new StringContent("""{"message":"Invalid API key","secretDetail":"leak-me"}""")
        });

        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var response = await Post(client, AlbumsUrl, new { serverUrl = TypedServer, apiKey = "wrong-key" });
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            // 400, distinctly: this is the administrator's credential being wrong, not the network.
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem, Does.Contain("rejected that API key"));

            // Immich's own body is not a message ImmichFrame is entitled to render.
            Assert.That(problem, Does.Not.Contain("secretDetail"));
            Assert.That(problem, Does.Not.Contain("Invalid API key"));
        });
    }

    [Test]
    public async Task Albums_WhenTheServerCannotBeReached_SaySoDistinctlyFromARejectedKey()
    {
        var handler = Immich(albums: _ => throw new HttpRequestException("No such host is known."));

        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var response = await Post(client, AlbumsUrl, new { serverUrl = TypedServer, apiKey = "typed-api-key" });
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(problem, Does.Contain("Could not reach"));
            Assert.That(problem, Does.Contain(TypedHost));

            // The two outcomes an administrator hits most must not read the same. A rejected key is a
            // 400 saying so; this one is not allowed to borrow that wording.
            Assert.That(problem, Does.Not.Contain("rejected that API key"));
        });
    }

    /// <summary>
    /// Any other status from the server: reached, and answering, but not the way Immich answers this
    /// call - most often because the URL points at something else. The status is the one part of the
    /// answer worth repeating; the body is the far end's, and is not.
    /// </summary>
    [TestCase(HttpStatusCode.NotFound)]
    [TestCase(HttpStatusCode.InternalServerError)]
    public async Task Albums_WhenTheServerAnswersWithAnotherError_SayWhichStatusAndNothingElse(HttpStatusCode status)
    {
        var handler = Immich(albums: _ => new HttpResponseMessage(status)
        {
            Content = new StringContent("""<html><body>Not Immich<!-- secretDetail --></body></html>""")
        });

        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var response = await Post(client, AlbumsUrl, new { serverUrl = TypedServer, apiKey = "typed-api-key" });
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(problem, Does.Contain($"answered with status {(int)status}"));

            // Neither of the other two outcomes' wording: the key was not rejected, and the server
            // was reached.
            Assert.That(problem, Does.Not.Contain("rejected that API key"));
            Assert.That(problem, Does.Not.Contain("Could not reach"));

            Assert.That(problem, Does.Not.Contain("Not Immich"));
            Assert.That(problem, Does.Not.Contain("secretDetail"));
        });
    }

    [Test]
    public async Task Albums_WithAnUnknownAccountHandle_AreRefusedWithoutAskingAnyImmichServer()
    {
        var handler = Immich();
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var saved = await SavedAccount(client);
        var response = await Post(client, AlbumsUrl,
            new { profile = saved.Profile, version = saved.Version, accountId = "0000000000000000" });

        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem, Does.Contain("not part of the saved configuration"));
        });

        handler.Protected().Verify("SendAsync", Times.Never(),
            ItExpr.Is<HttpRequestMessage>(request => request.RequestUri!.AbsolutePath.EndsWith("/api/albums")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Test]
    public async Task Albums_WithAStaleVersionToken_SayTheConfigurationMoved()
    {
        var handler = Immich();
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var saved = await SavedAccount(client);
        var response = await Post(client, AlbumsUrl,
            new { profile = saved.Profile, version = "sha256:stale", accountId = saved.AccountId });

        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

            // Checked ahead of the handle on purpose: the handle is derived from the version, so a
            // stale one matches nothing, and "no such account" would send an administrator looking for
            // an account they never deleted.
            Assert.That(problem, Does.Contain("changed since the editor loaded it"));
        });
    }

    [Test]
    public async Task Albums_WithANonHttpServerUrl_AreRefusedWithoutAnyOutboundRequest()
    {
        var handler = Immich();
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var response = await Post(client, AlbumsUrl, new { serverUrl = "file:///etc/passwd", apiKey = "typed-api-key" });

        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem, Does.Contain("http:// or https://"));
        });

        handler.Protected().Verify("SendAsync", Times.Never(),
            ItExpr.Is<HttpRequestMessage>(request => request.RequestUri!.Scheme == "file"),
            ItExpr.IsAny<CancellationToken>());
    }

    /// <summary>
    /// A saved account and a typed one in the same request, whole or in part. Refused rather than
    /// resolved by precedence, and before anything is sent anywhere: an editor that sends both is
    /// confused about which account it means, and honouring either half confidently answers about the
    /// wrong server - or, mixed the wrong way, hands the saved account's key to the URL being typed.
    /// </summary>
    [TestCase(TypedServer, "typed-api-key")]
    [TestCase(TypedServer, null)]
    [TestCase(null, "typed-api-key")]
    public async Task Albums_NamingASavedAccountAndATypedOne_AreRefusedWithoutAnyOutboundRequest(
        string? serverUrl, string? apiKey)
    {
        var handler = Immich();
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var saved = await SavedAccount(client);
        var response = await Post(client, AlbumsUrl,
            new { profile = saved.Profile, version = saved.Version, accountId = saved.AccountId, serverUrl, apiKey });
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem, Does.Contain("not both"));
        });

        VerifyNothingSentButTheVersionCheck(handler);
    }

    [Test]
    public async Task Albums_NamingNoAccountAtAll_AreRefusedWithoutAnyOutboundRequest()
    {
        var handler = Immich();
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var response = await Post(client, AlbumsUrl, new { });
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem, Does.Contain("No Immich account was named"));
        });

        VerifyNothingSentButTheVersionCheck(handler);
    }

    /// <summary>
    /// A handle issued under a profile the stored settings do not declare. Refused by name, rather
    /// than resolved against the default configuration in its place - which holds an account at the
    /// same position, and would answer with that account's albums.
    /// </summary>
    [Test]
    public async Task Albums_FromAnUnknownProfile_AreRefusedWithoutAnyOutboundRequest()
    {
        var handler = Immich();
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var saved = await SavedAccount(client);
        var response = await Post(client, AlbumsUrl,
            new { profile = "no-such-profile", version = saved.Version, accountId = saved.AccountId });
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem, Does.Contain("no configuration profile named 'no-such-profile'"));
        });

        VerifyNothingSentButTheVersionCheck(handler);
    }

    /// <summary>
    /// A handle from a profile, sent back under that profile's name in another case. Profile names
    /// are case-insensitive everywhere else - the catalog will not hold two that differ only in case,
    /// and the handle itself is derived from the lower-cased name - so the picker has to agree, rather
    /// than tell an administrator that a profile they can see does not exist.
    /// </summary>
    [Test]
    public async Task Albums_FromAProfileNamedInAnotherCase_AreReadFromThatProfilesOwnAccount()
    {
        File.WriteAllText(Path.Combine(_directory, "Settings.json"), ProfileSettingsJson);

        var handler = Immich();
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var saved = await SavedAccount(client, "kitchen");
        var response = await Post(client, AlbumsUrl, saved with { Profile = "KITCHEN" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());

        // The kitchen's own account, with its own key - not the default configuration's, which sits at
        // the same position in a list of its own.
        handler.Protected().Verify("SendAsync", Times.Once(),
            ItExpr.Is<HttpRequestMessage>(request =>
                request.RequestUri!.AbsolutePath == "/api/albums" &&
                request.RequestUri.Host == KitchenHost &&
                request.Headers.GetValues("X-API-KEY").Single() == "kitchen-api-key"),
            ItExpr.IsAny<CancellationToken>());
    }

    [Test]
    public async Task Albums_FromATypedServerUrlWithoutAKey_AreRefusedWithoutAnyOutboundRequest()
    {
        var handler = Immich();
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var response = await Post(client, AlbumsUrl, new { serverUrl = TypedServer });
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem, Does.Contain("An API key is required"));
        });

        VerifyNothingSentButTheVersionCheck(handler);
    }

    [Test]
    public async Task PersonThumbnail_ForASavedAccount_StreamsTheImageBack()
    {
        var handler = Immich();
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var saved = await SavedAccount(client);
        var response = await Send(client, HttpMethod.Get,
            $"/api/admin/immich/people/{PersonId}/thumbnail" +
            $"?profile={saved.Profile}&version={Uri.EscapeDataString(saved.Version)}&accountId={saved.AccountId}");

        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/jpeg"));
            Assert.That(bytes, Is.EqualTo(ThumbnailBytes));
        });
    }

    [Test]
    public async Task PersonThumbnail_WithAnUnknownAccountHandle_IsARefusalNotAnUnrenderableImage()
    {
        var handler = Immich();
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var saved = await SavedAccount(client);
        var response = await Send(client, HttpMethod.Get,
            $"/api/admin/immich/people/{PersonId}/thumbnail" +
            $"?version={Uri.EscapeDataString(saved.Version)}&accountId=0000000000000000");
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem, Does.Contain("not part of the saved configuration"));
        });
    }

    [Test]
    public async Task PersonThumbnail_LabelsTheBytesItself_AndForbidsSniffing()
    {
        // A compromised or hostile Immich server answering text/html. This response is served from
        // the origin that hosts the admin editor and carries its session cookie, so echoing that
        // label back is script execution on that origin - and without nosniff a browser will reach
        // the same conclusion from the bytes even against an image/* label.
        var handler = Immich(
            thumbnailContentType: "text/html",
            thumbnailBody: System.Text.Encoding.UTF8.GetBytes("<script>alert(document.domain)</script>"));

        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var saved = await SavedAccount(client);
        var response = await Send(client, HttpMethod.Get,
            $"/api/admin/immich/people/{PersonId}/thumbnail" +
            $"?profile={saved.Profile}&version={Uri.EscapeDataString(saved.Version)}&accountId={saved.AccountId}");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/jpeg"));
            Assert.That(response.Headers.TryGetValues("X-Content-Type-Options", out var nosniff)
                ? nosniff.FirstOrDefault()
                : null, Is.EqualTo("nosniff"));
        });
    }

    [Test]
    public async Task PersonThumbnail_KeepsAnImageTypeImmichActuallyProduces()
    {
        // The allowlist has to pass a real Immich response through unchanged, or it is just a
        // hard-coded content type wearing a list.
        var handler = Immich(thumbnailContentType: "image/webp");
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var saved = await SavedAccount(client);
        var response = await Send(client, HttpMethod.Get,
            $"/api/admin/immich/people/{PersonId}/thumbnail" +
            $"?profile={saved.Profile}&version={Uri.EscapeDataString(saved.Version)}&accountId={saved.AccountId}");

        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/webp"));
    }

    [Test]
    public async Task PersonThumbnail_ForAPersonImmichNoLongerHas_IsNotFound()
    {
        // Deleted, or merged into another person, between the picker reading the list and drawing
        // the face. Its own 404, not the 502 an unexpected status otherwise gets: the server is fine.
        var handler = Immich(thumbnailStatus: HttpStatusCode.NotFound);
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var saved = await SavedAccount(client);
        var response = await Send(client, HttpMethod.Get,
            $"/api/admin/immich/people/{PersonId}/thumbnail" +
            $"?profile={saved.Profile}&version={Uri.EscapeDataString(saved.Version)}&accountId={saved.AccountId}");
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(problem, Does.Contain("not on the Immich server any more"));
        });
    }

    /// <summary>
    /// The thumbnail is a GET because an &lt;img src&gt; cannot POST, and so it takes a saved-account
    /// handle and nothing else: a key on a query string is a key in every log the URL passes through.
    /// The action builds its account reference from the handle's own parameters, so a URL naming a
    /// server and a key names no account at all - and nothing is fetched from that server, whose
    /// answer would otherwise be served from the admin editor's own origin.
    /// </summary>
    [Test]
    public async Task PersonThumbnail_ForAServerAndKeyOnTheQueryString_IsRefusedWithoutAskingThatServer()
    {
        const string evilHost = "evil.example.com";

        var handler = Immich();
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var response = await Send(client, HttpMethod.Get,
            $"/api/admin/immich/people/{PersonId}/thumbnail" +
            $"?serverUrl={Uri.EscapeDataString($"http://{evilHost}")}&apiKey=k");
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem, Does.Contain("No Immich account was named"));
        });

        handler.Protected().Verify("SendAsync", Times.Never(),
            ItExpr.Is<HttpRequestMessage>(request => request.RequestUri!.Host == evilHost),
            ItExpr.IsAny<CancellationToken>());
    }

    [Test]
    public async Task People_WhenTheServerClaimsMoreButSendsNobody_StopsAfterOnePage()
    {
        // The unbounded-loop case. An empty page never raises the accumulated count, so a cap
        // counting people alone never fires and the loop asks for page after page forever.
        var handler = Immich(peoplePages: int.MaxValue, peoplePerPage: 0);
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var response = await Post(client, PeopleUrl, await SavedAccount(client));
        var people = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(people?["people"]?.AsArray().Count, Is.Zero);

            // The server said there was more and did not send it, so the list really is short of
            // what it claimed - reporting that as complete would be the lie the cap exists to avoid.
            Assert.That((bool?)people?["truncated"], Is.True);
        });

        handler.Protected().Verify("SendAsync", Times.Once(),
            ItExpr.Is<HttpRequestMessage>(request => request.RequestUri!.AbsolutePath == "/api/people"),
            ItExpr.IsAny<CancellationToken>());
    }

    [Test]
    public async Task People_FromAServerThatDripsOnePersonAPage_StopAtThePageCap()
    {
        // The same unbounded loop wearing a subtler costume: every page carries somebody, so the
        // count does rise - just never to MaxPeople. The page cap is the bound that fires here.
        var handler = Immich(peoplePages: int.MaxValue, peoplePerPage: 1);
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var response = await Post(client, PeopleUrl, await SavedAccount(client));
        var people = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(people?["people"]?.AsArray().Count, Is.EqualTo(MaxPeoplePages));
            Assert.That((bool?)people?["truncated"], Is.True);
        });

        handler.Protected().Verify("SendAsync", Times.Exactly(MaxPeoplePages),
            ItExpr.Is<HttpRequestMessage>(request => request.RequestUri!.AbsolutePath == "/api/people"),
            ItExpr.IsAny<CancellationToken>());
    }

    [Test]
    public async Task People_AreAskedForWithoutTheHiddenOnes()
    {
        var handler = Immich();
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var response = await Post(client, PeopleUrl, await SavedAccount(client));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Immich hides a face cluster precisely so it stops being offered; a picker that listed them
        // anyway would undo that, and the only place the decision is visible is the outbound query.
        handler.Protected().Verify("SendAsync", Times.Once(),
            ItExpr.Is<HttpRequestMessage>(request =>
                request.RequestUri!.AbsolutePath == "/api/people" &&
                request.RequestUri.Query.Contains("withHidden=false")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Test]
    public async Task ApiKey_PastedWithATrailingNewline_Works()
    {
        // A key copied out of a terminal or read from a file carries the newline that came with it,
        // and HttpHeaders.Add throws FormatException on one - outside every catch in the controller,
        // so this was a 500 on exactly the first-run path the inline credentials exist to serve.
        var handler = Immich();
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var response = await Post(client, AlbumsUrl, new { serverUrl = TypedServer, apiKey = "typed-api-key\n" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        handler.Protected().Verify("SendAsync", Times.Once(),
            ItExpr.Is<HttpRequestMessage>(request =>
                request.RequestUri!.AbsolutePath == "/api/albums" &&
                request.Headers.GetValues("X-API-KEY").Single() == "typed-api-key"),
            ItExpr.IsAny<CancellationToken>());
    }

    [Test]
    public async Task ApiKey_CarryingACharacterAHeaderCannot_IsARefusalNotA500()
    {
        var handler = Immich();
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var response = await Post(client, AlbumsUrl, new { serverUrl = TypedServer, apiKey = "typed\u0001key" });
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem, Does.Contain("an HTTP header cannot carry"));

            // The remedy that fits a key being typed, and not the one written for a key already in
            // the settings file - the two share everything up to this sentence.
            Assert.That(problem, Does.Contain("Copy it again from Immich"));
            Assert.That(problem, Does.Not.Contain("Fix it in the settings file"));
        });

        handler.Protected().Verify("SendAsync", Times.Never(),
            ItExpr.Is<HttpRequestMessage>(request => request.RequestUri!.AbsolutePath == "/api/albums"),
            ItExpr.IsAny<CancellationToken>());
    }

    [Test]
    public async Task Albums_WithNoVersionAtAll_AreRefusedRatherThanSkippingTheStalenessCheck()
    {
        var handler = Immich();
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var saved = await SavedAccount(client);
        var response = await Post(client, AlbumsUrl, new { profile = saved.Profile, accountId = saved.AccountId });
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem, Does.Contain("which version of the configuration"));
        });

        handler.Protected().Verify("SendAsync", Times.Never(),
            ItExpr.Is<HttpRequestMessage>(request => request.RequestUri!.AbsolutePath == "/api/albums"),
            ItExpr.IsAny<CancellationToken>());
    }

    [Test]
    public async Task Albums_WhenTheStoreHasMovedUnderTheRunningConfiguration_AreRefusedRatherThanServedFromTheWrongServer()
    {
        var handler = Immich();
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        // The running configuration read now, before the store moves - as a running process's was,
        // long before a second one wrote to its database. Left to whatever first asked for it, that was
        // the startup version check, on a thread of its own: when the check came second, the catalog
        // was seeded from the row already moved, the two agreed after all, and there was nothing left
        // for the picker to refuse.
        _ = factory.Services.GetRequiredService<IConfigCatalog>().Default;

        // Written straight to the store rather than through the editor, because going through the
        // editor swaps the catalog in the same breath and the two could never disagree. This is the
        // divergence that is actually left once the settings file stopped being read: a second
        // process against the same database file, which writes the row this process then reads while
        // its own catalog still holds what it booted with. The position resolves, and resolves to a
        // different server.
        var store = factory.Services.GetRequiredService<SettingsService>();
        var current = store.Read();
        store.Save(current.Text.Replace(SavedHost, "moved-immich.example.com"), current.Format, current.Version);

        var saved = await SavedAccount(client);
        var response = await Post(client, AlbumsUrl, saved);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            // Not a stale-version refusal: the handle is from a read of the store as it is now.
            Assert.That(problem, Does.Not.Contain("changed since the editor loaded it"));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem, Does.Contain("no longer matches the stored settings"));
        });

        // The whole point: the editor is showing moved-immich, so listing saved-immich's albums would
        // have the administrator save those GUIDs onto an account that has never seen them.
        handler.Protected().Verify("SendAsync", Times.Never(),
            ItExpr.Is<HttpRequestMessage>(request => request.RequestUri!.AbsolutePath == "/api/albums"),
            ItExpr.IsAny<CancellationToken>());
    }

    [Test]
    public async Task Albums_FromAServerAnnouncingMoreThanTheCap_SayThatRatherThanBlamingTheNetwork()
    {
        var handler = Immich(albums: _ =>
        {
            var response = Json("[]");
            response.Content.Headers.ContentLength = AdminImmichAccounts.MaxResponseBytes + 1;

            return response;
        });

        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var response = await Post(client, AlbumsUrl, new { serverUrl = TypedServer, apiKey = "typed-api-key" });
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(problem, Does.Contain("more data than ImmichFrame will read"));

            // The server was reached and it answered. "Could not reach" would send an administrator
            // to check DNS for a host that is up.
            Assert.That(problem, Does.Not.Contain("Could not reach"));
        });
    }

    /// <summary>
    /// The headers arrive and the body never finishes. The picker's client timeout has to end that,
    /// and end it as the ordinary "could not reach" answer an administrator can act on - not a 500,
    /// and not a request still open long after they have given up waiting for it.
    /// <para>
    /// Run with the picker client's timeout shortened to a second, so this waits on nothing like
    /// <see cref="AdminImmichAccounts.RequestTimeout"/>. The stalled body gives up after ten seconds of
    /// its own, so that a picker that ignores the timeout fails this test rather than hanging it.
    /// </para>
    /// </summary>
    [Test]
    public async Task Albums_FromAServerWhoseBodyStalls_EndWithTheTimeoutAsUnreachable()
    {
        var handler = Immich(albums: _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream(TimeSpan.FromSeconds(10)))
        });

        using var factory = CreateFactory(handler, pickerTimeout: TimeSpan.FromSeconds(1));
        var client = factory.CreateClient();

        var posting = Post(client, AlbumsUrl, new { serverUrl = TypedServer, apiKey = "typed-api-key" });

        Assert.That(await Task.WhenAny(posting, Task.Delay(TimeSpan.FromSeconds(5))), Is.SameAs(posting),
            "the picker was still waiting on a stalled body four seconds after its timeout");

        var response = await posting;
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(problem, Does.Contain("Could not reach"));
            Assert.That(problem, Does.Contain(TypedHost));
        });
    }

    [Test]
    public void PickerHttpClient_IsWiredToRefuseRedirects()
    {
        // Read off the application's own registrations rather than off the factory method, because
        // the factory method being correct is not the property that matters: deleting the
        // ConfigurePrimaryHttpMessageHandler call in Program.cs leaves CreatePrimaryHandler intact
        // and every other test green, while the picker starts following a redirect - handing the
        // request, and the X-API-KEY header on it, to a host the administrator never named.
        //
        // Each configuring action is run against its own probe rather than all of them in sequence,
        // because this test host replaces every primary handler with a mock (ImmichApiMock) and the
        // mock is registered last. The question asked is therefore "does the application install a
        // non-redirecting handler on this client", which is exactly the wiring being pinned.
        var handler = Immich();
        using var factory = CreateFactory(handler);

        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(AdminImmichAccounts.HttpClientName);

        var refusesRedirects = options.HttpMessageHandlerBuilderActions.Any(configure =>
        {
            var probe = new ProbeHandlerBuilder();
            configure(probe);

            return probe.PrimaryHandler is HttpClientHandler { AllowAutoRedirect: false };
        });

        Assert.That(refusesRedirects, Is.True,
            $"no registration on '{AdminImmichAccounts.HttpClientName}' installs a non-redirecting primary handler");
    }

    [Test]
    public void PickerHttpClient_IsWiredWithTheRequestTimeout()
    {
        // Read off a client the application's own registrations build, as the redirect refusal above
        // is. The one test that waits on a stalled server shortens this timeout to keep the suite fast,
        // so it goes on passing with Program.cs no longer setting it - and the picker waiting out
        // HttpClient's default hundred seconds on every server that takes the connection and then
        // says nothing.
        var handler = Immich();
        using var factory = CreateFactory(handler);

        var client = factory.Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(AdminImmichAccounts.HttpClientName);

        Assert.That(client.Timeout, Is.EqualTo(AdminImmichAccounts.RequestTimeout));
    }

    [Test]
    public async Task ApiKey_StoredWithACharacterAHeaderCannot_PointsAtTheStoredSettingsRatherThanTheForm()
    {
        // In the settings the process starts from, which is the only way such a key reaches a running
        // catalog now that validation refuses a save holding one: stored settings that fail validation
        // are run with anyway, so that the editor which fixes them is still served.
        File.WriteAllText(Path.Combine(_directory, "Settings.json"),
            SettingsJson.Replace("saved-api-key", @"stored\u0001key"));

        var handler = Immich();
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var response = await Post(client, AlbumsUrl, await SavedAccount(client));
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

            // The distinguishing half. An administrator looking at a key that is already in the
            // settings file is not looking at a form, so "copy it again from Immich" is advice about
            // somewhere they are not.
            Assert.That(problem, Does.Contain("Fix it in the admin editor"));
            Assert.That(problem, Does.Not.Contain("Copy it again from Immich"));
        });
    }

    [Test]
    public async Task AdminImmich_IsBehindTheAllowlist_NotJustAuthentication()
    {
        var handler = Immich();
        using var factory = CreateFactory(handler);
        var client = factory.CreateClient();

        var inline = new { serverUrl = TypedServer, apiKey = "typed-api-key" };

        var anonymous = await client.PostAsync(AlbumsUrl, JsonContent.Create(inline, options: Camel));
        var signedInButNotAnAdmin = await Post(client, AlbumsUrl, inline, OtherSubject);

        // ImmichFrameAuthenticationHandler authenticates any holder of AuthenticationSecret, so the
        // frame's own bearer token is a valid credential for the default scheme. That is exactly what
        // makes a bare [Authorize] on an admin path a hole rather than a guard, so it is pinned here.
        using var withFrameSecret = new HttpRequestMessage(HttpMethod.Post, AlbumsUrl)
        {
            Content = JsonContent.Create(inline, options: Camel)
        };
        withFrameSecret.Headers.Authorization = new AuthenticationHeaderValue("Bearer", FrameSecret);
        var frameToken = await client.SendAsync(withFrameSecret);

        var thumbnailUrl = $"/api/admin/immich/people/{PersonId}/thumbnail?accountId=whatever";
        var anonymousThumbnail = await client.GetAsync(thumbnailUrl);

        Assert.Multiple(() =>
        {
            Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(signedInButNotAnAdmin.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(frameToken.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(anonymousThumbnail.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        });

        handler.Protected().Verify("SendAsync", Times.Never(),
            ItExpr.Is<HttpRequestMessage>(request => request.RequestUri!.AbsolutePath.EndsWith("/api/albums")),
            ItExpr.IsAny<CancellationToken>());
    }

    // The controller's own page size and cap. Duplicated rather than exposed: a test that read them
    // off the controller would pass whatever they were changed to, which is the opposite of pinning
    // that a library larger than the cap is reported as truncated.
    private const int PageSize = 500;
    private const int MaxPeople = 5000;
    private const int MaxPeoplePages = MaxPeople / PageSize + 1;

    private static readonly byte[] ThumbnailBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02, 0x03];

    /// <summary>What the editor holds after a configuration read, and sends back to name an account.</summary>
    private record SavedAccountRef(string Profile, string Version, string AccountId);

    private static async Task<SavedAccountRef> SavedAccount(
        HttpClient client, string profile = ConfigCatalog.DefaultProfileName)
    {
        var response = await Send(client, HttpMethod.Get, "/api/admin/config");
        response.EnsureSuccessStatusCode();

        var config = (await response.Content.ReadFromJsonAsync<AdminConfigDto>(Camel))!;
        var entry = profile == ConfigCatalog.DefaultProfileName
            ? config.Default
            : config.Profiles.Single(candidate => candidate.Name == profile);

        return new SavedAccountRef(profile, config.Version, entry.Accounts[0].Id!);
    }

    /// <summary>
    /// Somewhere for a registration to write a primary handler so the test can see what it wrote.
    /// It starts at the framework's own default, which does follow redirects, so an action that
    /// leaves it alone cannot be mistaken for one that refuses them.
    /// </summary>
    private sealed class ProbeHandlerBuilder : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; }

        public override HttpMessageHandler PrimaryHandler { get; set; } = new HttpClientHandler();

        public override IList<DelegatingHandler> AdditionalHandlers { get; } = [];

        public override HttpMessageHandler Build() => PrimaryHandler;
    }

    private static Task<HttpResponseMessage> Post(
        HttpClient client, string url, object body, string subject = AdminSubject)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: Camel) };
        request.Headers.Add(TestAdminAuthHandler.SubjectHeader, subject);

        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Send(
        HttpClient client, HttpMethod method, string url, string subject = AdminSubject)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAdminAuthHandler.SubjectHeader, subject);

        return client.SendAsync(request);
    }

    /// <summary>
    /// That a refused request reached no Immich server at all - neither the saved one nor any it
    /// named. The one request allowed through is the version check every host makes of its saved
    /// accounts as it starts, which no request under test can be the cause of.
    /// </summary>
    private static void VerifyNothingSentButTheVersionCheck(Mock<HttpMessageHandler> handler) =>
        handler.Protected().Verify("SendAsync", Times.Never(),
            ItExpr.Is<HttpRequestMessage>(request => !request.RequestUri!.AbsolutePath.EndsWith("/server/version")),
            ItExpr.IsAny<CancellationToken>());

    /// <summary>
    /// A stand-in Immich server, answering the four endpoints the picker uses plus the startup
    /// version check. The album handler is injectable because the two failure paths - a rejected key
    /// and a host that cannot be reached - are the whole point of two of these tests.
    /// <para>
    /// The bodies are the generated response types serialized by the same library that reads them
    /// back, rather than hand-written JSON. The Immich schema marks a good deal of each response
    /// required-but-nullable, so a hand-written fixture missing one field fails to deserialize and
    /// arrives here as "the server answered 200" - a fixture bug wearing the costume of the very
    /// failure path these tests are checking.
    /// </para>
    /// </summary>
    private static Mock<HttpMessageHandler> Immich(
        Func<HttpRequestMessage, HttpResponseMessage>? albums = null,
        int peoplePages = 1,
        int peoplePerPage = PageSize,
        string thumbnailContentType = "image/jpeg",
        byte[]? thumbnailBody = null,
        HttpStatusCode thumbnailStatus = HttpStatusCode.OK)
    {
        var handler = new Mock<HttpMessageHandler>().WithServerVersion();

        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(request => request.RequestUri!.AbsolutePath.EndsWith("/api/albums")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
                albums is null ? Json(AlbumsBody) : albums(request));

        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(request => request.RequestUri!.AbsolutePath.EndsWith("/api/tags")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => Json(TagsBody));

        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(request => request.RequestUri!.AbsolutePath.EndsWith("/api/people")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
                Json(PeoplePage(request, peoplePages, peoplePerPage)));

        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(request => request.RequestUri!.AbsolutePath.EndsWith("/thumbnail")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var response = new HttpResponseMessage(thumbnailStatus)
                {
                    Content = new ByteArrayContent(thumbnailBody ?? ThumbnailBytes)
                };
                response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(thumbnailContentType);

                return response;
            });

        return handler;
    }

    /// <summary>
    /// One page of people. Every page but the last is full and claims another; the last carries a
    /// single person and does not - so a reader that stopped after the first page returns a
    /// different count from one that paged to the end.
    /// </summary>
    private static string PeoplePage(HttpRequestMessage request, int pages, int perPage)
    {
        var page = int.Parse(QueryHelpers.ParseQuery(request.RequestUri!.Query)["page"].FirstOrDefault() ?? "1");
        var last = page >= pages;
        var size = last ? 1 : perPage;

        return JsonConvert.SerializeObject(new PeopleResponseDto
        {
            HasNextPage = !last,
            Hidden = 0,
            // A server claiming an endless supply cannot state a real total, so the finite case is
            // the one the test reads.
            Total = pages == int.MaxValue ? long.MaxValue : (long)(pages - 1) * PageSize + 1,
            People = [.. Enumerable.Range(0, size).Select(index => new PersonResponseDto
            {
                Id = Guid.NewGuid(),
                // Empty on purpose: most people in a real Immich library are unlabelled face
                // clusters, which is what the picker has to render sensibly.
                Name = string.Empty,
                ThumbnailPath = "/thumb",
                IsHidden = false
            })]
        });
    }

    private static readonly string AlbumsBody = JsonConvert.SerializeObject(new[]
    {
        new AlbumResponseDto
        {
            Id = Guid.Parse(AlbumId),
            AlbumName = "Holidays 2024",
            AssetCount = 12,
            Description = "a description no picker needs",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch
        }
    });

    private static readonly string TagsBody = JsonConvert.SerializeObject(new[]
    {
        new TagResponseDto
        {
            Id = Guid.Parse(TagId),
            // Immich's own wording: value is the tag's full path, name its last segment. Both are
            // carried so that a swapped field cannot pass the test that pins which one is submitted.
            Value = "trips/summer 2024",
            Name = "summer 2024",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch
        }
    });

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    /// <summary>
    /// A body that never sends a byte. It waits on the token it is read with, the way a socket read
    /// does, and gives up on its own after <paramref name="_giveUpAfter"/> so that a reader which
    /// passes no token ends rather than hangs.
    /// </summary>
    private sealed class StallingStream(TimeSpan _giveUpAfter) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(_giveUpAfter, cancellationToken);

            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

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

    private WebApplicationFactory<Program> CreateFactory(Mock<HttpMessageHandler> handler, TimeSpan? pickerTimeout = null)
    {
        var directory = _directory;

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.UseMockHandler(handler);
                    services.AddSingleton(new ConfigLocation(directory));

                    if (pickerTimeout is { } timeout)
                    {
                        // Appended after Program's own client action, so it is the one that sticks.
                        services.Configure<HttpClientFactoryOptions>(AdminImmichAccounts.HttpClientName,
                            options => options.HttpClientActions.Add(client => client.Timeout = timeout));
                    }

                    services.AddSingleton(new AdminOidcOptions
                    {
                        Authority = "https://idp.example.com",
                        ClientId = "immichframe",
                        ClientSecret = "client-secret",
                        Admins = [AdminSubject]
                    });

                    services.AddAuthentication()
                        .AddScheme<AuthenticationSchemeOptions, TestAdminAuthHandler>(
                            TestAdminAuthHandler.SchemeName, _ => { });
                    services.Configure<CookieAuthenticationOptions>(
                        AdminAuthentication.CookieScheme,
                        options => options.ForwardAuthenticate = TestAdminAuthHandler.SchemeName);
                });
            });
    }
}
