using System.Text.Json;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.Onboarding;

/// <summary>One release's notes, as carried in WhatsNew.json.</summary>
public sealed record ReleaseNotes(string Version, string Headline, IReadOnlyList<string> Items)
{
    public FirmwareVersion? ParsedVersion => FirmwareVersion.Parse(Version);
}

/// <summary>
/// The release notes shown after an update. Not onboarding: shown after an
/// update and never on a first run, and dismissing one never silences the
/// other. Port of the macOS Console's WhatsNew.
/// </summary>
public static class WhatsNew
{
    private sealed record Entry(string version, string headline, List<string> items);

    /// <summary>Every release in the file, newest first whatever its order.</summary>
    public static IReadOnlyList<ReleaseNotes> Parse(string json)
    {
        try
        {
            var entries = JsonSerializer.Deserialize<List<Entry>>(json) ?? new();
            return entries
                .Select(e => new ReleaseNotes(e.version, e.headline, e.items ?? new()))
                .OrderByDescending(r => r.ParsedVersion ?? new FirmwareVersion(0, 0, 0))
                .ToList();
        }
        catch (JsonException) { return Array.Empty<ReleaseNotes>(); }
    }

    /// <summary>
    /// Notes newer than the last shown, up to the installed version. Empty on a
    /// new install (nothing shown yet): someone seeing the app for the first
    /// time has nothing to catch up on. Capped at the installed version because
    /// the file can describe a release still being built.
    /// </summary>
    public static IReadOnlyList<ReleaseNotes> Unread(IReadOnlyList<ReleaseNotes> notes, string? lastShown, FirmwareVersion? current)
    {
        if (FirmwareVersion.Parse(lastShown) is not { } shown || current is not { } now) return Array.Empty<ReleaseNotes>();
        return notes.Where(r => r.ParsedVersion is { } v && v > shown && v <= now).ToList();
    }
}
