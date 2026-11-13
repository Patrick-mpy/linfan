// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using LinFan.Ipc.Messages;
using LinFan.Ipc.Transport;

namespace LinFan.Ipc.Tests;

/// <summary>A connection that delivers fixed bytes, then ends; what the client writes is kept apart.</summary>
internal sealed class ScriptedStream(byte[] incoming) : Stream
{
    private readonly MemoryStream _incoming = new(incoming);
    private readonly MemoryStream _outgoing = new();

    public static ScriptedStream Of(params IpcSnapshot[] snapshots) =>
        new(snapshots.SelectMany(IpcJson.SerializeLine).ToArray());

    public string Written => Encoding.UTF8.GetString(_outgoing.ToArray());

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => _incoming.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => _outgoing.Write(buffer, offset, count);
}

/// <summary>Client-Transport, der eine vorgefertigte Verbindung liefert.</summary>
internal sealed class FixedClientTransport(Stream stream) : IIpcClientTransport
{
    public Task<Stream> ConnectAsync(string endpoint, CancellationToken ct) => Task.FromResult(stream);
}
