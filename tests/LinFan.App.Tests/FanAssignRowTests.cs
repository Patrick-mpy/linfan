// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Ipc.Messages;

namespace LinFan.App.Tests;

public sealed class FanAssignRowTests
{
    private static FanAssignRow MakeRow(byte minPwm = 0, byte maxPwm = 255) =>
        new FanAssignRow(
            new FanConfig { FanId = "hwmon1/pwm1", Name = "Test Fan", MinPwm = minPwm, MaxPwm = maxPwm },
            selected: null,
            availableCurves: new ObservableCollection<CurveEditRow>());

    [Fact]
    public void Constructor_InitializesMinMaxFromBaseFan()
    {
        var row = MakeRow(minPwm: 30, maxPwm: 200);

        Assert.Equal(30, row.MinPwm);
        Assert.Equal(200, row.MaxPwm);
    }

    [Fact]
    public void ToConfig_ReturnsSetMinMax()
    {
        var row = MakeRow(minPwm: 50, maxPwm: 220);
        row.MinPwm = 60;
        row.MaxPwm = 210;

        FanConfig result = row.ToConfig();

        Assert.Equal((byte)60, result.MinPwm);
        Assert.Equal((byte)210, result.MaxPwm);
    }

    [Fact]
    public void ToConfig_ClampsMinMaxTo0_255()
    {
        var row = MakeRow();
        row.MinPwm = -10;
        row.MaxPwm = 300;

        FanConfig result = row.ToConfig();

        Assert.Equal((byte)0, result.MinPwm);
        Assert.Equal((byte)255, result.MaxPwm);
    }

