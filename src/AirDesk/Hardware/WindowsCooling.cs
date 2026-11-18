using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace AirDesk;

internal static class ControllerProcesses
{
    public static string? Scan()
    {
        try
        {
            string[] names = Process.GetProcesses().Select(process =>
            {
                using (process)
                {
                    try { return ControllerNames.Match(process.ProcessName); }
                    catch (InvalidOperationException) { return null; } // Process exited during enumeration.
                }
            }).OfType<string>().Distinct().Order().ToArray();
            return names.Length == 0 ? null : $"Управление недоступно: обнаружен {string.Join(", ", names)}. Завершите его, включая трей или службу. Захват канала этим ПО не подтверждён.";
        }
        catch (Exception error)
        {
            Monitor.Log(error);
            return "Не удалось проверить запущенные контроллеры. Управление заблокировано.";
        }
    }

}

internal sealed class LocalCalibration : ICalibrationStore
{
    readonly string profile = Path.Combine(AtomicJson.Folder, "fan-binding-v1.json");
    const string Journal = @"SOFTWARE\AirDesk\Cooling";
    const string Pending = "PendingCaptureV1";
    public Calibration? Saved { get; private set; }
    public bool RecoveryPending { get; private set; }

    public LocalCalibration()
    {
        using var state = Registry.LocalMachine.OpenSubKey(Journal);
        RecoveryPending = state?.GetValue(Pending) != null;
        try { if (File.Exists(profile)) Saved = JsonSerializer.Deserialize<Calibration>(File.ReadAllText(profile)); }
        catch (Exception error) { Monitor.Log(error); }
    }

    public void Save(Calibration value) { AtomicJson.Write(profile, value); Saved = value; }
    public void ClearBinding() { File.Delete(profile); Saved = null; }
    public void MarkCapture(ChannelIdentity identity)
    {
        using var state = Registry.LocalMachine.CreateSubKey(Journal) ?? throw new IOException("Журнал восстановления недоступен.");
        if (state.GetValue(Pending) != null)
        {
            RecoveryPending = true;
            throw new InvalidOperationException("Существует незавершённый захват. Проверьте BIOS/EC.");
        }
        state.SetValue(Pending, JsonSerializer.Serialize(identity), RegistryValueKind.String);
        RecoveryPending = true;
        state.Flush();
    }

    public void ClearCapture()
    {
        using var state = Registry.LocalMachine.OpenSubKey(Journal, writable: true);
        if (state == null && RecoveryPending) throw new IOException("Нет доступа к журналу восстановления.");
        state?.DeleteValue(Pending, throwOnMissingValue: false);
        state?.Flush();
        RecoveryPending = false;
    }
}

internal sealed class WindowsCooling : IFanController, IDisposable
{
    const string ChipId = "/lpc/nct6779d/0";
    const string ControlId = ChipId + "/control/1";
    const string TachometerId = ChipId + "/fan/1";
    const string TemperatureId = "/intelcpu/0/temperature/8";
    const string BusName = @"Global\Access_ISABUS.HTP.Method";
    Mutex? writer;
    Mutex? bus;
    Hardware? chip;
    ISensor? power;
    (byte Mode, byte Command) original;
    Action<int, byte?>? setControl;
    byte? lastRawCommand;
    bool leased, softwareAttempted;
    public bool BoardUpdateBlocked { get; private set; }
    public string? ReadIssue { get; private set; }
    public bool RetryDiscovery { get; private set; }
    public ChannelIdentity? Identity { get; private set; }

