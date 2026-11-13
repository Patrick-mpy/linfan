// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Globalization;
using System.Text;

namespace LinFan.Core.Services;

/// <summary>
/// Handles text that came from an IPC peer or a hand-edited file before it reaches a log, a terminal or another
/// user's GUI. Control and format characters in such text could end a log record and forge the next one (LF, CR,
/// NUL - journald also splits records at NUL), steer a terminal (ESC/CSI/OSC, BEL) or hide or reorder what is
/// displayed (bidi overrides, invisible tag characters). Labels have them stripped (<see cref="Strip"/>), ids that
/// contain them are rejected (<see cref="IsClean"/>), and sinks print what is left through <see cref="Escape"/>.
/// </summary>
public static class UntrustedText
{
    // Zero-width joiner: part of emoji sequences, renders nothing on its own and cannot reorder or break a line.
    private const int ZeroWidthJoiner = 0x200D;

    /// <summary><c>true</c> if <paramref name="text"/> is <c>null</c> or holds no unsafe character.</summary>
    public static bool IsClean(string? text) => text is null || IndexOfUnsafe(text) < 0;

    /// <summary>Removes every unsafe character; returns the same instance when there is none.</summary>
    public static string Strip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return IndexOfUnsafe(text) < 0 ? text : Rewrite(text, (_, _) => { });
    }

    /// <summary>
    /// Replaces every unsafe character with a visible, inert <c>\uXXXX</c> (<c>\U00XXXXXX</c> beyond the BMP; a line
    /// feed included), so a sink writes the text as data on a single line. <c>null</c> becomes an empty string.
    /// </summary>
    public static string Escape(string? text)
    {
        if (text is null)
            return "";
        return IndexOfUnsafe(text) < 0
            ? text
            : Rewrite(text, (sb, value) => sb.Append(value <= 0xFFFF
                ? "\\u" + value.ToString("X4", CultureInfo.InvariantCulture)
                : "\\U" + value.ToString("X8", CultureInfo.InvariantCulture)));
    }

    private static int IndexOfUnsafe(string text)
    {
        for (int i = 0; i < text.Length;)
        {
            if (IsUnsafeAt(text, i, out int length, out _))
                return i;
            i += length;
        }
        return -1;
    }

    /// <summary>Copies <paramref name="text"/>, handing each unsafe character's code to <paramref name="replace"/>.</summary>
    private static string Rewrite(string text, Action<StringBuilder, int> replace)
    {
        var sb = new StringBuilder(text.Length + 16);
        for (int i = 0; i < text.Length;)
        {
            if (IsUnsafeAt(text, i, out int length, out int code))
                replace(sb, code);
            else
                sb.Append(text, i, length);
            i += length;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Whether the character at <paramref name="index"/> may steer whatever displays or parses the text: C0/C1
    /// controls (incl. CR, LF, NUL, ESC, DEL), format characters (bidi overrides, tag characters …) and the Unicode
    /// line/paragraph separators. A surrogate on its own counts too - it cannot be encoded, and the sink would replace
    /// it with something it chooses. <paramref name="length"/> is the character's length in UTF-16 units,
    /// <paramref name="code"/> its code point (the surrogate's own value for a lone one).
    /// </summary>
    private static bool IsUnsafeAt(string text, int index, out int length, out int code)
    {
        if (Rune.DecodeFromUtf16(text.AsSpan(index), out Rune rune, out length) != OperationStatus.Done)
        {
            length = Math.Max(length, 1);
            code = text[index];
            return true;
        }
        code = rune.Value;
        return rune.Value != ZeroWidthJoiner
               && Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                   or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
    }
}
