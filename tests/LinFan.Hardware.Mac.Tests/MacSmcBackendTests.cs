// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Hardware.Mac.Smc;

namespace LinFan.Hardware.Mac.Tests;

/// <summary>
/// Backend-spezifische Logik über dem Fake-SMC: Discovery/Klassifizierung, die plattformabhängige
/// Steuer-Freigabe (Apple Silicon / ohne Root ⇒ read-only), das PWM↔Ziel-Drehzahl-Mapping und der
/// Fail-Safe von <see cref="MacSmcBackend.RestoreDefaults"/>.
/// </summary>
public sealed class MacSmcBackendTests
{
    private static FakeSmc TwoFanBoard()
    {
        var smc = new FakeSmc();
        smc.SetUi8("FNum", 2);
        smc.SetFloat("F0Ac", 1200f);
        smc.SetFloat("F0Tg", 1200f);
        smc.SetUi8("F0Md", 0);
        smc.SetFloat("F0Mn", 1200f);
        smc.SetFloat("F0Mx", 5000f);
        smc.SetFloat("F1Ac", 980f);   // read-only Lüfter (keine Tg/Md-Keys)
        smc.SetFloat("TC0P", 47.0f);  // kuratierte Temperatur
        return smc;
    }

    private static MacSmcBackend Backend(FakeSmc smc, bool control) =>
        new(smc, new MacSmcBackend.ControlCapability(control, control ? null : "read-only"));

    [Fact]
    public void Discovery_ClassifiesFans_And_Temps()
    {
        using var b = Backend(TwoFanBoard(), control: true);

        var fans = b.DiscoverFans();
        Assert.Equal(2, fans.Count);
        Assert.Single(fans, f => f.CanControl);         // F0 steuerbar
        Assert.Single(fans, f => !f.CanControl);         // F1 read-only
        Assert.Null(fans.First(f => f.CanControl).ReadOnlyReason);
        Assert.Equal(FanReadOnlyReason.NoControlInterface, fans.First(f => !f.CanControl).ReadOnlyReason); // no Tg/Md keys

        var sensors = b.DiscoverSensors();
        Assert.Contains(sensors, s => s.Kind == SensorKind.Temperature && s.Name == "CPU Proximity");
        Assert.Equal(2, sensors.Count(s => s.Kind == SensorKind.FanRpm));

        // Der steuerbare Lüfter ist mit seinem Ist-Drehzahl-Sensor (Tacho) verknüpft.
        var controllable = fans.First(f => f.CanControl);
        Assert.NotNull(controllable.Tachometer);
    }

    /// <summary>Every channel belongs to the SMC, which offers no fingerprint short of a serial number.</summary>
    [Fact]
    public void Discovery_NamesTheSmc_AsTheDeviceOfEveryChannel_WithoutAFingerprint()
    {
        using var b = Backend(TwoFanBoard(), control: true);

        var smc = new HardwareDevice("SMC", null);
        Assert.All(b.DiscoverFans(), f => Assert.Equal(smc, f.Device));
        Assert.All(b.DiscoverSensors(), s => Assert.Equal(smc, s.Device));
    }

    [Fact]
    public void Control_IsGated_WhenPlatformDisallows()
    {
        var smc = TwoFanBoard();
        using var b = Backend(smc, control: false); // Apple Silicon / ohne Root

        Assert.All(b.DiscoverFans(), f => Assert.False(f.CanControl));
        var fans = b.DiscoverFans();
        Assert.Equal(FanReadOnlyReason.NoWriteAccess, fans.Single(f => f.Id.Value == "smc/F0").ReadOnlyReason);
        // F1 has no control keys: root would not help either.
        Assert.Equal(FanReadOnlyReason.NoControlInterface, fans.Single(f => f.Id.Value == "smc/F1").ReadOnlyReason);

        var id = b.DiscoverFans().First().Id;
        Assert.Throws<NotSupportedException>(() => b.SetPwm(id, 128));
        Assert.Equal("read-only", b.StartupWarning);
    }

