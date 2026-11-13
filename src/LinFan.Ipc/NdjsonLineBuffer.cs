// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;

namespace LinFan.Ipc;

/// <summary>Receives one complete NDJSON line, without its line feed; <c>false</c> stops the feed.</summary>
internal delegate bool LineHandler(ReadOnlySpan<byte> line);

/// <summary>
/// Splits a byte stream into NDJSON lines with an upper bound per line. <see cref="StreamReader.ReadLineAsync()"/> would
/// buffer a line without an end for as long as the peer keeps sending; both sides of the IPC boundary read through
/// this instead - neither a peer of the root daemon nor an oversized snapshot to a GUI can grow a buffer without limit.
/// </summary>
internal sealed class NdjsonLineBuffer
{
    private readonly MemoryStream _line = new();
    private readonly int _maxLineBytes;
    private readonly int _retainedBytes;
    private bool _skipping;

    /// <param name="maxLineBytes">Longest line accepted; a longer one goes to the <c>onTooLong</c> callback.</param>
    /// <param name="retainedBytes">Buffer kept between lines; a larger one, grown by a long line, is released.</param>
    public NdjsonLineBuffer(int maxLineBytes, int retainedBytes)
    {
        _maxLineBytes = maxLineBytes;
        _retainedBytes = retainedBytes;
    }

    /// <summary>A line has begun and not yet ended.</summary>
    public bool InLine => _line.Length > 0;

    /// <summary><see cref="Stopwatch.GetTimestamp"/> of the first byte of the pending line.</summary>
    public long LineStarted { get; private set; }

    /// <summary>The buffer currently held (test seam for the release after a long line).</summary>
    internal int BufferCapacity => _line.Capacity;

    /// <summary>
    /// Consumes <paramref name="chunk"/>: every complete line goes to <paramref name="onLine"/>, a line growing past the
    /// bound to <paramref name="onTooLong"/>, and its remaining bytes up to the next line feed are dropped. Empty lines
    /// are skipped.
    /// </summary>
    /// <returns><c>false</c> if a callback stopped the feed; the rest of the chunk is then left unread.</returns>
    public bool Feed(ReadOnlySpan<byte> chunk, LineHandler onLine, Func<bool> onTooLong)
    {
        while (!chunk.IsEmpty)
        {
            int end = chunk.IndexOf((byte)'\n');
            ReadOnlySpan<byte> part = end < 0 ? chunk : chunk[..end];
            chunk = end < 0 ? ReadOnlySpan<byte>.Empty : chunk[(end + 1)..];

            if (_skipping)
            {
                _skipping = end < 0;
                continue;
            }

            if (_line.Length + part.Length > _maxLineBytes)
            {
                Clear();
                _skipping = end < 0;
                if (!onTooLong())
                    return false;
                continue;
            }

            if (_line.Length == 0 && !part.IsEmpty)
                LineStarted = Stopwatch.GetTimestamp();
            _line.Write(part);
            if (end < 0)
                break;

            bool proceed = _line.Length == 0 || onLine(_line.GetBuffer().AsSpan(0, (int)_line.Length));
            Clear();
            if (!proceed)
                return false;
        }
        return true;
    }

    private void Clear()
    {
        _line.SetLength(0);
        if (_line.Capacity > _retainedBytes)
            _line.Capacity = _retainedBytes;
    }
}
