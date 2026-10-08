using System.Globalization;

namespace ImmichFrame.Core.Helpers;

public static class ImmichFrameExtensionMethods
{
    /// <summary>
    /// Makes an untrusted string safe to write into a log line: every printable character is kept as
    /// it was sent, and every character that could end the line or drive the terminal showing it is
    /// replaced with a space. Trimmed, and empty for a string of nothing but whitespace.
    /// <para>
    /// The console's single-line format only rewrites <see cref="Environment.NewLine"/>, so a lone CR,
    /// NEL (U+0085) or a Unicode line or paragraph separator still started a forged record, and ESC -
    /// or the C1 CSI (U+009B), its one-character form - let the sender run terminal commands on
    /// whoever read the log; ESC c resets the screen. So every control character goes, C0 and C1
    /// alike, and the two separators with them.
    /// </para>
    /// <para>
    /// A space, rather than deleting the character or putting an underscore in its place: most of
    /// these are breaks between words - tab, the line breaks - and the commonest legitimate one is an
    /// exception message that runs over two lines, which stays readable on one. Deleting fuses the
    /// words either side, and an underscore - a character kept here, as in Frame_Kitchen - would make
    /// them read as one identifier.
    /// </para>
    /// <para>
    /// Punctuation is not touched; nothing printable can break a log line. This used to delete it, so a
    /// denied OIDC subject was logged without its dashes or its '@' - copied into
    /// IMMICHFRAME_OIDC_ADMINS, it still did not match - and a host without its port. For the log only:
    /// this is not HTML, URL or shell escaping, and the webhook's client identifier keeps a transform
    /// of its own, <see cref="ToLegacyWebhookClientIdentifier"/>.
    /// </para>
    /// </summary>
    public static string SanitizeString(this string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var characters = input.ToCharArray();

        for (var i = 0; i < characters.Length; i++)
        {
            if (IsUnsafeInALogLine(characters[i]))
            {
                characters[i] = ' ';
            }
        }

        // Trimmed after the replacement, so a space it leaves at either end goes with the rest.
        return new string(characters).Trim();
    }

    /// <summary>
    /// The client identifier as the webhook's <c>AssetRequestedNotification</c> payload carries it,
    /// and for that payload alone: what <see cref="SanitizeString"/> returned before it stopped
    /// deleting punctuation. Trimmed, then stripped of a fixed set of punctuation and of CR and LF -
    /// so 'Frame_Kitchen' arrives as 'FrameKitchen', and a GUID without its dashes.
    /// <para>
    /// Kept, unchanged, for compatibility with existing automations. The identifier is how one tells
    /// one frame from another, so changing what it is sent would silently break every automation
    /// matching on the old form - even though the webhook documentation
    /// (docs/docs/getting-started/configuration.md, its Events table) shows the payload carrying
    /// 'Frame_Kitchen' as it was set. Which of the two is right is a decision to announce in a release,
    /// not one for a change to how strings are logged.
    /// </para>
    /// <para>
    /// Not a log sanitiser: every control character but CR and LF passes through. That is no concern
    /// in a JSON payload, which escapes them, but log with <see cref="SanitizeString"/>.
    /// </para>
    /// </summary>
    public static string ToLegacyWebhookClientIdentifier(this string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        string removeChars = "'\"?&^$#@!()+-,:;<>’\'-_*/\\|`~{}[]";

        input = input.Trim();

        foreach (char c in removeChars)
        {
            input = input.Replace(c.ToString(), string.Empty);
        }

        input = input.Replace(Environment.NewLine, string.Empty);
        input = input.Replace("\r", string.Empty);
        input = input.Replace("\n", string.Empty);

        return input;
    }

    // Control characters, C0 and C1 alike - CR, LF, tab, NUL, ESC, NEL and CSI among them - and the
    // two separators Unicode ends a line or a paragraph with, which are not control characters.
    private static bool IsUnsafeInALogLine(char character) =>
        char.IsControl(character) ||
        char.GetUnicodeCategory(character) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
}