    [Fact]
    public void SetPwm_ForcesManual_AndWritesMappedTargetRpm()
    {
        var smc = TwoFanBoard();
        using var b = Backend(smc, control: true);
        var id = b.DiscoverFans().First(f => f.CanControl).Id;

        b.SetPwm(id, 128); // ohne vorheriges SetMode

        Assert.Equal(FanMode.Manual, b.GetMode(id));
        Assert.InRange((int)b.GetPwm(id), 126, 130);

        // Ziel-Drehzahl liegt bei ~ Min + (Max-Min)*128/255 = 1200 + 3800*0,502 ≈ 3107 RPM.
        Assert.True(smc.TryReadKey("F0Tg", out var tg));
        Assert.InRange(SmcCodec.Decode(tg), 3090.0, 3125.0);
        Assert.True(smc.TryReadKey("F0Md", out var md));
        Assert.Equal(1.0, SmcCodec.Decode(md), 3); // Manual
    }

    [Fact]
    public void GetMode_OfAnUnreadableModeKey_Throws_InsteadOfReportingAuto()
    {
        var smc = TwoFanBoard();
        using var b = Backend(smc, control: true);
        var id = b.DiscoverFans().First(f => f.CanControl).Id;
        smc.FailReadsFor("F0Md");

        Assert.Throws<IOException>(() => b.GetMode(id));
    }

    [Fact]
    public void RestoreDefaults_ReturnsControllableFanToAuto()
    {
        var smc = TwoFanBoard();
        using var b = Backend(smc, control: true);
        var id = b.DiscoverFans().First(f => f.CanControl).Id;

        b.SetPwm(id, 50);
        Assert.Equal(FanMode.Manual, b.GetMode(id));

        b.RestoreDefaults();

        Assert.Equal(FanMode.Auto, b.GetMode(id));
        Assert.True(smc.TryReadKey("F0Md", out var md));
        Assert.Equal(0.0, SmcCodec.Decode(md), 3); // Firmware-Auto
    }

    /// <summary>The CLI's read-only commands dispose the backend as root too - next to a daemon driving the fans.</summary>
    [Fact]
    public void Dispose_WithoutAWrite_LeavesTheFansAlone()
    {
        var smc = TwoFanBoard();
        smc.SetUi8("F0Md", 1); // manual, set by another process
        var b = Backend(smc, control: true);

        b.Dispose();

        Assert.True(smc.TryReadKey("F0Md", out var md));
        Assert.Equal(1.0, SmcCodec.Decode(md), 3);
        Assert.True(smc.Disposed);
    }

    [Fact]
    public void Dispose_AfterAWrite_HandsTheFansBack()
    {
        var smc = TwoFanBoard();
        var b = Backend(smc, control: true);
        b.SetPwm(b.DiscoverFans().First(f => f.CanControl).Id, 50);

        b.Dispose();

        Assert.True(smc.TryReadKey("F0Md", out var md));
        Assert.Equal(0.0, SmcCodec.Decode(md), 3);
    }

    [Fact]
    public void Control_IsDenied_WhenTargetTypeNotEncodable()
    {
        // Alle Steuer-Keys da UND Plattform erlaubt - aber der Ziel-Typ ist nicht kodierbar.
        // Dann darf der Kanal NICHT steuerbar sein (sonst schaltete SetPwm auf Manual und übersprünge
        // den Ziel-Write mangels Encoder → Lüfter Manual/niedrig mit deaktivierter Firmware-Regelung).
        var smc = new FakeSmc();
        smc.SetUi8("FNum", 1);
        smc.SetFloat("F0Ac", 1200f);
        smc.Set("F0Tg", "zzzz", new byte[] { 0, 0, 0, 0 }); // nicht kodierbarer Typ
        smc.SetUi8("F0Md", 0);
        smc.SetFloat("F0Mn", 1200f);
        smc.SetFloat("F0Mx", 5000f);

        using var b = Backend(smc, control: true);
        var fan = Assert.Single(b.DiscoverFans());
        Assert.False(fan.CanControl);
        Assert.Throws<NotSupportedException>(() => b.SetPwm(fan.Id, 128));
    }

    [Theory]
    [InlineData("F0Md")]
    [InlineData("F0Tg")]
    public void SetPwm_Throws_WhenTheSmcRefusesAWrite_AndHandsTheFanBack(string refusedKey)
    {
        // A refused write must not pass for done: the loop would report the fan as driven by its curve. With only the
        // target refused the fan was already switched to manual - it must not stay there on its old target.
        var smc = TwoFanBoard();
        smc.FailWritesFor(refusedKey);
        using var b = Backend(smc, control: true);
        var id = b.DiscoverFans().First(f => f.CanControl).Id;

        Assert.Throws<IOException>(() => b.SetPwm(id, 128));

        Assert.True(smc.TryReadKey("F0Md", out var md));
        Assert.True(smc.TryReadKey("F0Tg", out var tg));
        Assert.True(SmcCodec.Decode(md) == 0 || SmcCodec.Decode(tg) >= 5000,
            $"weder Firmware-Auto (Md {SmcCodec.Decode(md)}) noch Volllast (Tg {SmcCodec.Decode(tg)})");
    }

