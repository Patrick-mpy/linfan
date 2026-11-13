// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Ipc.Messages;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// <see cref="HardwareChangeController"/> - the hardware dialog's cards follow the daemon's report, and each action sends
/// what it should: a keep without names changes nothing the editor holds, removing entries or clearing names rebuilds
/// the editor from the config that resolved the change (F5), and asks first when that would drop unsaved edits.
/// </summary>
public sealed class HardwareChangeControllerTests
{
    private static readonly HardwareChangeReading GpuReplaced = new("Replaced:gpu", HardwareChangeKind.Replaced,
        "amdgpu", "pci:1002:73bf:1002:0e3a", "amdgpu", "pci:1002:744c:1da2:e471",
        new[] { "amdgpu/pwm1" }, new[] { "amdgpu/temp1", "amdgpu/temp2" });

    private static readonly HardwareChangeReading NvmeMissing = new("Missing:nvme", HardwareChangeKind.Missing,
        "nvme", "nvme:Samsung SSD 980", null, null, new[] { "it87/pwm3" }, new[] { "nvme/temp1" });

    private static readonly HardwareChangeReading SsdReplaced = new("Replaced:ssd", HardwareChangeKind.Replaced,
        "nvme", "nvme:Samsung SSD 970", "nvme", "nvme:Samsung SSD 990", Array.Empty<string>(), new[] { "nvme/temp1" });

    /// <summary>The GPU's two sensors: the first with an own name, the second called by its hardware label.</summary>
    private static MonitorSnapshot Snap(params HardwareChangeReading[] changes) => new(
        "ok",
        new[]
        {
            new SensorReading("amdgpu/temp1", "GPU-Kern", SensorKind.Temperature, "°C", 50, Label: "edge"),
            new SensorReading("amdgpu/temp2", "junction", SensorKind.Temperature, "°C", 55, Label: "junction"),
            new SensorReading("nvme/temp1", "Composite", SensorKind.Temperature, "°C", 40, Label: "Composite"),
        },
        new[] { new FanReading("amdgpu/pwm1", "Lüfter 2", 1200, 100, CanControl: true) },
        new AppConfig
        {
            Fans = new[] { new FanConfig { FanId = "amdgpu/pwm1" }, new FanConfig { FanId = "it87/pwm3", Name = "Heck" } },
            Sensors = new[] { new SensorConfig { SensorId = "amdgpu/temp1", Name = "GPU-Kern" } },
        },
        Connected: true,
        HardwareChanges: changes);

    /// <summary>The controller's surroundings: sink, editor, fan rows and the view's side of the events.</summary>
    private sealed class Harness
    {
        public List<(string Key, bool Names)> Accepted { get; } = new();
        public List<string> Forgotten { get; } = new();
        public List<Func<MonitorSnapshot, bool>> Armed { get; } = new();
        public List<HardwareConfirmRequest> Asked { get; } = new();
        public Dictionary<string, FanRow> Rows { get; } = new();
        public int Cancelled { get; private set; }
        public int CloseRequests { get; private set; }
        public bool Reachable { get; set; } = true;
        public bool Unsaved { get; set; }

        public HardwareChangeController Controller()
        {
            var controller = new HardwareChangeController(
                (key, names) => { Accepted.Add((key, names)); return Task.FromResult(Reachable); },
                key => { Forgotten.Add(key); return Task.FromResult(Reachable); },
                until => { Armed.Add(until); return Task.FromResult(true); },
                () => Cancelled++,
                () => Unsaved,
                id => Rows.GetValueOrDefault(id));
            controller.ConfirmationRequested += (_, request) => Asked.Add(request);
            controller.CloseRequested += (_, _) => CloseRequests++;
            return controller;
        }

        public FanRow Row(string id, bool canControl = true)
        {
            var row = new FanRow(id, id);
            row.Update(new FanReading(id, id, 1200, 100, canControl));
            Rows[id] = row;
            return row;
        }
    }

    private static (HardwareChangeController Controller, Harness Harness) Open(params HardwareChangeReading[] changes)
    {
        var harness = new Harness();
        HardwareChangeController controller = harness.Controller();
        controller.Apply(Snap(changes));
        return (controller, harness);
    }