    public void Discover(IHardware[] hardware)
    {
        RetryDiscovery = false;
        string library = typeof(Computer).Assembly.GetName().Version?.ToString() ?? "?";
        using var board = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        string Field(string name) => (board?.GetValue(name) as string)?.Trim() ?? "";
        string vendor = Field("BaseBoardManufacturer"), model = Field("BaseBoardProduct"), revision = Field("BaseBoardVersion"), firmware = Field("BIOSVersion");
        DriverStatus driverStatus = DriverStatus.Read();
        string? driver = driverStatus.Version;
        chip = hardware.OfType<Hardware>().FirstOrDefault(h => h.HardwareType == HardwareType.SuperIO && h.Identifier.ToString() == ChipId);
        if (driver == null) ReadIssue = driverStatus.Description;
        else if (!driverStatus.Elevated) ReadIssue = "Недостаточно прав администратора. Для управления откройте AirDesk.exe через «Запуск от имени администратора».";
        else if (vendor != "INTEL" || model != "X99-XD4" || revision != "V2.0" || firmware != "5.11" ||
            library != "0.9.6.0" || driver != "2.2.0")
            ReadIssue = $"Только мониторинг. Нет проверенного определения для {vendor} {model} {revision}, BIOS {firmware}, PawnIO {driver}.";
        else if (chip == null) ReadIssue = "Контроллер Nuvoton NCT6779D недоступен. Проверьте доступ драйвера PawnIO.";
        else if (bus == null && !Mutex.TryOpenExisting(BusName, out bus))
        { ReadIssue = "Блокировка аппаратной шины недоступна."; RetryDiscovery = true; }
        else if (BindBackend(chip) is not { } write) ReadIssue = "Для этой версии библиотеки не подтверждены операции записи и возврата.";
        else
        {
            setControl = write;
            try
            {
                var detected = WithBus(ReadRegisters);
                Identity = new(vendor, model, revision, firmware, ChipId + " " + detected.Fingerprint,
                    ControlId, TachometerId, TemperatureId, "PawnIO " + driver, library, 1);
            }
            catch (Exception error)
            {
                RetryDiscovery = error is TimeoutException;
                ReadIssue = "Проверка чтения режима Super I/O: " + error.Message;
                Monitor.Log(error);
            }
        }
    }

    static Action<int, byte?>? BindBackend(Hardware hardware)
    {
        // Pinned LHM 0.9.6: IControl.SetSoftware first writes a cached value (initially 0),
        // and SetDefault caches the new mode before I/O. Use the same LHM backend directly
        // so every call applies exactly one target or retries the native restore.
        var field = hardware.GetType().GetField("_superIO", BindingFlags.NonPublic | BindingFlags.Instance);
        object? backend = field?.GetValue(hardware);
        if (backend?.GetType().FullName != "LibreHardwareMonitor.Hardware.Motherboard.Lpc.Nct677X") return null;
        var method = backend.GetType().GetMethod("SetControl", [typeof(int), typeof(byte?)]);
        return method?.CreateDelegate<Action<int, byte?>>(backend);
    }

    public void Update(IHardware hardware)
    {
        if (hardware != chip || Identity == null) { hardware.Update(); return; }
        try
        {
            WithBus(() => { hardware.Update(); return true; });
            BoardUpdateBlocked = false;
            power = chip.Sensors.SingleOrDefault(s => s.SensorType == SensorType.Control && s.Identifier.ToString() == ControlId);
        }
        catch (TimeoutException) { BoardUpdateBlocked = true; }
    }

    public FanSample Sample(Telemetry telemetry)
    {
        float? Find(string identifier, string kind) => telemetry.Readings.FirstOrDefault(r => r.Id == identifier && r.Kind == kind && r.Available)?.Value;
        if (Identity == null) return new(null, null, null, null, false, ReadIssue ?? "Канал недоступен.");
        bool available = !BoardUpdateBlocked && power?.Control is { MinSoftwareValue: 0f, MaxSoftwareValue: 100f };
        string reason = BoardUpdateBlocked ? "Шина Super I/O занята." : "Канал записи или его операции не найдены.";
        var cpu = telemetry.Cpu?.Temperature;
        float? cpuValue = cpu?.Id == TemperatureId && cpu.Name == "CPU Package" && cpu.Available ? cpu.Value : null;
        return new(Identity, cpuValue, Find(TachometerId, "Fan"), Find(ControlId, "Control"), available, reason, telemetry.CurveSources);
    }

