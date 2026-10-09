using ImmichFrame.WebApi.Helpers;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Helpers;

/// <summary>
/// <see cref="UpstreamContentType.Allowlisted"/> on its own, for declarations the Immich mocked in the
/// controller tests never makes. What each caller serves an upstream's type as, through a real host,
/// is that caller's own tests' business.
/// </summary>
[TestFixture]
public class UpstreamContentTypeTests
{
    private const string Fallback = "application/octet-stream";

    /// <summary>
    /// Whitespace around the media type is no part of it, whichever side it is on: the grammar allows
    /// some before the semicolon that starts the parameters (RFC 9110, 8.3.1), and a caller may hand
    /// over a header value no parser has been through. Either way it is still the allowlisted type,
    /// served in the allowlist's own spelling. An absent Content-Type names no type at all, so it is
    /// the fallback.
    /// </summary>
    [TestCase("image/jpeg ; x=y", "image/jpeg")]
    [TestCase(" IMAGE/JPEG", "image/jpeg")]
    [TestCase(null, Fallback)]
    public void Allowlisted_DeclaredTypePaddedOrAbsent_IsTheAllowlistsEntryOrTheFallback(string? declared, string served)
    {
        // Act
        var contentType = UpstreamContentType.Allowlisted(declared, ["image/jpeg"], Fallback);

        // Assert
        Assert.That(contentType, Is.EqualTo(served));
    }
}