    // --- cards --------------------------------------------------------------------------------------

    [Fact]
    public void Cards_ShowWhatChanged_AndWhatItCovers()
    {
        var (controller, _) = Open(GpuReplaced, NvmeMissing);

        Assert.Equal(2, controller.Cards.Count);
        HardwareChangeCard gpu = controller.Cards[0];
        Assert.True(gpu.IsReplaced);
        Assert.Equal("amdgpu", gpu.Title);
        Assert.Equal("Getauscht", gpu.KindLabel);
        Assert.Equal(StatusTone.Warn, gpu.KindTone);
        Assert.Equal("Getauscht: amdgpu", gpu.AccessibleName);
        // Both are "amdgpu": only the fingerprints tell the two cards apart.
        Assert.Equal(("pci:1002:73bf:1002:0e3a", "pci:1002:744c:1da2:e471"), (gpu.OldDevice, gpu.NewDevice));
        Assert.Equal(new[] { "Lüfter 2" }, gpu.Fans.Select(f => f.Name));
        Assert.Equal("GPU-Kern, junction", gpu.Sensors);
        Assert.True(gpu.ShowStartPointNote);

        HardwareChangeCard nvme = controller.Cards[1];
        Assert.True(nvme.IsMissing);
        Assert.Equal("nvme", nvme.Title);
        Assert.Equal("Nicht gefunden", nvme.KindLabel);
        Assert.Equal(("", ""), (nvme.OldDevice, nvme.NewDevice));
        Assert.Equal(new[] { "Heck" }, nvme.Fans.Select(f => f.Name)); // not detected: the configured name
        Assert.Equal("Composite", nvme.Sensors);
        Assert.False(nvme.ShowStartPointNote);
    }

    [Fact]
    public void Devices_WithDifferentNames_AreShownByName_AnUnknownOneAsSuch_UnnamedEntriesById()
    {
        var windows = new HardwareChangeReading("Replaced:w", HardwareChangeKind.Replaced, "NVIDIA GeForce RTX 3080",
            "NVIDIA GeForce RTX 3080", "AMD Radeon RX 6800", "AMD Radeon RX 6800", Array.Empty<string>(), new[] { "/gpu/0/temp/0" });
        var unrecorded = new HardwareChangeReading("Missing:none", HardwareChangeKind.Missing, null, null, null, null,
            new[] { "hwmon9/pwm2" }, new[] { "hwmon9/temp\u00071" });

        var (controller, _) = Open(windows, unrecorded);

        Assert.Equal("AMD Radeon RX 6800", controller.Cards[0].Title);
        Assert.Equal(("NVIDIA GeForce RTX 3080", "AMD Radeon RX 6800"), (controller.Cards[0].OldDevice, controller.Cards[0].NewDevice));
        Assert.Equal("Unbekanntes Gerät", controller.Cards[1].Title);
        Assert.Equal(new[] { "hwmon9/pwm2" }, controller.Cards[1].Fans.Select(f => f.Name));
        Assert.Equal("hwmon9/temp1", controller.Cards[1].Sensors); // an id shown as a name loses unsafe characters
    }

    /// <summary>„Neu kalibrieren" is the fan table's own command - only for a fan of a replaced device that can be driven.</summary>
    [Fact]
    public void Recalibrate_IsTheFanRows_ForPresentControllableFansOfAReplacedDevice()
    {
        var harness = new Harness();
        FanRow gpuFan = harness.Row("amdgpu/pwm1");
        harness.Row("it87/pwm3"); // a row from before it went missing
        HardwareChangeController controller = harness.Controller();
        controller.Apply(Snap(GpuReplaced, NvmeMissing));

        Assert.Same(gpuFan, Assert.Single(controller.Cards[0].Fans).Fan);
        Assert.Equal("Lüfter 2 neu kalibrieren", controller.Cards[0].Fans[0].RecalibrateName);
        Assert.Null(Assert.Single(controller.Cards[1].Fans).Fan);

        harness.Row("amdgpu/pwm1", canControl: false);
        controller.Apply(Snap(GpuReplaced, NvmeMissing));
        Assert.Null(Assert.Single(controller.Cards[0].Fans).Fan);
    }

