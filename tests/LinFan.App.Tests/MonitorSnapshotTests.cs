// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Services;
using Xunit;

namespace LinFan.App.Tests;

public class MonitorSnapshotTests
{
    [Fact]
    public void Unavailable_IsNotConnected()
    {
        MonitorSnapshot snap = MonitorSnapshot.Unavailable("Daemon nicht erreichbar");

        Assert.False(snap.Connected); // steuert den „nicht verbunden"-Banner in der GUI
        Assert.Empty(snap.Sensors);
        Assert.Empty(snap.Fans);
    }
}
