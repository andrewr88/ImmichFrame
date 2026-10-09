using ImmichFrame.Core.Helpers;
using NUnit.Framework;

namespace ImmichFrame.Core.Tests.Helpers;

/// <summary>
/// <c>SanitizeString</c> is the guard every untrusted string passes through on its way into a log
/// line. It has two jobs, and used to do neither well: it deleted ordinary punctuation, so what the
/// log showed was not what was sent, while letting through control characters other than CR and LF.
/// </summary>
[TestFixture]
public class ImmichFrameExtensionMethodsTests
{
    /// <summary>
    /// What an operator reads has to be what was sent. A denied OIDC subject lost its dashes or its
    /// '@', so copying it from the log into IMMICHFRAME_OIDC_ADMINS still did not let the
    /// administrator in - and a host lost its port, a URL its scheme.
    /// </summary>
    [TestCase("immich.local:2283", TestName = "SanitizeString_HostAndPort_IsKeptAsSent")]
    [TestCase("https://immich.example.com/api/assets?size=preview&key=a+b", TestName = "SanitizeString_Url_IsKeptAsSent")]
    [TestCase("8ab0c1e2-4f6d-4b1a-9c3e-2d5f7a9b1c4e", TestName = "SanitizeString_Guid_IsKeptAsSent")]
    [TestCase("Frame_Kitchen", TestName = "SanitizeString_Underscore_IsKeptAsSent")]
    [TestCase("admin@example.com", TestName = "SanitizeString_EmailAddress_IsKeptAsSent")]
    [TestCase("'quoted' (and \"double\") it’s [1] {2} <3> #4 !5 ~6 |7 `8 *9 ^10 $11 ;12 ,13 \\14", TestName = "SanitizeString_OtherPunctuation_IsKeptAsSent")]
    [TestCase("Küche \U0001F4F7", TestName = "SanitizeString_LettersAndEmojiBeyondAscii_AreKeptAsSent")]
    public void SanitizeString_PrintableCharacters_AreKeptAsSent(string input)
    {
        // Act
        var sanitized = input.SanitizeString();

        // Assert
        Assert.That(sanitized, Is.EqualTo(input));
    }

    /// <summary>
    /// Every character that ends a log line, or that a terminal reads as the start of a command, is
    /// replaced. The console's single-line format only replaces the platform's newline, so CR alone,
    /// NEL and the two Unicode separators broke a record just the same; and ESC - alone or as the C1
    /// CSI - lets the sender drive the operator's terminal: ESC c resets it, wiping what was on screen.
    /// </summary>
    [TestCase("\r", TestName = "SanitizeString_CarriageReturn_IsReplacedWithASpace")]
    [TestCase("\n", TestName = "SanitizeString_LineFeed_IsReplacedWithASpace")]
    [TestCase("\t", TestName = "SanitizeString_Tab_IsReplacedWithASpace")]
    [TestCase("\0", TestName = "SanitizeString_Nul_IsReplacedWithASpace")]
    [TestCase("\u001B", TestName = "SanitizeString_Escape_IsReplacedWithASpace")]
    [TestCase("\u009B", TestName = "SanitizeString_ControlSequenceIntroducer_IsReplacedWithASpace")]
    [TestCase("\u0085", TestName = "SanitizeString_NextLine_IsReplacedWithASpace")]
    [TestCase("\u2028", TestName = "SanitizeString_LineSeparator_IsReplacedWithASpace")]
    [TestCase("\u2029", TestName = "SanitizeString_ParagraphSeparator_IsReplacedWithASpace")]
    public void SanitizeString_ControlOrSeparatorCharacter_IsReplacedWithASpace(string character)
    {
        // Act
        var sanitized = $"frame{character}forged".SanitizeString();

        // Assert
        Assert.That(sanitized, Is.EqualTo("frame forged"));
    }

    /// <summary>
    /// Format characters are invisible, and change how a line reads without being seen. A
    /// right-to-left override shows the rest of the line backwards, so a record can be forged over
    /// a real one, and a zero-width character makes a name nobody can tell from the real one - or
    /// copy out of the log and have match. Characters beyond the Basic Multilingual Plane are two
    /// chars, and a tag character is one of those.
    /// </summary>
    [TestCase("a\u202Eb\u2066c\u2069", "a b c", TestName = "SanitizeString_BidirectionalControls_AreReplacedWithSpaces")]
    [TestCase("kitchen\u200Bframe", "kitchen frame", TestName = "SanitizeString_ZeroWidthSpace_IsReplacedWithASpace")]
    [TestCase("kitchen\U000E006Bframe", "kitchen frame", TestName = "SanitizeString_TagCharacter_IsReplacedWithASpace")]
    public void SanitizeString_FormatCharacters_AreReplacedWithSpaces(string input, string expected)
    {
        // Act
        var sanitized = input.SanitizeString();

        // Assert
        Assert.That(sanitized, Is.EqualTo(expected));
    }

    /// <summary>
    /// Trimmed after the replacement, so nothing the replacement leaves at either end is kept: an
    /// escape sequence is broken up wherever it sits, rather than left at the start of the line.
    /// </summary>
    [Test]
    public void SanitizeString_ControlCharactersAndWhitespaceAtTheEnds_AreTrimmedAway()
    {
        // Act
        var sanitized = " \u001Bc kitchen-frame:1\r\n".SanitizeString();

        // Assert
        Assert.That(sanitized, Is.EqualTo("c kitchen-frame:1"));
    }

    [TestCase("", TestName = "SanitizeString_Empty_IsEmpty")]
    [TestCase("   ", TestName = "SanitizeString_Spaces_AreEmpty")]
    [TestCase(" \t\r\n\u2028 ", TestName = "SanitizeString_AnyWhitespace_IsEmpty")]
    public void SanitizeString_WhitespaceOnly_IsEmpty(string input)
    {
        // Act
        var sanitized = input.SanitizeString();

        // Assert
        Assert.That(sanitized, Is.Empty);
    }

    /// <summary>
    /// The webhook's client identifier keeps the transform the log sanitiser used to apply, exactly:
    /// it is what every existing automation has been matching against. These are the values
    /// <c>SanitizeString</c> returned before it stopped stripping punctuation.
    /// </summary>
    [TestCase("8ab0c1e2-4f6d-4b1a-9c3e-2d5f7a9b1c4e", "8ab0c1e24f6d4b1a9c3e2d5f7a9b1c4e",
        TestName = "ToLegacyWebhookClientIdentifier_Guid_LosesItsDashesAsItAlwaysHas")]
    [TestCase("Frame_Kitchen", "FrameKitchen",
        TestName = "ToLegacyWebhookClientIdentifier_Underscore_IsStrippedAsItAlwaysHas")]
    [TestCase(" kitchen@home:1 (left)\r\n", "kitchenhome1 left",
        TestName = "ToLegacyWebhookClientIdentifier_OtherPunctuationAndLineBreaks_AreStrippedAsTheyAlwaysHave")]
    public void ToLegacyWebhookClientIdentifier_ReproducesTheOldSanitizer(string input, string expected)
    {
        // Act
        var identifier = input.ToLegacyWebhookClientIdentifier();

        // Assert
        Assert.That(identifier, Is.EqualTo(expected));
    }
}