    [Fact]
    public void AResolvedChange_ItsCardGoes_AndWithNoneLeft_TheDialogAsksToClose()
    {
        var (controller, harness) = Open(GpuReplaced, NvmeMissing);
        HardwareChangeCard nvme = controller.Cards[1];

        controller.Apply(Snap(NvmeMissing));
        Assert.Same(nvme, Assert.Single(controller.Cards)); // kept, with its checkbox and focus
        Assert.Equal(0, harness.CloseRequests);

        controller.Apply(Snap());
        Assert.Empty(controller.Cards);
        Assert.Equal(1, harness.CloseRequests);
        controller.Apply(Snap());
        Assert.Equal(1, harness.CloseRequests);
    }

    [Fact]
    public void ANewChange_GetsItsCard_WhileTheDialogIsOpen_UnknownKindsNone()
    {
        var (controller, _) = Open(NvmeMissing);

        controller.Apply(Snap(GpuReplaced, NvmeMissing, GpuReplaced with { Key = "future", Kind = null }));

        Assert.Equal(new[] { "Replaced:gpu", "Missing:nvme" }, controller.Cards.Select(c => c.Key));
    }

    [Fact]
    public void Offline_KeepsTheCards_AndLocksTheirActions()
    {
        var (controller, _) = Open(GpuReplaced, NvmeMissing);
        HardwareChangeCard gpu = controller.Cards[0];

        controller.Apply(MonitorSnapshot.Unavailable("weg", ConnectionProblem.Unreachable));

        Assert.True(controller.IsOffline);
        Assert.Equal(2, controller.Cards.Count);
        Assert.False(gpu.AcceptCommand.CanExecute(null));
        Assert.False(gpu.ForgetCommand.CanExecute(null));
        Assert.False(controller.Cards[1].ForgetCommand.CanExecute(null));
        Assert.Equal("Gesperrt - der Hintergrunddienst ist nicht erreichbar.", gpu.ForgetTooltip);

        controller.Apply(Snap(GpuReplaced, NvmeMissing));
        Assert.False(controller.IsOffline);
        Assert.True(gpu.AcceptCommand.CanExecute(null));
        Assert.True(gpu.ForgetCommand.CanExecute(null));
    }

    /// <summary>
    /// The config tells an own name: the daemon clears one that is just the hardware label, so a name there is the user's
    /// - also where the snapshot carries no labels (an older daemon).
    /// </summary>
    [Fact]
    public void ResetNames_IsOffered_OnlyWhenASensorOfTheReplacedDeviceHasAnOwnName()
    {
        var (controller, _) = Open(GpuReplaced, SsdReplaced, NvmeMissing);

        Assert.True(controller.Cards[0].CanResetSensorNames);   // „GPU-Kern" is an own name
        Assert.False(controller.Cards[1].CanResetSensorNames);  // no own name: „Composite" is the label
        Assert.False(controller.Cards[2].CanResetSensorNames);  // nothing to accept for a missing device

        MonitorSnapshot noLabels = Snap(GpuReplaced) with
        {
            Sensors = Snap().Sensors.Select(s => s with { Label = null }).ToList(),
        };
        controller.Apply(noLabels);
        Assert.True(controller.Cards[0].CanResetSensorNames);

        MonitorSnapshot blankName = Snap(GpuReplaced) with
        {
            Config = Snap().Config with { Sensors = [new SensorConfig { SensorId = "amdgpu/temp1", Name = "  " }] },
        };
        controller.Apply(blankName);
        Assert.False(controller.Cards[0].CanResetSensorNames);  // the shown „GPU-Kern" alone is no own name
    }

