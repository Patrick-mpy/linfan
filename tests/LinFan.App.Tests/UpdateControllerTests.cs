// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.App.Services;
using Xunit;

namespace LinFan.App.Tests;

public class UpdateControllerTests : IDisposable
{
    private static readonly SemVer Current = new(0, 1, 0, null);
    private readonly List<string> _tempPaths = new();

    private UiSettingsStore NewStore(UiSettings seed)
    {
        string path = Path.Combine(Path.GetTempPath(), "linfan-updtest-" + Guid.NewGuid().ToString("N") + ".json");
        _tempPaths.Add(path);
        var store = new UiSettingsStore(path);
        store.Save(seed);
        return store;
    }

    private static UpdateController New(
        UpdateInfo? result, UiSettingsStore store, FakeService? fake = null, Func<string, Task<bool>>? open = null)
        => new(fake ?? new FakeService(result), store, Current, open ?? (_ => Task.FromResult(true)), marshal: a => a());

    [Fact]
    public async Task NewerVersion_NotDismissed_ShowsBanner()
    {
        var co = New(new UpdateInfo("0.2.0", "https://example/rel"), NewStore(new UiSettings { UpdateChecksEnabled = true }));
        await co.CheckAsync();
        Assert.True(co.UpdateAvailable);
        Assert.Equal("0.2.0", co.LatestVersion);
    }

    [Fact]
    public async Task OptOut_SkipsCheckEntirely()
    {
        var fake = new FakeService(new UpdateInfo("0.2.0", "https://example/rel"));
        var co = New(null, NewStore(new UiSettings { UpdateChecksEnabled = false }), fake);
        await co.CheckAsync();
        Assert.False(co.UpdateAvailable);
        Assert.Equal(0, fake.Calls); // Opt-out fragt gar nicht erst ab
    }

    [Fact]
    public async Task DismissedSameVersion_SuppressesBanner()
    {
        var store = NewStore(new UiSettings { UpdateChecksEnabled = true, DismissedUpdateVersion = "0.2.0" });
        var co = New(new UpdateInfo("0.2.0", "https://example/rel"), store);
        await co.CheckAsync();
        Assert.False(co.UpdateAvailable);
    }

    [Fact]
    public async Task DismissedOlderVersion_StillShows()
    {
        var store = NewStore(new UiSettings { UpdateChecksEnabled = true, DismissedUpdateVersion = "0.1.5" });
        var co = New(new UpdateInfo("0.2.0", "https://example/rel"), store);
        await co.CheckAsync();
        Assert.True(co.UpdateAvailable); // neuere Version als die weggeklickte → wieder anzeigen
    }

    [Fact]
    public async Task ServiceReturnsNull_NoBanner()
    {
        var co = New(null, NewStore(new UiSettings { UpdateChecksEnabled = true }));
        await co.CheckAsync();
        Assert.False(co.UpdateAvailable);
    }

    [Fact]
    public async Task Dismiss_PersistsVersion_AndHidesBanner()
    {
        var store = NewStore(new UiSettings { UpdateChecksEnabled = true });
        var co = New(new UpdateInfo("0.2.0", "https://example/rel"), store);
        await co.CheckAsync();
        Assert.True(co.UpdateAvailable);

        co.DismissCommand.Execute(null);

        Assert.False(co.UpdateAvailable);
        Assert.Equal("0.2.0", store.Load().DismissedUpdateVersion);
    }

    [Fact]
    public async Task OpenRelease_OpensReleaseUrl()
    {
        string? opened = null;
        var co = New(new UpdateInfo("0.2.0", "https://example/rel"),
            NewStore(new UiSettings { UpdateChecksEnabled = true }),
            open: u => { opened = u; return Task.FromResult(true); });
        await co.CheckAsync();

        await co.OpenReleaseCommand.ExecuteAsync(null);

        Assert.Equal("https://example/rel", opened);
    }

    public void Dispose()
    {
        foreach (string p in _tempPaths)
            try { File.Delete(p); } catch { /* best effort */ }
    }

    private sealed class FakeService : IUpdateCheckService
    {
        private readonly UpdateInfo? _result;
        public int Calls { get; private set; }
        public FakeService(UpdateInfo? result) => _result = result;
        public Task<UpdateInfo?> CheckAsync(SemVer current, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }
}
