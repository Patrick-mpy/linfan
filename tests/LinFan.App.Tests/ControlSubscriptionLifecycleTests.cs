// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using LinFan.App.Controllers;
using LinFan.App.Controls;

namespace LinFan.App.Tests;

/// <summary>
/// Regression (2026-07-05 Review): <see cref="CurveChart"/> und <see cref="Sparkline"/> abonnierten die
/// <c>CollectionChanged</c>-Handler ihrer gebundenen Sammlung, lösten sie aber beim Detach aus dem
/// Visual-Tree nie wieder. Überlebt die Sammlung das Control (sie gehört dem Controller), hielt sie es
/// so am Leben. Diese Tests fahren einen Attach → Detach → Re-Attach-Zyklus headless und prüfen, dass
/// exakt ein Abo bestehen bleibt und nach dem Re-Attach kein zweites hinzukommt.
///
/// Die UI-Arbeit läuft über eine manuell gestartete <see cref="HeadlessUnitTestSession"/> (der
/// [AvaloniaFact]-Weg scheidet aus: Avalonia.Headless.XUnit 12.0.4 verlangt xunit.v3 — Konflikt mit
/// dem hier genutzten xunit 2.x). Die App-Definition liefert das assembly-weite
/// <c>[AvaloniaTestApplication]</c> in HeadlessTestApp.
/// </summary>
public sealed class ControlSubscriptionLifecycleTests
{
    /// <summary>Führt <paramref name="action"/> auf dem Headless-UI-Thread aus; Assertions darin propagieren.</summary>
    private static void OnUiThread(Action action)
    {
        HeadlessUnitTestSession session =
            HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ControlSubscriptionLifecycleTests).Assembly);
        session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Minimale beobachtbare Sammlung, die die Zahl ihrer CollectionChanged-Abonnenten offenlegt.</summary>
    private sealed class TrackingCollection<T> : IEnumerable<T>, INotifyCollectionChanged
    {
        private readonly List<T> _items = new();

        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        public int SubscriberCount => CollectionChanged?.GetInvocationList().Length ?? 0;

        public void Add(T item)
        {
            _items.Add(item);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Add, item, _items.Count - 1));
        }

        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static StackPanel ShownHost()
    {
        var host = new StackPanel();
        var window = new Window { Content = host, Width = 240, Height = 200 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return host;
    }

    [Fact]
    public void CurveChart_DetachReattach_KeepsExactlyOneSubscription() => OnUiThread(() =>
    {
        var points = new TrackingCollection<PointRow>();
        points.Add(new PointRow(20, 30));
        points.Add(new PointRow(60, 80));

        var chart = new CurveChart { Points = points };
        StackPanel host = ShownHost();

        host.Children.Add(chart); // Attach
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, points.SubscriberCount);

        host.Children.Remove(chart); // Detach
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, points.SubscriberCount);

        host.Children.Add(chart); // Re-Attach
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, points.SubscriberCount); // genau eins — kein Doppel-Abo

        // Nach dem Re-Attach ist der Handler wieder verdrahtet: eine Änderung läuft durch (InvalidateVisual),
        // ohne zu werfen. (Ein echter Pixel-Render braucht Skia; das Headless-Drawing genügt fürs Wiring.)
        points.Add(new PointRow(80, 100));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, points.SubscriberCount);
    });

    [Fact]
    public void CurveChart_SwappingPointsWhileAttached_MovesSubscription() => OnUiThread(() =>
    {
        var first = new TrackingCollection<PointRow>();
        first.Add(new PointRow(10, 10));
        var second = new TrackingCollection<PointRow>();
        second.Add(new PointRow(40, 50));

        var chart = new CurveChart { Points = first };
        StackPanel host = ShownHost();
        host.Children.Add(chart);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, first.SubscriberCount);

        chart.Points = second; // Sammlung im angehängten Zustand wechseln
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, first.SubscriberCount);  // altes Abo gelöst
        Assert.Equal(1, second.SubscriberCount); // neues Abo gesetzt
    });

    [Fact]
    public void Sparkline_DetachReattach_KeepsExactlyOneSubscription() => OnUiThread(() =>
    {
        var values = new TrackingCollection<double>();
        values.Add(1);
        values.Add(2);
        values.Add(3);

        var spark = new Sparkline { Values = values };
        StackPanel host = ShownHost();

        host.Children.Add(spark); // Attach
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, values.SubscriberCount);

        host.Children.Remove(spark); // Detach
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, values.SubscriberCount);

        host.Children.Add(spark); // Re-Attach
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, values.SubscriberCount);

        values.Add(4); // Handler verdrahtet, läuft ohne Wurf
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, values.SubscriberCount);
    });
}
