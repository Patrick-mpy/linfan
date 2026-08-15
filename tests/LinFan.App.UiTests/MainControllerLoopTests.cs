// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LinFan.App.Controllers;

namespace LinFan.App.UiTests;

/// <summary>
/// Tests für den abbrechbaren/injizierbaren Poll-Loop des <see cref="MainController"/>:
/// das Intervall ist injizierbar und <see cref="MainController.Dispose"/> stoppt die Schleife.
/// </summary>
public class MainControllerLoopTests
{
    [AvaloniaFact]
    public void PollLoop_IsCancelable_StopsReadingAfterDispose()
    {
        var fake = new FakeLiveMonitor(UiTestHelpers.SampleSnapshot());
        // Kurzes (injiziertes) Intervall → die Schleife iteriert mehrfach.
        var ctrl = new MainController(fake, pollInterval: TimeSpan.FromMilliseconds(10));

        UiTestHelpers.PumpUntil(() => fake.ReadCount >= 3);
        Assert.True(fake.ReadCount >= 3); // Loop läuft (und das injizierte Intervall greift)

        ctrl.Dispose();                                    // bricht den Loop ab
        UiTestHelpers.PumpUntil(() => false, timeoutMs: 60);   // in-flight Read abklingen lassen
        int afterDispose = fake.ReadCount;

        // Ohne Abbruch kämen bei 10 ms-Intervall in 250 ms ~25 weitere Reads - nach Dispose darf es keiner sein.
        UiTestHelpers.PumpUntil(() => false, timeoutMs: 250);
        Assert.Equal(afterDispose, fake.ReadCount);

        ctrl.Dispose(); // idempotent - darf nicht werfen
    }
}
