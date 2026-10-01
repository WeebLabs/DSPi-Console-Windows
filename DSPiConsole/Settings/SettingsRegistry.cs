using System;
using System.Collections.Generic;
using DSPiConsole.Settings.Pages;

namespace DSPiConsole.Settings;

/// <summary>
/// Single source of truth for which Settings pages exist and in what
/// order. The <see cref="SettingsShell"/> consumes this list to populate
/// the NavigationView; nothing else should hardcode the page list.
///
/// <para>
/// Pages are constructed lazily on first <see cref="Pages"/> access,
/// not at class-init time. Each factory runs inside a try/catch — if
/// a page's XAML fails to parse (a misformed glyph, a removed control,
/// a missing converter) we log the failure to
/// <c>%LOCALAPPDATA%\DSPiConsole\settings-crash.log</c> and skip it.
/// The rest of the registry still loads.
/// </para>
///
/// <para>
/// Adding a new page: append a <c>TryAdd&lt;NewPage&gt;(pages)</c>
/// call in <see cref="Build"/> at the appropriate position. The
/// generic constraint requires the new class to implement
/// <see cref="ISettingsPage"/> and have a parameterless constructor.
/// </para>
/// </summary>
internal static class SettingsRegistry
{
    /// <summary>All registered pages, in declaration order. Pages whose
    /// constructor threw are silently omitted (with a crash-log entry).
    /// Computed lazily on first access; the value is cached for the
    /// lifetime of the process.</summary>
    public static IReadOnlyList<ISettingsPage> Pages => _pages.Value;

    private static readonly Lazy<IReadOnlyList<ISettingsPage>> _pages = new(Build);

    private static IReadOnlyList<ISettingsPage> Build()
    {
        var pages = new List<ISettingsPage>();

        // ── General ────────────────────────────────────────────────
        TryAdd<GeneralVolumePage>(pages);

        // ── Graphing ───────────────────────────────────────────────
        TryAdd<GraphingStylePage>(pages);
        TryAdd<GraphingScalePage>(pages);
        TryAdd<GraphingGridLabelsPage>(pages);
        TryAdd<GraphingSpectrumPage>(pages);

        // ── System ─────────────────────────────────────────────────
        TryAdd<HardwareOverviewPage>(pages);
        TryAdd<HardwareOutputAssignmentPage>(pages);
        TryAdd<HardwareMasterClockPage>(pages);
        TryAdd<HardwareAdatPage>(pages);
        TryAdd<HardwareI2SPage>(pages);
        TryAdd<HardwareSpdifInputPage>(pages);

        // ── Control ────────────────────────────────────────────────
        TryAdd<ControlMacrosPage>(pages);
        TryAdd<ControlGroupsPage>(pages);
        TryAdd<ControlSurfacesPage>(pages);
        TryAdd<HardwareControlInterfacesPage>(pages);
        TryAdd<HardwareDacMutePage>(pages);

        // ── Presets ────────────────────────────────────────────────
        // Startup + output-config (Inclusion) moved to General › Globals.
        TryAdd<PresetsUIPage>(pages);

        // ── Advanced ───────────────────────────────────────────────
        TryAdd<AdvancedDebugPage>(pages);

        // ── About (rendered in FooterMenuItems) ────────────────────
        TryAdd<AboutPage>(pages);

        return pages;
    }

    /// <summary>Construct a page via its parameterless ctor and add it
    /// to the list, swallowing any constructor failure. A failure here
    /// almost always means the page's XAML failed to parse at runtime
    /// — we log the full exception (with the page type's name) and
    /// continue, so one broken page doesn't take down the whole shell.</summary>
    private static void TryAdd<TPage>(List<ISettingsPage> list)
        where TPage : ISettingsPage, new()
    {
        try
        {
            list.Add(new TPage());
        }
        catch (Exception ex)
        {
            SettingsWindow.WriteCrashLog($"SettingsRegistry: failed to construct {typeof(TPage).Name}", ex);
        }
    }
}