    public void Acquire()
    {
        if (power?.Control == null || setControl == null) throw new InvalidOperationException("Канал записи недоступен.");
        writer ??= new Mutex(false, @"Global\AirDesk.Cooling.Owner");
        try { leased = writer.WaitOne(0); }
        catch (AbandonedMutexException) { writer.ReleaseMutex(); throw new InvalidOperationException("Предыдущий владелец канала завершился аварийно."); }
        if (!leased) throw new InvalidOperationException("Канал занят другим экземпляром AirDesk.");
        try { original = WithBus(ReadCurrent); }
        catch { ReleaseLease(); throw; }
        softwareAttempted = false;
        lastRawCommand = null;
    }

    public float? Write(float percent, Action guard) => WithBus<float?>(() =>
    {
        var before = ReadCurrent();
        if (lastRawCommand is byte last ? before != (0, last) : before != original)
            throw new InvalidOperationException("Режим или команда выхода изменились вне AirDesk. Источник изменения неизвестен.");
        guard();
        byte command = (byte)Math.Ceiling(percent * 255d / 100d);
        softwareAttempted = true;
        setControl!(1, command);
        var readback = ReadCurrent();
        if (readback != (0, command)) return null;
        lastRawCommand = command;
        return command * 100f / 255;
    });

    public RestoreResult Restore() => WithBus<RestoreResult>(() =>
    {
        if (!softwareAttempted) return new(true, true, "Запись не выполнялась.");
        setControl!(1, null);
        bool verified = ReadCurrent() == original;
        if (verified) softwareAttempted = false;
        return new(verified, verified, verified ? "Режим и команда контроллера совпали с исходными." : "Исходный режим или команда не подтверждены." );
    });

    (byte Mode, byte Command) ReadCurrent()
    {
        var registers = ReadRegisters();
        if (Identity?.Chip != ChipId + " " + registers.Fingerprint)
            throw new InvalidOperationException("Изменилась аппаратная идентичность Super I/O.");
        return (registers.Mode, registers.Command);
    }

    (byte Mode, byte Command, string Fingerprint) ReadRegisters()
    {
        // Public report of the pinned 0.9.6 NCT6779D backend.
        string report = chip!.GetReport();
        if (!report.Contains("Chip Id: 0xC560", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Не подтверждена аппаратная идентичность NCT6779D.");
        string[]? row = report.Split('\n').Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .SingleOrDefault(fields => fields.Length == 17 && fields[0] == "0200");
        if (row == null || !byte.TryParse(row[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte mode) ||
            !byte.TryParse(row[10], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte command))
            throw new InvalidOperationException("Не читаются регистры текущего режима и команды выхода.");
        string? chipRevision = report.Split('\n').SingleOrDefault(line => line.StartsWith("Chip Revision: 0x", StringComparison.Ordinal))?.Trim();
        string? baseAddress = report.Split('\n').SingleOrDefault(line => line.StartsWith("Base Address: 0x", StringComparison.Ordinal))?.Trim();
        if (chipRevision == null || baseAddress == null) throw new InvalidOperationException("Нет ревизии или адреса Super I/O.");
        return (mode, command, chipRevision + " " + baseAddress);
    }

    T WithBus<T>(Func<T> action)
    {
        if (bus == null) throw new InvalidOperationException("Шина Super I/O недоступна.");
        try { if (!bus.WaitOne(100)) throw new TimeoutException("Шина Super I/O занята."); }
        catch (AbandonedMutexException) { bus.ReleaseMutex(); throw new InvalidOperationException("Обнаружен аварийный владелец шины Super I/O."); }
        try { return action(); }
        finally { bus.ReleaseMutex(); }
    }

    public void ReleaseLease() { if (leased) { writer!.ReleaseMutex(); leased = false; } }
    public void Dispose() { ReleaseLease(); bus?.Dispose(); writer?.Dispose(); }
}