    [Fact]
    public void ToConfig_TrimsName()
    {
        var row = MakeRow();
        row.Name = "  CPU-Lüfter  ";

        Assert.Equal("CPU-Lüfter", row.ToConfig().Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ToConfig_EmptyName_FallsBackToPreviousDisplayName(string emptied)
    {
        // baseFan.Name = "Test Fan" - der zuletzt geladene Anzeigename bleibt erhalten.
        var row = MakeRow();
        row.Name = emptied;

        Assert.Equal("Test Fan", row.ToConfig().Name);
    }

    /// <summary>
    /// Empty means "no own name" - then the hardware label applies everywhere. This used to yield the FanId,
    /// which turned the raw hardware path ("/lpc/nct6797d/0/control/1") into the display name.
    /// </summary>
    [Fact]
    public void ToConfig_EmptyName_AndEmptyBaseName_StaysEmpty()
    {
        var row = new FanAssignRow(
            new FanConfig { FanId = "hwmon1/pwm1", Name = "" },
            selected: null, availableCurves: new ObservableCollection<CurveEditRow>());
        row.Name = "   ";

        Assert.Equal("", row.ToConfig().Name);
    }

    /// <summary>
    /// Without an own name the row shows the hardware label as a <b>placeholder</b>, not as a value.
    /// Otherwise an untouched field would freeze it as a user-defined name on the next save, and the
    /// daemon-side "no pseudo name" fix would come to nothing.
    /// </summary>
    [Fact]
    public void HardwareLabel_IsPlaceholderOnly_AndNotPersisted()
    {
        var row = new FanAssignRow(
            new FanConfig { FanId = "/lpc/nct6797d/0/control/1", Name = "" },
            selected: null, availableCurves: new ObservableCollection<CurveEditRow>(),
            hardwareLabel: "Nuvoton NCT6797D Fan #2");

        Assert.Equal("Nuvoton NCT6797D Fan #2", row.NamePlaceholder);
        Assert.Equal("", row.Name);
        Assert.Equal("", row.ToConfig().Name);

        row.Name = "CPU";
        Assert.Equal("CPU", row.ToConfig().Name);   // a real own name is of course kept
    }

    [Fact]
    public void ToConfig_PreservesOtherBaseFields_WhenMinMaxChanged()
    {
        var row = MakeRow(minPwm: 40, maxPwm: 200);
        row.MinPwm = 80;
        row.MaxPwm = 180;

        FanConfig result = row.ToConfig();

        Assert.Equal("hwmon1/pwm1", result.FanId);
        Assert.Equal((byte)80, result.MinPwm);
        Assert.Equal((byte)180, result.MaxPwm);
    }

    [Fact]
    public void OnMinPwmChanged_ClampsMaxUp_WhenMinExceedsMax()
    {
        var row = MakeRow(minPwm: 50, maxPwm: 100);
        row.MinPwm = 150; // exceeds MaxPwm=100

        Assert.Equal(150, row.MaxPwm); // Max should be raised to Min
    }

    [Fact]
    public void OnMaxPwmChanged_ClampsMinDown_WhenMaxBelowMin()
    {
        var row = MakeRow(minPwm: 100, maxPwm: 200);
        row.MaxPwm = 50; // below MinPwm=100

        Assert.Equal(50, row.MinPwm); // Min should be lowered to Max
    }

    // --- PWM-Auto-Swap-Hinweis (Punkt 3) --------------------------------------------------------

    [Fact]
    public void NewRow_NoPwmAdjustHint()
    {
        var row = MakeRow(minPwm: 30, maxPwm: 200);
        Assert.Equal("", row.PwmAdjustHint);
    }

    [Fact]
    public void MinExceedsMax_RaisesMax_AndSetsHint()
    {
        var row = MakeRow(minPwm: 50, maxPwm: 100);
        row.MinPwm = 150; // > Max=100

        Assert.Equal(150, row.MaxPwm);
        Assert.Contains("59 %", row.PwmAdjustHint); // 150/255 ≈ 59 % - der Hinweis spricht Prozent
        Assert.Contains("angehoben", row.PwmAdjustHint);
    }

    [Fact]
    public void MaxBelowMin_LowersMin_AndSetsHint()
    {
        var row = MakeRow(minPwm: 100, maxPwm: 200);
        row.MaxPwm = 50; // < Min=100

        Assert.Equal(50, row.MinPwm);
        Assert.Contains("20 %", row.PwmAdjustHint); // 50/255 ≈ 20 %
        Assert.Contains("gesenkt", row.PwmAdjustHint);
    }

    [Fact]
    public void ValidChange_AfterAdjust_ClearsHint()
    {
        var row = MakeRow(minPwm: 50, maxPwm: 100);
        row.MinPwm = 150; // löst Anheben + Hinweis aus
        Assert.NotEqual("", row.PwmAdjustHint);

        row.MinPwm = 80; // jetzt gültig (≤ Max=150), kein Nachziehen
        Assert.Equal("", row.PwmAdjustHint);
    }

    [Fact]
    public void ValidMaxChange_DoesNotSetHint()
    {
        var row = MakeRow(minPwm: 30, maxPwm: 100);
        row.MaxPwm = 200; // ≥ Min, kein Nachziehen

        Assert.Equal("", row.PwmAdjustHint);
        Assert.Equal(30, row.MinPwm);
    }

    // --- PWM-Anzeige in Prozent (Geräte-Tab) ----------------------------------------------------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(255, 100)]
    [InlineData(128, 50)] // 128/255 ≈ 50,2 % → gerundet 50
    public void MinMaxPercent_ReflectRawPwm(byte raw, int expectedPercent)
    {
        var row = MakeRow(minPwm: raw, maxPwm: raw);

        Assert.Equal(expectedPercent, row.MinPercent);
        Assert.Equal(expectedPercent, row.MaxPercent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(99)]
    [InlineData(100)]
    public void SettingPercent_RoundTripsStably(int percent)
    {
        var row = MakeRow();
        row.MaxPercent = 100; // freier Spielraum nach oben, sonst zieht das Auto-Adjust mit
        row.MinPercent = percent;

        // %→PWM→% ist stabil, weil Prozent gröber ist als die 256 PWM-Stufen.
        Assert.Equal(percent, row.MinPercent);
    }

    [Fact]
    public void UntouchedMinPwm_StaysExact_OnSave_EvenIfPercentRounds()
    {
        // 100/255 ≈ 39 % - angezeigt wird 39 %, gespeichert bleibt aber exakt 100 (kein stiller Drift).
        var row = MakeRow(minPwm: 100, maxPwm: 255);

        Assert.Equal(39, row.MinPercent);
        Assert.Equal((byte)100, row.ToConfig().MinPwm);
    }

    [Fact]
    public void SettingMinPercent_AboveMax_RaisesMaxPercent_AndHintInPercent()
    {
        var row = MakeRow(minPwm: 0, maxPwm: 0);
        row.MinPercent = 80; // > Max=0 %

        Assert.Equal(80, row.MaxPercent); // Max wird auf Min nachgezogen
        Assert.Contains("%", row.PwmAdjustHint);
        Assert.Contains("angehoben", row.PwmAdjustHint);
    }

    [Fact]
    public void SettingMinPercent_RaisesMinPercentNotification()
    {
        var row = MakeRow();
        var changed = new List<string>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        row.MinPercent = 50;

        Assert.Contains(nameof(FanAssignRow.MinPercent), changed);
    }

    // --- „Bereits kalibriert"-Badge -------------------------------------------------------------

    [Fact]
    public void NewRow_WithoutCalibration_IsNotMarkedCalibrated()
    {
        var row = MakeRow();

        Assert.False(row.IsCalibrated);
        Assert.Equal("", row.CalibrationBadgeHint);
    }

    [Fact]
    public void LoadedCalibration_MarksCalibrated_AndHintShowsStartPercent()
    {
        var row = new FanAssignRow(
            new FanConfig { FanId = "hwmon1/pwm1", Name = "Fan", Calibration = new FanCalibration { StartPwm = 128 } },
            selected: null, availableCurves: new ObservableCollection<CurveEditRow>());

        Assert.True(row.IsCalibrated);
        Assert.Contains("50 %", row.CalibrationBadgeHint); // 128/255 ≈ 50 %
        Assert.Contains("kalibriert", row.CalibrationBadgeHint);
    }

    [Fact]
    public void LoadedCalibration_NoSafeStart_HintSaysSo()
    {
        var row = new FanAssignRow(
            new FanConfig { FanId = "hwmon1/pwm1", Name = "Fan", Calibration = new FanCalibration { StartPwm = 255 } },
            selected: null, availableCurves: new ObservableCollection<CurveEditRow>());

        Assert.True(row.IsCalibrated);
        Assert.Contains("kein sicherer Anlaufpunkt", row.CalibrationBadgeHint);
    }

    [Fact]
    public void ApplyCalibration_DoneSuccess_MarksCalibrated_WithPercentHint()
    {
        FanAssignRow row = CalibRow(canControl: true, new List<string>());
        Assert.False(row.IsCalibrated);

        row.ApplyCalibration(new CalibrationStatus("hwmon7/pwm1", CalibrationPhase.Done, 0, 0,
            Running: false, Done: true, StartPwm: 51, FailReason: null));

        Assert.True(row.IsCalibrated);
        Assert.Contains("20 %", row.CalibrationBadgeHint); // 51/255 ≈ 20 %
    }

    [Fact]
    public void ApplyCalibration_Error_DoesNotMarkCalibrated()
    {
        FanAssignRow row = CalibRow(canControl: true, new List<string>());

        row.ApplyCalibration(new CalibrationStatus("hwmon7/pwm1", CalibrationPhase.Failed, 0, 0,
            Running: false, Done: false, StartPwm: null, FailReason: CalibrationFailReason.NoTacho));

        Assert.False(row.IsCalibrated);
    }

    // --- Kalibrierung im Geräte-Tab -------------------------------------------------------------

    private static FanAssignRow CalibRow(bool canControl, List<string> calls) =>
        new(new FanConfig { FanId = "hwmon7/pwm1", Name = "CPU Fan" },
            selected: null, new ObservableCollection<CurveEditRow>(), canControl,
            id => { calls.Add(id); return Task.CompletedTask; });

    [Fact]
    public async Task Calibrate_WhenControllable_SendsFanIdThroughDelegate()
    {
        var calls = new List<string>();
        FanAssignRow row = CalibRow(canControl: true, calls);

        await row.CalibrateCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "hwmon7/pwm1" }, calls);
    }

    [Fact]
    public async Task Calibrate_WhenNotControllable_DoesNothing()
    {
        var calls = new List<string>();
        FanAssignRow row = CalibRow(canControl: false, calls);

        await row.CalibrateCommand.ExecuteAsync(null);

        Assert.Empty(calls); // read-only Lüfter werden nicht kalibriert
    }

    [Fact]
    public void ApplyCalibration_MatchingFan_SetsRunningAndProgress()
    {
        FanAssignRow row = CalibRow(canControl: true, new List<string>());

        row.ApplyCalibration(new CalibrationStatus("hwmon7/pwm1", CalibrationPhase.Measuring, 120, 1500,
            Running: true, Done: false, StartPwm: null, FailReason: null));

        Assert.True(row.IsCalibrating);
        Assert.Contains("120", row.CalibrationProgress);
    }

    [Fact]
    public void ApplyCalibration_Done_ShowsStartPwm_AndStopsRunning()
    {
        FanAssignRow row = CalibRow(canControl: true, new List<string>());

        row.ApplyCalibration(new CalibrationStatus("hwmon7/pwm1", CalibrationPhase.Done, 0, 0,
            Running: false, Done: true, StartPwm: 96, FailReason: null));

        Assert.False(row.IsCalibrating);
        Assert.Contains("96", row.CalibrationProgress);
    }

    [Fact]
    public void ApplyCalibration_OtherFanOrNull_ClearsInlineState()
    {
        FanAssignRow row = CalibRow(canControl: true, new List<string>());
        row.ApplyCalibration(new CalibrationStatus("hwmon7/pwm1", CalibrationPhase.Measuring, 120, 1500, true, false, null, null));

        row.ApplyCalibration(new CalibrationStatus("other/pwm2", CalibrationPhase.Measuring, 50, 800, true, false, null, null));
        Assert.False(row.IsCalibrating);
        Assert.Equal("", row.CalibrationProgress);

        row.ApplyCalibration(null);
        Assert.False(row.IsCalibrating);
        Assert.Equal("", row.CalibrationProgress);
    }

    // --- Kalibrier-Status latchen (Punkt 4) -----------------------------------------------------

    private static FanAssignRow LatchRow(TimeSpan hold) =>
        new(new FanConfig { FanId = "hwmon7/pwm1", Name = "CPU Fan" },
            selected: null, new ObservableCollection<CurveEditRow>(), canControl: true,
            sendCalibrate: null, calibrationHold: hold);

    private static CalibrationStatus Running(string fanId = "hwmon7/pwm1") =>
        new(fanId, CalibrationPhase.Measuring, 120, 1500, Running: true, Done: false, StartPwm: null, FailReason: null);

    private static CalibrationStatus DoneStatus(string fanId = "hwmon7/pwm1") =>
        new(fanId, CalibrationPhase.Done, 0, 0, Running: false, Done: true, StartPwm: 96, FailReason: null);

    private static CalibrationStatus ErrorStatus(string fanId = "hwmon7/pwm1") =>
        new(fanId, CalibrationPhase.Failed, 0, 0, Running: false, Done: false, StartPwm: null, FailReason: CalibrationFailReason.NoTacho);

    /// <summary>Pollt bis die Bedingung gilt; die Latch-Continuation läuft im Test auf dem ThreadPool.</summary>
    private static async Task WaitUntil(Func<bool> until, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!until() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(5);
    }

    [Fact]
    public async Task Done_LatchesMessage_ThenAutoClearsAfterHold()
    {
        FanAssignRow row = LatchRow(TimeSpan.FromMilliseconds(80));

        row.ApplyCalibration(DoneStatus());
        Assert.False(row.IsCalibrating);
        Assert.Contains("96", row.CalibrationProgress); // sofort sichtbar (gehalten)

        await WaitUntil(() => row.CalibrationProgress == "");
        Assert.Equal("", row.CalibrationProgress); // nach Ablauf der Haltedauer geleert
    }

    [Fact]
    public async Task Error_LatchesMessage_ThenAutoClearsAfterHold()
    {
        FanAssignRow row = LatchRow(TimeSpan.FromMilliseconds(80));

        row.ApplyCalibration(ErrorStatus());
        Assert.False(row.IsCalibrating);
        Assert.Contains("Tachosignal", row.CalibrationProgress); // lokalisierte NoTacho-Meldung

        await WaitUntil(() => row.CalibrationProgress == "");
        Assert.Equal("", row.CalibrationProgress);
    }

    [Fact]
    public async Task DoneLatch_SurvivesNullAndOtherFanSnapshots_UntilHoldElapses()
    {
        FanAssignRow row = LatchRow(TimeSpan.FromMilliseconds(150));

        row.ApplyCalibration(DoneStatus());
        Assert.Contains("96", row.CalibrationProgress);

        // Zwischendurch Snapshots ohne Kalibrierung für diesen Lüfter - die finale Meldung bleibt.
        row.ApplyCalibration(null);
        Assert.Contains("96", row.CalibrationProgress);
        row.ApplyCalibration(Running("other/pwm2"));
        Assert.Contains("96", row.CalibrationProgress);
        Assert.False(row.IsCalibrating);

        await WaitUntil(() => row.CalibrationProgress == "");
        Assert.Equal("", row.CalibrationProgress); // erst nach Ablauf geleert
    }

    [Fact]
    public async Task NewRun_DuringHold_CancelsLatch_AndShowsLiveProgress()
    {
        FanAssignRow row = LatchRow(TimeSpan.FromMilliseconds(500));

        row.ApplyCalibration(DoneStatus());
        Assert.Contains("96", row.CalibrationProgress);

        // Neuer laufender Lauf für DENSELBEN Lüfter bricht das Halten ab → Live-Fortschritt.
        row.ApplyCalibration(Running());
        Assert.True(row.IsCalibrating);
        Assert.Contains("120", row.CalibrationProgress);

        // Das alte (gecancelte) Halten darf den Live-Text nicht später wegräumen.
        await Task.Delay(120);
        Assert.True(row.IsCalibrating);
        Assert.Contains("120", row.CalibrationProgress);
    }

    [Fact]
    public void RunningStatus_NoHold_OtherFanClearsImmediately()
    {
        // Reine Live-Phase (kein Done/Error) startet kein Halten: ein fremder Snapshot räumt wie bisher auf.
        FanAssignRow row = LatchRow(TimeSpan.FromMilliseconds(500));
        row.ApplyCalibration(Running());
        Assert.True(row.IsCalibrating);

        row.ApplyCalibration(null);
        Assert.False(row.IsCalibrating);
        Assert.Equal("", row.CalibrationProgress);
    }

    // --- Lüfter identifizieren ------------------------------------------------------------------

    private static FanAssignRow IdentifyRow(bool canControl, List<string> calls, TimeSpan? hold = null) =>
        new(new FanConfig { FanId = "hwmon7/pwm1", Name = "CPU Fan" },
            selected: null, new ObservableCollection<CurveEditRow>(), canControl,
            sendCalibrate: null, calibrationHold: hold,
            sendIdentify: id => { calls.Add(id); return Task.CompletedTask; });

    [Fact]
    public async Task Identify_WhenControllable_SendsFanIdThroughDelegate()
    {
        var calls = new List<string>();
        FanAssignRow row = IdentifyRow(canControl: true, calls);

        await row.IdentifyCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "hwmon7/pwm1" }, calls);
    }

    [Fact]
    public async Task Identify_WhenNotControllable_DoesNothing()
    {
        var calls = new List<string>();
        FanAssignRow row = IdentifyRow(canControl: false, calls);

        await row.IdentifyCommand.ExecuteAsync(null);

        Assert.Empty(calls); // read-only Lüfter lassen sich nicht identifizieren
    }

    [Fact]
    public void ApplyIdentify_MatchingFanRunning_SetsRunningAndProgress()
    {
        FanAssignRow row = IdentifyRow(canControl: true, new List<string>());

        row.ApplyIdentify(new IdentifyStatus("hwmon7/pwm1", Running: true, FailReason: null));

        Assert.True(row.IsIdentifying);
        Assert.Contains("100 %", row.IdentifyProgress);
    }

    [Fact]
    public void ApplyIdentify_OtherFanOrNull_ClearsInlineState()
    {
        FanAssignRow row = IdentifyRow(canControl: true, new List<string>());
        row.ApplyIdentify(new IdentifyStatus("hwmon7/pwm1", Running: true, FailReason: null));
        Assert.True(row.IsIdentifying);

        row.ApplyIdentify(new IdentifyStatus("other/pwm2", Running: true, FailReason: null));
        Assert.False(row.IsIdentifying);
        Assert.Equal("", row.IdentifyProgress);

        row.ApplyIdentify(new IdentifyStatus("hwmon7/pwm1", Running: true, FailReason: null));
        Assert.True(row.IsIdentifying);

        row.ApplyIdentify(null); // Erfolg: Daemon sendet Identify=null → leerer Live-Zustand
        Assert.False(row.IsIdentifying);
        Assert.Equal("", row.IdentifyProgress);
    }

    [Fact]
    public async Task ApplyIdentify_Error_LatchesMessage_ThenAutoClearsAfterHold()
    {
        FanAssignRow row = IdentifyRow(canControl: true, new List<string>(), TimeSpan.FromMilliseconds(80));

        row.ApplyIdentify(new IdentifyStatus("hwmon7/pwm1", Running: false,
            FailReason: IdentifyFailReason.OverTemperature, OverTempC: 95, OverLimitC: 90));
        Assert.False(row.IsIdentifying);
        Assert.Contains("Übertemperatur", row.IdentifyProgress);

        // Zwischendurch fremde/leere Snapshots - die Abbruch-Meldung bleibt bis zum Ablauf.
        row.ApplyIdentify(null);
        Assert.Contains("Übertemperatur", row.IdentifyProgress);

        await WaitUntil(() => row.IdentifyProgress == "");
        Assert.Equal("", row.IdentifyProgress);
    }

    [Fact]
    public void IdentifyAndCalibrate_DoNotStompEachOthersState()
    {
        // Eigene Halte-Quelle pro Feature: ein laufender Identify-Status darf die Kalibrier-Zeile nicht leeren.
        FanAssignRow row = IdentifyRow(canControl: true, new List<string>(), TimeSpan.FromMilliseconds(500));

        row.ApplyCalibration(new CalibrationStatus("hwmon7/pwm1", CalibrationPhase.Done, 0, 0,
            Running: false, Done: true, StartPwm: 96, FailReason: null));
        Assert.Contains("96", row.CalibrationProgress);

        row.ApplyIdentify(new IdentifyStatus("hwmon7/pwm1", Running: true, FailReason: null));
        Assert.True(row.IsIdentifying);
        Assert.Contains("100 %", row.IdentifyProgress);
        // Das gehaltene Kalibrier-Ergebnis bleibt unangetastet.
        Assert.Contains("96", row.CalibrationProgress);
    }

    [Fact]
    public void IsIdentifying_DisablesCalibrate_AndViceVersa()
    {
        FanAssignRow row = IdentifyRow(canControl: true, new List<string>());
        Assert.True(row.CanCalibrate);
        Assert.True(row.CanIdentify);

        row.ApplyIdentify(new IdentifyStatus("hwmon7/pwm1", Running: true, FailReason: null));
        Assert.False(row.CanCalibrate); // Kalibrieren während Identify gesperrt
        Assert.False(row.CanIdentify);

        row.ApplyIdentify(null);
        Assert.True(row.CanCalibrate);

        row.ApplyCalibration(new CalibrationStatus("hwmon7/pwm1", CalibrationPhase.Measuring, 120, 1500,
            Running: true, Done: false, StartPwm: null, FailReason: null));
        Assert.False(row.CanIdentify); // Identifizieren während Kalibrierung gesperrt
    }

    // --- Pro-Lüfter-Fortschritt + Fehler-Indikator (geteilte Kalibrier-Anzeige) ------------------

    [Fact]
    public void ApplyCalibration_Running_SetsFanProgress_FromPwm()
    {
        FanAssignRow row = CalibRow(canControl: true, new List<string>());

        row.ApplyCalibration(new CalibrationStatus("hwmon7/pwm1", CalibrationPhase.Measuring, 128, 1500,
            Running: true, Done: false, StartPwm: null, FailReason: null));

        Assert.InRange(row.CalibrationFanProgress, 49, 51); // 128/255 ≈ 50 %
        Assert.False(row.CalibrationFailed);
    }

    [Fact]
    public void ApplyCalibration_Done_FanProgressFull_NotFailed()
    {
        FanAssignRow row = CalibRow(canControl: true, new List<string>());

        row.ApplyCalibration(new CalibrationStatus("hwmon7/pwm1", CalibrationPhase.Done, 0, 0,
            Running: false, Done: true, StartPwm: 96, FailReason: null));

        Assert.Equal(100, row.CalibrationFanProgress);
        Assert.False(row.CalibrationFailed);
    }

    [Fact]
    public void ApplyCalibration_Error_SetsFailed_NewRunResetsIt()
    {
        FanAssignRow row = CalibRow(canControl: true, new List<string>());

        row.ApplyCalibration(new CalibrationStatus("hwmon7/pwm1", CalibrationPhase.Failed, 0, 0,
            Running: false, Done: false, StartPwm: null, FailReason: CalibrationFailReason.NoTacho));
        Assert.True(row.CalibrationFailed);

        // Ein neuer Lauf (bricht das Halten ab) löscht den Fehler-Indikator wieder.
        row.ApplyCalibration(new CalibrationStatus("hwmon7/pwm1", CalibrationPhase.Measuring, 64, 900,
            Running: true, Done: false, StartPwm: null, FailReason: null));
        Assert.False(row.CalibrationFailed);
    }

    [Fact]
    public async Task Error_ClearsFailedAndProgress_AfterHold()
    {
        FanAssignRow row = LatchRow(TimeSpan.FromMilliseconds(50));

        row.ApplyCalibration(ErrorStatus());
        Assert.True(row.CalibrationFailed);

        await WaitUntil(() => !row.CalibrationFailed);
        Assert.False(row.CalibrationFailed);
        Assert.Equal(0, row.CalibrationFanProgress);
    }

    // --- Tacho-Sensor-Kopplung ------------------------------------------------------------------

    private static ObservableCollection<TachSensorOption> TachOptions() => new()
    {
        new TachSensorOption(null, "- (keiner) -"),
        new TachSensorOption("hwmon7/fan1", "Fan 1"),
        new TachSensorOption("hwmon7/fan2", "Fan 2"),
    };

    private static FanAssignRow TachRow(
        bool canControl,
        List<string>? starts = null,
        List<int>? cancels = null,
        List<(string fanId, string? sensorId)>? sets = null,
        TimeSpan? hold = null,
        ObservableCollection<TachSensorOption>? tachs = null,
        string? rpmSource = null) =>
        new(new FanConfig { FanId = "hwmon7/pwm1", Name = "CPU Fan", RpmSource = rpmSource },
            selected: null, new ObservableCollection<CurveEditRow>(), canControl,
            sendCalibrate: null, calibrationHold: hold,
            sendTachMapping: starts is null ? null : id => { starts.Add(id); return Task.CompletedTask; },
            cancelTachMapping: cancels is null ? null : () => { cancels.Add(1); return Task.CompletedTask; },
            sendSetTach: sets is null ? null : (id, s) => { sets.Add((id, s)); return Task.CompletedTask; },
            availableTachSensors: tachs);

    private static TachMappingStatus TachRunning(string fanId = "hwmon7/pwm1") =>
        new(fanId, TachMappingPhase.Running, Running: true);

    [Fact]
    public async Task CoupleSensor_WhenControllable_SendsStartTachMapping()
    {
        var starts = new List<string>();
        FanAssignRow row = TachRow(canControl: true, starts: starts);

        await row.CoupleSensorCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "hwmon7/pwm1" }, starts);
    }

    [Fact]
    public async Task CoupleSensor_WhenNotControllable_DoesNothing()
    {
        var starts = new List<string>();
        FanAssignRow row = TachRow(canControl: false, starts: starts);

        await row.CoupleSensorCommand.ExecuteAsync(null);

        Assert.Empty(starts); // read-only Lüfter können nicht angetrieben werden → keine Kopplung
    }

    [Fact]
    public async Task CancelCoupleSensor_InvokesCancelDelegate()
    {
        var cancels = new List<int>();
        FanAssignRow row = TachRow(canControl: true, cancels: cancels);

        await row.CancelCoupleSensorCommand.ExecuteAsync(null);

        Assert.Single(cancels);
    }

    [Fact]
    public void ApplyTachMapping_Running_SetsRunningAndProgress()
    {
        FanAssignRow row = TachRow(canControl: true);

        row.ApplyTachMapping(TachRunning());

        Assert.True(row.IsTachMapping);
        Assert.Contains("Koppeln", row.TachMappingProgress);
    }

    [Fact]
    public void ApplyTachMapping_Matched_ShowsAssignedText_StopsRunning()
    {
        FanAssignRow row = TachRow(canControl: true, hold: TimeSpan.FromSeconds(5));

        row.ApplyTachMapping(new TachMappingStatus("hwmon7/pwm1", TachMappingPhase.Matched, Running: false,
            MatchedTachId: "hwmon7/fan1", RiseRpm: 800));

        Assert.False(row.IsTachMapping);
        Assert.Contains("zugeordnet", row.TachMappingProgress);
    }

    [Fact]
    public void ApplyTachMapping_NoResponse_ShowsNoSignalText()
    {
        FanAssignRow row = TachRow(canControl: true, hold: TimeSpan.FromSeconds(5));

        row.ApplyTachMapping(new TachMappingStatus("hwmon7/pwm1", TachMappingPhase.NoResponse, Running: false));

        Assert.False(row.IsTachMapping);
        Assert.Contains("Drehzahlsignal", row.TachMappingProgress);
    }

    [Fact]
    public void ApplyTachMapping_Ambiguous_ShowsAmbiguousText()
    {
        FanAssignRow row = TachRow(canControl: true, hold: TimeSpan.FromSeconds(5));

        row.ApplyTachMapping(new TachMappingStatus("hwmon7/pwm1", TachMappingPhase.Ambiguous, Running: false));

        Assert.False(row.IsTachMapping);
        Assert.Contains("eindeutig", row.TachMappingProgress);
    }

    [Fact]
    public void ApplyTachMapping_Failed_OverTemperature_ShowsReason()
    {
        FanAssignRow row = TachRow(canControl: true, hold: TimeSpan.FromSeconds(5));

        row.ApplyTachMapping(new TachMappingStatus("hwmon7/pwm1", TachMappingPhase.Failed, Running: false,
            FailReason: TachMappingFailReason.OverTemperature, OverTempC: 95, OverLimitC: 90));

        Assert.False(row.IsTachMapping);
        Assert.Contains("Übertemperatur", row.TachMappingProgress);
    }

    [Fact]
    public void ApplyTachMapping_OtherFanOrNull_ClearsInlineState()
    {
        FanAssignRow row = TachRow(canControl: true);
        row.ApplyTachMapping(TachRunning());
        Assert.True(row.IsTachMapping);

        row.ApplyTachMapping(TachRunning("other/pwm2"));
        Assert.False(row.IsTachMapping);
        Assert.Equal("", row.TachMappingProgress);

        row.ApplyTachMapping(TachRunning());
        Assert.True(row.IsTachMapping);

        row.ApplyTachMapping(null);
        Assert.False(row.IsTachMapping);
        Assert.Equal("", row.TachMappingProgress);
    }

    [Fact]
    public async Task ApplyTachMapping_Result_LatchesMessage_ThenAutoClearsAfterHold()
    {
        FanAssignRow row = TachRow(canControl: true, hold: TimeSpan.FromMilliseconds(80));

        row.ApplyTachMapping(new TachMappingStatus("hwmon7/pwm1", TachMappingPhase.NoResponse, Running: false));
        Assert.Contains("Drehzahlsignal", row.TachMappingProgress); // sofort sichtbar (gehalten)

        // Zwischendurch fremde/leere Snapshots - die Ergebnis-Meldung bleibt bis zum Ablauf.
        row.ApplyTachMapping(null);
        Assert.Contains("Drehzahlsignal", row.TachMappingProgress);

        await WaitUntil(() => row.TachMappingProgress == "");
        Assert.Equal("", row.TachMappingProgress);
    }

    [Fact]
    public void TachMappingAndCalibration_DoNotStompEachOthersState()
    {
        // Eigene Halte-Quelle pro Feature: ein Kopplungs-Ergebnis darf die gehaltene Kalibrier-Meldung nicht leeren.
        FanAssignRow row = TachRow(canControl: true, hold: TimeSpan.FromMilliseconds(500));

        row.ApplyCalibration(new CalibrationStatus("hwmon7/pwm1", CalibrationPhase.Done, 0, 0,
            Running: false, Done: true, StartPwm: 96, FailReason: null));
        Assert.Contains("96", row.CalibrationProgress);

        row.ApplyTachMapping(new TachMappingStatus("hwmon7/pwm1", TachMappingPhase.Matched, Running: false,
            MatchedTachId: "hwmon7/fan1"));
        Assert.Contains("zugeordnet", row.TachMappingProgress);
        // Das gehaltene Kalibrier-Ergebnis bleibt unangetastet.
        Assert.Contains("96", row.CalibrationProgress);
    }

    [Fact]
    public void IsTachMapping_DisablesCalibrateAndIdentify_AndViceVersa()
    {
        FanAssignRow row = TachRow(canControl: true);
        Assert.True(row.CanCalibrate);
        Assert.True(row.CanIdentify);
        Assert.True(row.CanCoupleSensor);

        row.ApplyTachMapping(TachRunning());
        Assert.False(row.CanCalibrate); // Kalibrieren während Kopplung gesperrt
        Assert.False(row.CanIdentify);  // Identifizieren während Kopplung gesperrt
        Assert.False(row.CanCoupleSensor);

        row.ApplyTachMapping(null);
        Assert.True(row.CanCoupleSensor);

        row.ApplyCalibration(new CalibrationStatus("hwmon7/pwm1", CalibrationPhase.Measuring, 120, 1500,
            Running: true, Done: false, StartPwm: null, FailReason: null));
        Assert.False(row.CanCoupleSensor); // Kopplung während Kalibrierung gesperrt

        row.ApplyCalibration(null);
        row.ApplyIdentify(new IdentifyStatus("hwmon7/pwm1", Running: true, FailReason: null));
        Assert.False(row.CanCoupleSensor); // Kopplung während Identify gesperrt
    }

    // --- Manuelle Tacho-Zuordnung (Dropdown) ----------------------------------------------------

    [Fact]
    public void Constructor_InitializesSelectedTach_FromRpmSource()
    {
        ObservableCollection<TachSensorOption> tachs = TachOptions();
        FanAssignRow row = TachRow(canControl: true, tachs: tachs, rpmSource: "hwmon7/fan2");

        Assert.Same(tachs[2], row.SelectedTach); // aktuelle Zuordnung aus der Config gespiegelt
    }

    [Fact]
    public void Constructor_NoRpmSource_SelectsNoneOption()
    {
        ObservableCollection<TachSensorOption> tachs = TachOptions();
        FanAssignRow row = TachRow(canControl: true, tachs: tachs); // rpmSource null

        Assert.Same(tachs[0], row.SelectedTach); // „- (keiner) -"
    }

    [Fact]
    public void SelectingTach_SendsSetFanTachometer_WithSensorId()
    {
        var sets = new List<(string, string?)>();
        ObservableCollection<TachSensorOption> tachs = TachOptions();
        FanAssignRow row = TachRow(canControl: true, sets: sets, tachs: tachs);

        row.SelectedTach = tachs[1]; // Fan 1

        Assert.Single(sets);
        Assert.Equal(("hwmon7/pwm1", "hwmon7/fan1"), sets[0]);
    }

    [Fact]
    public void SelectingNone_SendsSetFanTachometer_WithNull()
    {
        var sets = new List<(string, string?)>();
        ObservableCollection<TachSensorOption> tachs = TachOptions();
        // Mit vorhandener Zuordnung starten, damit der Wechsel auf „keiner" eine echte Änderung ist.
        FanAssignRow row = TachRow(canControl: true, sets: sets, tachs: tachs, rpmSource: "hwmon7/fan1");

        row.SelectedTach = tachs[0]; // „- (keiner) -" → null

        Assert.Single(sets);
        Assert.Equal(("hwmon7/pwm1", (string?)null), sets[0]);
    }

    [Fact]
    public void ApplyRpmSource_UpdatesSelection_WithoutSendingCommand()
    {
        var sets = new List<(string, string?)>();
        ObservableCollection<TachSensorOption> tachs = TachOptions();
        FanAssignRow row = TachRow(canControl: true, sets: sets, tachs: tachs); // rpmSource null

        row.ApplyRpmSource("hwmon7/fan1"); // Daemon meldet die (auto-gekoppelte) Zuordnung

        Assert.Same(tachs[1], row.SelectedTach);
        Assert.Empty(sets); // programmatische Spiegelung darf kein SetFanTachometer auslösen
    }

    [Fact]
    public void ApplyRpmSource_UnchangedValue_DoesNotClobberPendingUserSelection()
    {
        var sets = new List<(string, string?)>();
        ObservableCollection<TachSensorOption> tachs = TachOptions();
        FanAssignRow row = TachRow(canControl: true, sets: sets, tachs: tachs); // rpmSource null

        row.SelectedTach = tachs[1]; // Nutzer wählt Fan 1 → Command raus, Daemon noch nicht bestätigt
        Assert.Single(sets);

        row.ApplyRpmSource(null); // Snapshot noch mit altem (null) Wert → Auswahl nicht zurücksetzen
        Assert.Same(tachs[1], row.SelectedTach);
    }

    // --- IpcStatusText für die Kopplungs-Ergebnisse ---------------------------------------------

    [Fact]
    public void IpcStatusText_TachMapping_CoversResultPhases()
    {
        Assert.Contains("zugeordnet", IpcStatusText.TachMapping(
            new TachMappingStatus("f", TachMappingPhase.Matched, Running: false, MatchedTachId: "s")));
        Assert.Contains("Drehzahlsignal", IpcStatusText.TachMapping(
            new TachMappingStatus("f", TachMappingPhase.NoResponse, Running: false)));
        Assert.Contains("eindeutig", IpcStatusText.TachMapping(
            new TachMappingStatus("f", TachMappingPhase.Ambiguous, Running: false)));
        Assert.Contains("Übertemperatur", IpcStatusText.TachMapping(
            new TachMappingStatus("f", TachMappingPhase.Failed, Running: false,
                FailReason: TachMappingFailReason.OverTemperature, OverTempC: 95, OverLimitC: 90)));
        Assert.Equal("", IpcStatusText.TachMapping(
            new TachMappingStatus("f", TachMappingPhase.Running, Running: true))); // Running hat keinen Ergebnistext
    }

    [Fact]
    public void IpcStatusText_TachFail_Canceled_SharesBaseTextWithIdentify()
    {
        Assert.Equal(IpcStatusText.Fail(IdentifyFailReason.Canceled, null, null),
                     IpcStatusText.Fail(TachMappingFailReason.Canceled, null, null));
    }
}