    [Theory]
    [InlineData(FanMode.Manual)]
    [InlineData(FanMode.Auto)]
    public void SetMode_Throws_WhenTheSmcRefusesTheModeWrite(FanMode mode)
    {
        var smc = TwoFanBoard();
        smc.FailWritesFor("F0Md");
        using var b = Backend(smc, control: true);
        var id = b.DiscoverFans().First(f => f.CanControl).Id;

        Assert.Throws<IOException>(() => b.SetMode(id, mode));

        Assert.True(smc.TryReadKey("F0Md", out var md));
        Assert.True(smc.TryReadKey("F0Tg", out var tg));
        Assert.True(SmcCodec.Decode(md) == 0 || SmcCodec.Decode(tg) >= 5000,
            $"weder Firmware-Auto (Md {SmcCodec.Decode(md)}) noch Volllast (Tg {SmcCodec.Decode(tg)})");
    }

    [Fact]
    public void RestoreDefaults_FallsBackToFullSpeed_WhenAutoWriteFails()
    {
        // Steuerbar (bei Scan lesbar/kodierbar), aber der Md-Write scheitert transient. RestoreDefaults
        // muss dann den unabhängigen Volllast-Fallback (Ziel = Max-RPM) nehmen - kein Single-Point-of-Failure.
        var smc = TwoFanBoard();
        smc.FailWritesFor("F0Md"); // Auto-Write (und Manual-Write) scheitern

        using var b = Backend(smc, control: true);
        var id = b.DiscoverFans().First(f => f.CanControl).Id;

        b.RestoreDefaults();

        Assert.True(smc.TryReadKey("F0Tg", out var tg));
        Assert.Equal(5000.0, SmcCodec.Decode(tg), 1); // Volllast = Max-RPM
    }

    [Fact]
    public void ReadValue_Fan_ReturnsLiveRpm()
    {
        var smc = TwoFanBoard();
        using var b = Backend(smc, control: true);
        var rpmSensor = b.DiscoverSensors().First(s => s.Kind == SensorKind.FanRpm);

        Assert.InRange(b.ReadValue(rpmSensor.Id), 900, 1300);
    }

    [Fact]
    public void ReadValue_UnknownSensor_Throws()
    {
        using var b = Backend(TwoFanBoard(), control: true);
        Assert.Throws<KeyNotFoundException>(() => b.ReadValue(new SensorId("smc/nope")));
    }

    [Fact]
    public void NoFans_ProducesStartupWarning()
    {
        var smc = new FakeSmc();
        smc.SetFloat("TC0P", 40f); // nur ein Temp-Sensor, kein FNum
        using var b = Backend(smc, control: true);

        Assert.Empty(b.DiscoverFans());
        Assert.Equal("Keine Lüfter über SMC gefunden.", b.StartupWarning);
    }

    [Fact]
    public void Controllable_WhenAllowed_But_NoWarning()
    {
        using var b = Backend(TwoFanBoard(), control: true);
        Assert.Null(b.StartupWarning); // Lüfter vorhanden + steuerbar ⇒ unauffällig
    }

    [Theory]
    [InlineData(1200, 5000, 1200, 0)]
    [InlineData(1200, 5000, 5000, 255)]
    [InlineData(1200, 5000, 3100, 128)]
    public void RpmToPwm_MapsRange(double min, double max, double rpm, int expected)
    {
        Assert.Equal(expected, MacSmcBackend.RpmToPwm(rpm, min, max));
    }

    [Fact]
    public void RpmToPwm_DegenerateRange_IsZero()
    {
        Assert.Equal(0, MacSmcBackend.RpmToPwm(3000, 5000, 5000));
        Assert.Equal(0, MacSmcBackend.RpmToPwm(double.NaN, 1200, 5000));
    }

    [Fact]
    public void PwmToRpm_MapsEndpoints()
    {
        Assert.Equal(1200, MacSmcBackend.PwmToRpm(0, 1200, 5000), 3);
        Assert.Equal(5000, MacSmcBackend.PwmToRpm(255, 1200, 5000), 3);
    }
}
