using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AirDesk;

public sealed class UserPreferences
{
    const int CurrentSchema = 2;
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, string> Aliases { get; init; } = new(StringComparer.Ordinal);
    public List<SavedProfile> Profiles { get; init; } = new();
    public string? Gpu { get; set; }
    public string? GpuInstance { get; set; }
    public string? GpuName { get; set; }
    public Appearance Theme { get; set; } = Appearance.System;
    public bool StartMinimized { get; set; }
    public bool WelcomeDismissed { get; set; }
    public CardSetting[] Cards { get; set; } = DesktopLayout.DefaultCards();
    public TrayMetric[] Tooltip { get; set; } = DesktopLayout.DefaultTooltip();
    public HistoryRetention Retention { get; set; }
    public SensorReference[]? HistorySensors { get; set; }
    public ThresholdRule[] Rules { get; set; } = [];
    public FrameOverlayOptions Overlay { get; set; } = new();
    static readonly string DefaultPath = Path.Combine(AtomicJson.Folder, "monitor-preferences-v1.json");
    string path = DefaultPath;
    [JsonIgnore] public string? RecoveryNotice { get; private set; }
    bool loadedFromBackup, corruptPrimary, incompatibleSchema;
    const int MaximumBytes = 4 * 1024 * 1024;

    public static UserPreferences Load(string? filename = null, Action<Exception>? report = null)
    {
        string pathname = filename ?? DefaultPath;
        report ??= error => DiagnosticLog.Write(error.ToString());
        if (!File.Exists(pathname) && !File.Exists(pathname + ".bak")) return new() { path = pathname };
        try { var value = Read(pathname); value.path = pathname; return value; }
        catch (NotSupportedException error)
        {
            report(error);
            return new() { path = pathname, incompatibleSchema = true, RecoveryNotice = "Файл настроек создан другой версией AirDesk. Изменения не сохраняются в этот файл." };
        }
        catch (Exception mainError)
        {
            report(mainError);
            try
            {
                var copy = Read(pathname + ".bak");
                copy.path = pathname;
                copy.loadedFromBackup = true;
                copy.RecoveryNotice = "Настройки загружены из резервной копии. Следующее сохранение восстановит основной файл.";
                return copy;
            }
            catch (Exception backupError)
            {
                report(backupError);
                return new() { path = pathname, corruptPrimary = true, RecoveryNotice = "Не удалось прочитать настройки и резервную копию. Загружены значения по умолчанию." };
            }
        }
    }

    static UserPreferences Read(string path)
    {
            if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("Слишком большой файл настроек.");
            var saved = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllBytes(path));
            if (saved == null) throw new InvalidDataException("Повреждена структура настроек.");
            if (saved.SchemaVersion is < 1 or > CurrentSchema) throw new NotSupportedException("Неизвестная версия настроек.");
            if (saved.Aliases == null || saved.Profiles == null) throw new InvalidDataException("Повреждена структура настроек.");
            saved.SchemaVersion = CurrentSchema;
            saved.Aliases.Keys.Where(key => key.Length > 512 || string.IsNullOrWhiteSpace(saved.Aliases[key]) || saved.Aliases[key].Length > 60)
                .ToList().ForEach(key => saved.Aliases.Remove(key));
            saved.Theme = Enum.IsDefined(saved.Theme) ? saved.Theme : Appearance.System;
            saved.Cards = DesktopLayout.NormalizeCards(saved.Cards);
            saved.Tooltip = (saved.Tooltip ?? DesktopLayout.DefaultTooltip()).Where(value => Enum.IsDefined(value)).Distinct().ToArray();
            saved.Retention = Enum.IsDefined(saved.Retention) ? saved.Retention : HistoryRetention.Off;
            saved.HistorySensors = saved.HistorySensors?.Where(sensor => sensor is { Id.Length: > 0, DeviceId.Length: > 0, DeviceName.Length: > 0 })
                .Distinct().ToArray();
            saved.Rules = (saved.Rules ?? []).Where(rule => rule?.Valid == true).DistinctBy(rule => rule.Id).Take(32).ToArray();
            saved.Overlay = (saved.Overlay ?? new()).Normalize();
            saved.Profiles.RemoveAll(entry => entry?.Channel == null || entry.Current == null || !Curve.Valid(entry.Current));
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

    public void Save()
    {
        if (incompatibleSchema) throw new InvalidOperationException("Версия файла настроек новее поддерживаемой.");
        SchemaVersion = CurrentSchema;
        if (corruptPrimary && File.Exists(path)) File.Copy(path, path + ".corrupt", overwrite: true);
        AtomicJson.Write(path, this, backup: !loadedFromBackup && !corruptPrimary);
        loadedFromBackup = corruptPrimary = false;
        RecoveryNotice = null;
    }

    public void Export(string destination)
    {
        AtomicJson.RequireExternalDestination(destination);
        string target = Path.GetFullPath(destination);
        if (new[] { path, path + ".bak", path + ".corrupt" }.Any(item => Path.GetFullPath(item).Equals(target, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Выберите файл экспорта отдельно от файлов настроек.");
        if (incompatibleSchema || corruptPrimary && File.Exists(path)) File.Copy(path, destination, overwrite: true);
        else AtomicJson.Write(destination, this);
    }

    public void Reset() => AtomicJson.Write(path, new UserPreferences { SchemaVersion = CurrentSchema }, backup: true);
    public string Label(string key, string original) => Aliases.TryGetValue(key, out string? alias) && !string.IsNullOrWhiteSpace(alias) ? alias : original;

    internal static string DeviceAliasKey(string id, string? instanceId) => instanceId == null ? id : Telemetry.GpuInstancePrefix + instanceId;

    public string DeviceLabel(string id, string original, string? instanceId = null) =>
        Label(DeviceAliasKey(id, instanceId), Label(id, original));

    public DeviceReadings? SelectedGpu(Telemetry telemetry) => telemetry.PrimaryGpu(Gpu, GpuInstance, GpuName);

    public void SelectGpu(DeviceReadings? device)
    {
        var previous = (Gpu, GpuInstance, GpuName);
        (Gpu, GpuInstance, GpuName) = (device?.Id, device?.InstanceId, device?.Name);
        if (previous == (Gpu, GpuInstance, GpuName)) return;
        try { Save(); }
        catch { (Gpu, GpuInstance, GpuName) = previous; throw; }
    }

    public void SetAlias(string key, string name, string? legacyKey = null)
    {
        name = name.Trim();
        if (name.Length > 60) throw new ArgumentException("Название должно быть не длиннее 60 символов.", nameof(name));
        string[] keys = legacyKey == null || key == legacyKey ? [key] : [key, legacyKey];
        var previous = keys.Where(Aliases.ContainsKey).ToDictionary(value => value, value => Aliases[value]);
        foreach (string value in keys) Aliases.Remove(value);
        if (name.Length > 0) Aliases[key] = name;
        try { Save(); }
        catch
        {
            Aliases.Remove(key);
            foreach (var entry in previous) Aliases[entry.Key] = entry.Value;
            throw;
        }
    }
}
