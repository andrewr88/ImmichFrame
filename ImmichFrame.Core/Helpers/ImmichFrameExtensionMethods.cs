using System.Globalization;
using System.Text;

namespace ImmichFrame.Core.Helpers;

public static class ImmichFrameExtensionMethods
{
    /// <summary>
    /// Makes an untrusted string safe to write into a log line: every printable character is kept as
    /// it was sent, and every character that could end the line, drive the terminal showing it or
    /// change how it reads without being seen is replaced with a space. Trimmed, and empty for a
    /// string of nothing but whitespace.
    /// <para>
    /// The console's single-line format only rewrites <see cref="Environment.NewLine"/>, so a lone CR,
    /// NEL (U+0085) or a Unicode line or paragraph separator still started a forged record, and ESC -
    /// or the C1 CSI (U+009B), its one-character form - let the sender run terminal commands on
    /// whoever read the log; ESC c resets the screen. So every control character goes, C0 and C1
    /// alike, and the two separators with them.
    /// </para>
    /// <para>
    /// So does every format character, for being invisible. A right-to-left override (U+202E) shows the
    /// rest of the line backwards, so a forged record can be laid over a real one, and a zero-width
    /// space (U+200B) makes a name nobody can tell from the real one - or copy out of the log and have
    /// match. Judged by code point, not by char: a tag character (U+E0000 on) is two chars, a
    /// surrogate pair, and neither half is a format character on its own.
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

        var sanitized = new StringBuilder(input.Length);

        for (var i = 0; i < input.Length; i++)
        {
            // A character beyond the Basic Multilingual Plane is a surrogate pair, kept or replaced
            // whole. A lone surrogate is a char of its own, and kept as it always was.
            var length = char.IsSurrogatePair(input, i) ? 2 : 1;

            if (IsUnsafeInALogLine(input, i))
            {
                sanitized.Append(' ');
            }
            else
            {
                sanitized.Append(input, i, length);
            }

            i += length - 1;
        }

        // Trimmed after the replacement, so a space it leaves at either end goes with the rest.
        return sanitized.ToString().Trim();
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

    // Control characters, C0 and C1 alike - CR, LF, tab, NUL, ESC, NEL and CSI among them - the two
    // separators Unicode ends a line or a paragraph with, which are not control characters, and
    // format characters: the bidirectional controls, the zero-width characters and the tags among
    // them. Of the character at index, a surrogate pair's whole code point when one starts there.
    private static bool IsUnsafeInALogLine(string input, int index) =>
        char.IsControl(input, index) ||
        char.GetUnicodeCategory(input, index) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.Format;
}
