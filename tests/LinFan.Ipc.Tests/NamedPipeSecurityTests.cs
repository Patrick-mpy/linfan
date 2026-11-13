// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using LinFan.Ipc.Transport;
using Xunit;

namespace LinFan.Ipc.Tests;

/// <summary>
/// Windows-only access control of the daemon pipe (security audit W1/W4). Each test returns early elsewhere: .NET
/// emulates named pipes over Unix sockets there, without DACLs, owners or <see cref="PipeOptions.FirstPipeInstance"/>.
/// </summary>
public class NamedPipeSecurityTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static string PipeName() => $"linfan-test-{Guid.NewGuid():N}";

    [Fact]
    public void Dacl_WithoutTheGroup_GrantsNobodyElse()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var transport = new NamedPipeServerTransport($"linfan-missing-{Guid.NewGuid():N}");
        PipeSecurity security = transport.BuildSecurity();

        Assert.False(Grants(security, WellKnownSidType.AuthenticatedUserSid));
        Assert.False(Grants(security, WellKnownSidType.WorldSid));
        Assert.False(Grants(security, WellKnownSidType.BuiltinUsersSid));
        Assert.True(Grants(security, WellKnownSidType.LocalSystemSid));
        Assert.True(Grants(security, WellKnownSidType.BuiltinAdministratorsSid));
    }

    [SupportedOSPlatform("windows")]
    private static bool Grants(PipeSecurity security, WellKnownSidType type) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Any(r => ((SecurityIdentifier)r.IdentityReference).IsWellKnown(type));

    [Fact]
    public async Task Listen_WhenTheDaemonOnThePipeServes_StandsDown()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string name = PipeName();
        using var other = new NamedPipeServerStream(
            name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
        Task serve = Task.Run(async () =>
        {
            await other.WaitForConnectionAsync();
            await other.WriteAsync(new byte[] { (byte)'{' });
            await other.FlushAsync();
        });
        using var transport = new NamedPipeServerTransport(
            NamedPipeServerTransport.DefaultAllowedGroup, silentAfter: TimeSpan.FromSeconds(10), retryFor: TimeSpan.Zero);

        var ex = Assert.Throws<IpcEndpointInUseException>(() => transport.Listen(name));
        await serve;
        Assert.True(ex.Serving);
    }

    [Fact]
    public async Task Listen_WhenTheDaemonOnThePipeSendsNothing_StandsDownAsSilent()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string name = PipeName();
        using var other = new NamedPipeServerStream(
            name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
        Task waiting = other.WaitForConnectionAsync();
        using var transport = new NamedPipeServerTransport(
            NamedPipeServerTransport.DefaultAllowedGroup, silentAfter: TimeSpan.FromMilliseconds(500), retryFor: TimeSpan.Zero);

        var ex = Assert.Throws<IpcEndpointInUseException>(() => transport.Listen(name));
        await waiting;
        Assert.False(ex.Serving);
    }

    [Fact]
    public async Task Listen_WhenThePredecessorReleasesTheNameInTime_TakesIt()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string name = PipeName();
        var old = new NamedPipeServerStream(
            name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
        using var transport = new NamedPipeServerTransport(
            NamedPipeServerTransport.DefaultAllowedGroup, retryFor: TimeSpan.FromSeconds(10));
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            old.Dispose();
        });

        transport.Listen(name); // no IpcEndpointInUseException: the name is free by the time the retries run out
        using var cts = new CancellationTokenSource(Timeout);
        Task<Stream> accept = transport.AcceptAsync(cts.Token);
        await using Stream client = await new NamedPipeClientTransport().ConnectAsync(name, cts.Token);
        await using Stream server = await accept;
    }

    [Fact]
    public void IsEndpointHeld_ByAPipeOfThisAccount_ButNotAFreeName()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string name = PipeName();
        Assert.False(IpcTransportFactory.IsEndpointHeld(name));

        using var daemon = new NamedPipeServerStream(
            name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
        _ = daemon.WaitForConnectionAsync(); // listens, sends nothing - held all the same
        Assert.True(IpcTransportFactory.IsEndpointHeld(name));
    }

    [Fact]
    public void Listen_CanBeCancelledWhileProbing()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string name = PipeName();
        using var other = new NamedPipeServerStream(
            name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
        _ = other.WaitForConnectionAsync();
        using var transport = new NamedPipeServerTransport(
            NamedPipeServerTransport.DefaultAllowedGroup, silentAfter: TimeSpan.FromSeconds(30), retryFor: TimeSpan.Zero);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        Assert.ThrowsAny<OperationCanceledException>(() => transport.Listen(name, cts.Token));
    }

    [Fact]
    public async Task Accept_WithoutAFollowUpInstance_StillAcceptsWhileAnotherClientIsConnected()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // An aborted accept leaves no follow-up instance. With a GUI still connected the name cannot be taken as a
        // first instance again, so the transport has to rejoin its own pipe instead of locking every new GUI out.
        string name = PipeName();
        using var transport = new NamedPipeServerTransport();
        transport.Listen(name);
        using var cts = new CancellationTokenSource(Timeout);

        Task<Stream> acceptHeld = transport.AcceptAsync(cts.Token);
        await using Stream heldClient = await new NamedPipeClientTransport().ConnectAsync(name, cts.Token);
        await using Stream heldServer = await acceptHeld;

        using (var aborted = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.AcceptAsync(aborted.Token));

        Task<Stream> accept = transport.AcceptAsync(cts.Token);
        await using Stream client = await new NamedPipeClientTransport().ConnectAsync(name, cts.Token);
        await using Stream server = await accept;
    }

    [Fact]
    public async Task Restart_WhileAClientStillHoldsTheOldPipe_StillCreatesTheFirstInstance()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // A service restart with the GUI connected: the old server end is gone, the GUI's handle is not yet.
        string name = PipeName();
        using var cts = new CancellationTokenSource(Timeout);
        Stream staleClient;
        using (var old = new NamedPipeServerTransport())
        {
            old.Listen(name);
            Task<Stream> accept = old.AcceptAsync(cts.Token);
            staleClient = await new NamedPipeClientTransport().ConnectAsync(name, cts.Token);
            (await accept).Dispose();
        }

        using var restarted = new NamedPipeServerTransport();
        restarted.Listen(name);
        Exception? whileHeld = await Record.ExceptionAsync(async () =>
        {
            using var shortWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            (await restarted.AcceptAsync(shortWait.Token)).Dispose();
        });
        await staleClient.DisposeAsync();

        Task<Stream> retry = restarted.AcceptAsync(cts.Token);
        await using Stream client = await new NamedPipeClientTransport().ConnectAsync(name, cts.Token);
        await using Stream server = await retry;
        // The new first instance is created despite the stale handle; the accept merely timed out unanswered.
        Assert.IsAssignableFrom<OperationCanceledException>(whileHeld);
    }

    [Fact]
    public void ServerOwner_OnlySystemAdministratorsOrTheCurrentUserAreTrusted()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var self = WindowsIdentity.GetCurrent();
        Assert.True(PipeOwner.IsTrusted(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.True(PipeOwner.IsTrusted(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
        Assert.True(PipeOwner.IsTrusted(self.User));

        Assert.False(PipeOwner.IsTrusted(null));
        Assert.False(PipeOwner.IsTrusted(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null)));
        Assert.False(PipeOwner.IsTrusted(new SecurityIdentifier("S-1-5-21-1-2-3-1001")));
    }
}