    [Theory]
    [InlineData("calibration")]
    [InlineData("identify")]
    [InlineData("coupling")]
    public void ForgetIsLocked_WhileADeviceActionRuns_IfTheChangeCoversFans(string action)
    {
        var (controller, _) = Open(GpuReplaced, SsdReplaced);
        MonitorSnapshot reporting = Snap(GpuReplaced, SsdReplaced);
        MonitorSnapshot running = action switch
        {
            "calibration" => reporting with
            {
                Calibration = new CalibrationStatus("other", CalibrationPhase.Measuring, 100, 900, Running: true, Done: false,
                    StartPwm: null, FailReason: null),
            },
            "identify" => reporting with { Identify = new IdentifyStatus("other", Running: true, FailReason: null) },
            _ => reporting with { TachMapping = new TachMappingStatus("other", TachMappingPhase.Running, Running: true) },
        };

        controller.Apply(running);

        Assert.False(controller.Cards[0].ForgetCommand.CanExecute(null));
        Assert.Equal("Gesperrt - gerade läuft eine Kalibrierung, Identifikation oder Tacho-Kopplung.", controller.Cards[0].ForgetTooltip);
        Assert.True(controller.Cards[0].AcceptCommand.CanExecute(null)); // the daemon refuses only a removal
        Assert.True(controller.Cards[1].ForgetCommand.CanExecute(null)); // sensors only: nothing to refuse

        controller.Apply(reporting); // the action ended
        Assert.True(controller.Cards[0].ForgetCommand.CanExecute(null));
    }

    // --- keep (accept) ------------------------------------------------------------------------------

    /// <summary>The device is daemon-only - the editor holds nothing a plain keep changes, so it needs no rebuild.</summary>
    [Fact]
    public async Task Accept_SendsTheKey_WithoutNames_AndWithoutARebuild()
    {
        var (controller, harness) = Open(GpuReplaced);
        HardwareChangeCard gpu = controller.Cards[0];

        await gpu.AcceptCommand.ExecuteAsync(null);

        Assert.Equal(new[] { ("Replaced:gpu", false) }, harness.Accepted);
        Assert.Empty(harness.Armed);
        Assert.Empty(harness.Asked);
        Assert.True(gpu.IsPending);
        Assert.Equal("Wartet auf den Hintergrunddienst …", gpu.Note);
        Assert.False(gpu.AcceptCommand.CanExecute(null));
    }

    [Fact]
    public async Task Accept_WithResetNames_ClearsThemToo_AndRebuildsTheEditorOnceTheChangeIsResolved()
    {
        var (controller, harness) = Open(GpuReplaced, NvmeMissing);
        controller.Cards[0].ResetSensorNames = true;

        await controller.Cards[0].AcceptCommand.ExecuteAsync(null);

        Assert.Equal(new[] { ("Replaced:gpu", true) }, harness.Accepted);
        Func<MonitorSnapshot, bool> until = Assert.Single(harness.Armed);
        Assert.False(until(Snap(GpuReplaced, NvmeMissing))); // a config change that overtakes it does not count
        Assert.True(until(Snap(NvmeMissing)));
    }

    [Fact]
    public async Task Accept_TheCheckboxCountsOnlyWhileOffered()
    {
        var (controller, harness) = Open(SsdReplaced);
        controller.Cards[0].ResetSensorNames = true; // not offered: no own name

        await controller.Cards[0].AcceptCommand.ExecuteAsync(null);

        Assert.Equal(new[] { ("Replaced:ssd", false) }, harness.Accepted);
        Assert.Empty(harness.Armed);
    }

    /// <summary>The rebuild that follows clearing the names would drop unsaved edits - asked first (F5).</summary>
    [Fact]
    public async Task Accept_WithResetNames_AndUnsavedChanges_AsksFirst()
    {
        var (controller, harness) = Open(GpuReplaced);
        harness.Unsaved = true;
        controller.Cards[0].ResetSensorNames = true;

        await controller.Cards[0].AcceptCommand.ExecuteAsync(null);

        HardwareConfirmRequest asked = Assert.Single(harness.Asked);
        Assert.Empty(harness.Accepted);
        Assert.Empty(harness.Armed);
        Assert.Equal("Sensornamen zurücksetzen?", asked.Title);
        Assert.EndsWith("Deine ungespeicherten Änderungen werden dabei verworfen.", asked.Message);
        Assert.Equal("Verwerfen & übernehmen", asked.ConfirmLabel);

        await controller.ConfirmedAsync(asked);

        Assert.Equal(new[] { ("Replaced:gpu", true) }, harness.Accepted);
        Assert.Single(harness.Armed);
    }

