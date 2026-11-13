// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using LinFan.Ipc;
using Xunit;

namespace LinFan.Ipc.Tests;

/// <summary>
/// The bounded NDJSON line splitter both IPC sides read through: neither a daemon peer nor an oversized snapshot to a GUI
/// may grow a line buffer without limit (audit run-2, hardening notes h2/h7).
/// </summary>
public class NdjsonLineBufferTests
{
    [Fact]
    public void Lines_SplitAcrossChunks_ArriveWhole_AndEmptyLinesAreSkipped()
    {
        var lines = new NdjsonLineBuffer(maxLineBytes: 64, retainedBytes: 64);
        var seen = new List<string>();

        Assert.True(lines.Feed("{\"a\":"u8, Collect(seen), Fail));
        Assert.True(lines.InLine);
        Assert.True(lines.Feed("1}\n\n{\"b\":2}\n{\"c\""u8, Collect(seen), Fail));
        Assert.True(lines.Feed(":3}\n"u8, Collect(seen), Fail));

        Assert.Equal(["{\"a\":1}", "{\"b\":2}", "{\"c\":3}"], seen);
        Assert.False(lines.InLine);
    }

    [Fact]
    public void ALineOverTheBound_IsReported_AndDroppedUpToItsEnd_TheNextLineStillArrives()
    {
        var lines = new NdjsonLineBuffer(maxLineBytes: 8, retainedBytes: 8);
        var seen = new List<string>();
        int tooLong = 0;
        bool Count() { tooLong++; return true; }

        Assert.True(lines.Feed("0123456789"u8, Collect(seen), Count));
        Assert.True(lines.Feed("still the same line\nok\n"u8, Collect(seen), Count));

        Assert.Equal(1, tooLong);
        Assert.Equal(["ok"], seen);
    }

    [Fact]
    public void ALineOfExactlyTheBound_IsAccepted()
    {
        var lines = new NdjsonLineBuffer(maxLineBytes: 4, retainedBytes: 4);
        var seen = new List<string>();

        Assert.True(lines.Feed("abcd\n"u8, Collect(seen), Fail));

        Assert.Equal(["abcd"], seen);
    }

    [Fact]
    public void ACallbackReturningFalse_StopsTheFeed_AndLeavesTheRestUnread()
    {
        var lines = new NdjsonLineBuffer(maxLineBytes: 4, retainedBytes: 4);
        var seen = new List<string>();

        Assert.False(lines.Feed("toolong\nnext\n"u8, Collect(seen), () => false));
        Assert.Empty(seen);

        var stopping = new NdjsonLineBuffer(maxLineBytes: 64, retainedBytes: 64);
        Assert.False(stopping.Feed("first\nsecond\n"u8, line => { seen.Add(Encoding.UTF8.GetString(line)); return false; }, Fail));
        Assert.Equal(["first"], seen);
    }

    [Fact]
    public void TheBufferALongLineGrew_IsReleasedOnceTheLineIsHandled()
    {
        const int retained = 64 * 1024;
        var lines = new NdjsonLineBuffer(maxLineBytes: 8 * 1024 * 1024, retainedBytes: retained);
        int capacityWhileHandling = 0;
        byte[] large = Encoding.UTF8.GetBytes(new string('x', 1024 * 1024) + "\n");

        Assert.True(lines.Feed(large, _ => { capacityWhileHandling = lines.BufferCapacity; return true; }, Fail));

        Assert.True(capacityWhileHandling >= 1024 * 1024);
        Assert.True(lines.BufferCapacity <= retained);
    }

    [Fact]
    public void ALineThatOverflows_DoesNotKeepItsBuffer()
    {
        var lines = new NdjsonLineBuffer(maxLineBytes: 1024 * 1024, retainedBytes: 1024);

        Assert.True(lines.Feed(new byte[1024 * 1024], _ => true, () => true));
        Assert.True(lines.Feed(new byte[1], _ => true, () => true));

        Assert.False(lines.InLine);
        Assert.True(lines.BufferCapacity <= 1024);
    }

    private static LineHandler Collect(List<string> into) => line =>
    {
        into.Add(Encoding.UTF8.GetString(line));
        return true;
    };

    private static bool Fail() => throw new Xunit.Sdk.XunitException("no line should exceed the bound");
}
