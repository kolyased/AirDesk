using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AirDesk;

public sealed class UserPreferences
{
    public Dictionary<string, string> Aliases { get; init; } = new(StringComparer.Ordinal);
    public List<SavedProfile> Profiles { get; init; } = new();
    public string? Gpu { get; set; }
    public Appearance Theme { get; set; } = Appearance.System;
    public bool StartMinimized { get; set; }
    public bool WelcomeDismissed { get; set; }
    public CardSetting[] Cards { get; set; } = DesktopLayout.DefaultCards();
    public TrayMetric[] Tooltip { get; set; } = DesktopLayout.DefaultTooltip();
    static readonly string Pathname = Path.Combine(LocalCalibration.Folder, "monitor-preferences-v1.json");

    public static UserPreferences Load()
    {
        try
        {
            if (!File.Exists(Pathname)) return new();
            var saved = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(Pathname));
            if (saved?.Aliases == null || saved.Profiles == null) return new();
            saved.Theme = Enum.IsDefined(saved.Theme) ? saved.Theme : Appearance.System;
            saved.Cards = DesktopLayout.NormalizeCards(saved.Cards);
            saved.Tooltip = (saved.Tooltip ?? DesktopLayout.DefaultTooltip()).Where(value => Enum.IsDefined(value)).Distinct().ToArray();
            saved.Profiles.RemoveAll(entry => entry?.Current == null || !Curve.Valid(entry.Current));
            for (int index = 0; index < saved.Profiles.Count; index++)
            {
                var entry = saved.Profiles[index];
                saved.Profiles[index] = entry with
                {
                    Previous = ValidOrNull(entry.Previous), Startup = ValidOrNull(entry.Startup), LastConfirmed = ValidOrNull(entry.LastConfirmed)
                };
            }
            return saved;
        }
        catch (Exception error) { Monitor.Log(error); return new(); }
    }

    public SavedProfile? Find(ChannelIdentity channel) => Profiles.FirstOrDefault(entry => entry.Channel == channel);

    static FanProfile? ValidOrNull(FanProfile? profile) => profile != null && Curve.Valid(profile) ? profile : null;

    public void SetProfile(ChannelIdentity channel, FanProfile profile, bool confirmed)
    {
        if (!Curve.Valid(profile)) throw new ArgumentException("Некорректный профиль.");
        var old = Find(channel);
        var next = old?.Select(profile, confirmed) ?? new SavedProfile(channel, profile, null, LastConfirmed: confirmed ? profile : null);
        if (old != next) StoreProfile(old, next);
    }

    public void SetStartupProfile(ChannelIdentity channel, FanProfile profile)
    {
        if (!Curve.Valid(profile)) throw new ArgumentException("Некорректный профиль по умолчанию.");
        var old = Find(channel);
        StoreProfile(old, (old ?? new SavedProfile(channel, profile, null)) with { Startup = profile });
    }

    void StoreProfile(SavedProfile? old, SavedProfile next)
    {
        var channel = next.Channel;
        Profiles.RemoveAll(entry => entry.Channel == channel);
        Profiles.Add(next);
        try { Save(); }
        catch
        {
            Profiles.RemoveAll(entry => entry.Channel == channel);
            if (old != null) Profiles.Add(old);
            throw;
        }
    }

    public void Save() => LocalCalibration.Write(Pathname, this);
    public string Label(string key, string original) => Aliases.TryGetValue(key, out string? alias) && !string.IsNullOrWhiteSpace(alias) ? alias : original;
}