    [Fact]
    public async Task Accept_WithoutNames_AndUnsavedChanges_DoesNotAsk()
    {
        var (controller, harness) = Open(GpuReplaced);
        harness.Unsaved = true;

        await controller.Cards[0].AcceptCommand.ExecuteAsync(null);

        Assert.Empty(harness.Asked);
        Assert.Single(harness.Accepted);
    }

    // --- remove (forget) ----------------------------------------------------------------------------

    [Fact]
    public async Task SetUpAsNew_AsksFirst_ThenRemovesTheEntries_AndRebuildsTheEditor()
    {
        var (controller, harness) = Open(GpuReplaced, NvmeMissing);

        controller.Cards[0].ForgetCommand.Execute(null);

        HardwareConfirmRequest asked = Assert.Single(harness.Asked);
        Assert.Empty(harness.Forgotten);
        Assert.Equal("Als neues Gerät einrichten?", asked.Title);
        Assert.Equal("Namen, Kurvenzuordnungen und Kalibrierungen dieser Einträge werden entfernt. Die Lüfter laufen auf "
                     + "Hardware-Auto, bis sie wieder eine Kurve bekommen - kalibriere sie danach neu. Lässt sich nicht rückgängig "
                     + "machen.", asked.Message); // no unsaved changes: nothing about them
        Assert.Equal("Neu einrichten", asked.ConfirmLabel);

        await controller.ConfirmedAsync(asked);

        Assert.Equal(new[] { "Replaced:gpu" }, harness.Forgotten);
        Func<MonitorSnapshot, bool> until = Assert.Single(harness.Armed);
        Assert.False(until(Snap(GpuReplaced, NvmeMissing)));
        Assert.True(until(Snap(NvmeMissing)));
    }

    [Fact]
    public void SetUpAsNew_ASensorsOnlyDevice_SaysWhatItsSensorsLose()
    {
        var (controller, harness) = Open(SsdReplaced);

        controller.Cards[0].ForgetCommand.Execute(null);

        Assert.StartsWith("Die gespeicherten Namen und Gruppen dieser Sensoren werden entfernt", Assert.Single(harness.Asked).Message);
    }

    /// <summary>A device that is only missing for now - another program blocking access on Windows - would lose it all.</summary>
    [Fact]
    public async Task RemoveEntries_WarnsAboutATemporarilyMissingDevice_AndUnsavedChanges()
    {
        var (controller, harness) = Open(NvmeMissing);
        harness.Unsaved = true;

        controller.Cards[0].ForgetCommand.Execute(null);

        HardwareConfirmRequest asked = Assert.Single(harness.Asked);
        Assert.Equal("Einträge entfernen?", asked.Title);
        Assert.Contains("etwa weil unter Windows ein anderes Programm den Hardware-Zugriff blockiert", asked.Message);
        Assert.EndsWith("\n\nDeine ungespeicherten Änderungen werden dabei verworfen.", asked.Message);
        Assert.Equal("Entfernen", asked.ConfirmLabel);

        await controller.ConfirmedAsync(asked);
        Assert.Equal(new[] { "Missing:nvme" }, harness.Forgotten);
        Assert.Single(harness.Armed);
    }

    [Fact]
    public async Task AConfirmation_ForACardThatWentMeanwhile_SendsNothing()
    {
        var (controller, harness) = Open(GpuReplaced);
        controller.Cards[0].ForgetCommand.Execute(null);

        controller.Apply(Snap()); // resolved elsewhere while the user was asked
        await controller.ConfirmedAsync(Assert.Single(harness.Asked));

        Assert.Empty(harness.Forgotten);
        Assert.Empty(harness.Armed);
    }

