using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AirDesk;

public record SavedProfile(ChannelIdentity Channel, FanProfile Current, FanProfile? Previous);

public sealed class UserPreferences
{
    public Dictionary<string, string> Aliases { get; init; } = new(StringComparer.Ordinal);
    public List<SavedProfile> Profiles { get; init; } = new();
    public string? Gpu { get; set; }
    static readonly string Pathname = Path.Combine(LocalCalibration.Folder, "monitor-preferences-v1.json");

    public static UserPreferences Load()
    {
        try
        {
            if (!File.Exists(Pathname)) return new();
            var saved = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(Pathname));
            if (saved?.Aliases == null || saved.Profiles == null) return new();
            saved.Profiles.RemoveAll(entry => entry?.Current == null || !Curve.Valid(entry.Current));
            return saved;
        }
        catch (Exception error) { Monitor.Log(error); return new(); }
    }

    public SavedProfile? Find(ChannelIdentity channel) => Profiles.FirstOrDefault(entry => entry.Channel == channel);

    public void SetProfile(ChannelIdentity channel, FanProfile profile)
    {
        if (!Curve.Valid(profile)) throw new ArgumentException("Некорректный профиль.");
        var old = Find(channel);
        if (old?.Current == profile) return;
        Profiles.RemoveAll(entry => entry.Channel == channel);
        Profiles.Add(new(channel, profile, old?.Current));
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