    [Fact]
    public async Task ACommandThatDoesNotGoOut_DisarmsTheRebuild_AndSaysSo()
    {
        var (controller, harness) = Open(NvmeMissing);
        harness.Reachable = false;
        controller.Cards[0].ForgetCommand.Execute(null);

        await controller.ConfirmedAsync(Assert.Single(harness.Asked));

        Assert.Single(harness.Armed);
        Assert.Equal(1, harness.Cancelled);
        Assert.False(controller.Cards[0].IsPending);
        Assert.Equal("Hintergrunddienst nicht erreichbar - nichts geändert.", controller.Cards[0].Note);
        Assert.True(controller.Cards[0].ForgetCommand.CanExecute(null)); // can be tried again
    }

    /// <summary>Two changes sent one after the other: the editor rebuilds once neither is reported any more.</summary>
    [Fact]
    public async Task TwoChangesSent_TheRebuildWaitsForBoth()
    {
        var (controller, harness) = Open(GpuReplaced, NvmeMissing);
        controller.Cards[0].ResetSensorNames = true;
        await controller.Cards[0].AcceptCommand.ExecuteAsync(null);
        controller.Cards[1].ForgetCommand.Execute(null);
        await controller.ConfirmedAsync(Assert.Single(harness.Asked));

        Func<MonitorSnapshot, bool> until = harness.Armed[^1];
        Assert.False(until(Snap(NvmeMissing)));
        Assert.False(until(Snap(GpuReplaced)));
        Assert.True(until(Snap()));
    }

    // --- waiting for the daemon ---------------------------------------------------------------------

    /// <summary>
    /// The daemon answers only by no longer reporting the change. If it stays (refused, see the service log), the card
    /// offers its actions again - counted in fresh snapshots: the GUI applies the last one every second.
    /// </summary>
    [Fact]
    public async Task AChangeThatStays_UnlocksItsCard_AfterTheGrace()
    {
        var (controller, _) = Open(GpuReplaced);
        HardwareChangeCard gpu = controller.Cards[0];
        await gpu.AcceptCommand.ExecuteAsync(null);

        MonitorSnapshot repeated = Snap(GpuReplaced);
        for (int i = 0; i < HardwareChangeController.PendingTicks * 2; i++)
            controller.Apply(repeated); // one tick, applied again and again: counts once
        Assert.True(gpu.IsPending);

        for (int i = 2; i < HardwareChangeController.PendingTicks; i++)
            controller.Apply(Snap(GpuReplaced));
        Assert.True(gpu.IsPending); // one fresh tick short of the grace

        controller.Apply(Snap(GpuReplaced));
        Assert.False(gpu.IsPending);
        Assert.Equal("Der Hintergrunddienst hat die Änderung nicht übernommen - Details im Dienst-Log.", gpu.Note);
        Assert.True(gpu.AcceptCommand.CanExecute(null));
    }

    [Fact]
    public async Task ADroppedConnection_EndsTheWait()
    {
        var (controller, _) = Open(GpuReplaced);
        await controller.Cards[0].AcceptCommand.ExecuteAsync(null);

        controller.Apply(MonitorSnapshot.Unavailable("weg", ConnectionProblem.Unreachable));
        Assert.False(controller.Cards[0].IsPending);

        controller.Apply(Snap(GpuReplaced));
        Assert.True(controller.Cards[0].AcceptCommand.CanExecute(null));
    }

    // --- keep (missing) and closing -----------------------------------------------------------------

    [Fact]
    public void Keep_SendsNothing_AndTheCardLeavesThisDialog()
    {
        var (controller, harness) = Open(GpuReplaced, NvmeMissing);

        controller.Cards[1].KeepCommand.Execute(null);
        controller.Apply(Snap(GpuReplaced, NvmeMissing)); // still reported - still kept out

        Assert.Equal(new[] { "Replaced:gpu" }, controller.Cards.Select(c => c.Key));
        Assert.Empty(harness.Forgotten);
        Assert.Empty(harness.Accepted);
        Assert.Equal(0, harness.CloseRequests);
    }

    [Fact]
    public void Keep_OnTheLastCard_ClosesTheDialog()
    {
        var (controller, harness) = Open(NvmeMissing);

        controller.Cards[0].KeepCommand.Execute(null);

        Assert.Equal(1, harness.CloseRequests);
    }
}
