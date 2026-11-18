using System;
using System.Collections.Generic;
using System.Linq;

namespace AirDesk;

public enum ChannelState { Unavailable, MonitorOnly, Unverified, Verified, Controlled, Error, Conflict, Restoring }
public enum SignalMode { Unknown, Pwm, Dc }
public record ChannelIdentity(string BoardVendor, string Board, string Revision, string Firmware, string Chip,
    string Control, string Tachometer, string Temperature, string Driver, string Library, int Definition);
public record Calibration(ChannelIdentity Identity, string Group, string Topology, SignalMode Signal, float Floor);
public record FanSample(ChannelIdentity? Identity, float? Cpu, float? Rpm, float? Duty, bool Available, string Reason,
    TemperatureInput[]? Inputs = null);
public record RestoreResult(bool Completed, bool Confirmed, string Detail);
public record CoolingView(ChannelState State, string Reason, string Action, string Restore, string? Conflict,
    bool CanCalibrate, bool CanEnable, bool CanAdjust, bool CanRelease, bool CanRecover, bool CanSave,
    float? Floor, string? Group, string? Channel, float? Rpm, float? Command, ChannelIdentity? Identity, string? LastPollUtc, string? LastWriteUtc,
    bool CanConfirm, float? Requested, float? Sent, string? Source, bool CanRename, bool NeedsRecovery);

public interface ICoolingOutput
{
    void Acquire();
    // The backend calls guard after acquiring the bus, immediately before the software write.
    float? Write(float percent, Action guard);
    RestoreResult Restore();
    void ReleaseLease();
}

public interface ICalibrationStore
{
    Calibration? Saved { get; }
    bool RecoveryPending { get; }
    void Save(Calibration value);
    void ClearBinding();
    void MarkCapture(ChannelIdentity identity);
    void ClearCapture();
}

public sealed class CoolingSafety(ICoolingOutput output, ICalibrationStore store,
    Func<string?> conflictScan, Func<TimeSpan> clock, Action<string> log, Func<bool> stopRequested)
{
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan TestDuration = TimeSpan.FromSeconds(8);
    static readonly TimeSpan RestoreWindow = TimeSpan.FromSeconds(3);
    FanSample? sample;
    TimeSpan? lastPoll, lastWrite, testEnd, restoreAt;
    TimeSpan restoreDeadline;
    readonly Queue<float> baseline = new();
    readonly List<float> response = new();
    readonly List<float> floorSamples = new();
    Calibration? verified, candidate;
    volatile ChannelIdentity? captured;
    string lastAction = "", restoreStatus = "";
    string? conflict, fault;
    float? command, requested, sentCommand;
    string? sourceLabel;
    FanProfile profile = FanProfile.Default;
    readonly CurveResponse responsePolicy = new();
    bool testing, active, restoring, storageFailed, restoreUncertain, closed;
    bool testingFloor;
    int restoreAttempts, mismatches;
    public DateTimeOffset? LastPollUtc { get; private set; }
    public DateTimeOffset? LastWriteUtc { get; private set; }

    bool Fresh => lastPoll is { } time && clock() - time < Freshness;
    bool SafeCpu => Fresh && FanPolicy.ValidTemperature(sample?.Cpu);
    bool HardwareReady => sample is { Available: true, Identity: not null } && sample.Rpm > 0 && sample.Duty is >= 0 and <= 100 && SafeCpu;
    bool Idle => !closed && !stopRequested() && captured == null && !restoring && conflict == null && !storageFailed && !store.RecoveryPending;
    bool CanCalibrate => Idle && HardwareReady && sample!.Cpu < 75 && baseline.Count >= 3 &&
        baseline.Max() - baseline.Min() <= Math.Max(125, Median(baseline.ToArray()) * .08f);
    bool CanEnable => Idle && HardwareReady && verified?.Identity == sample!.Identity && verified != null;
    public bool OwnsOutput => captured != null;
    public bool ShutdownComplete => closed && !restoring;
    public TimeSpan? Deadline => testEnd;

    public CoolingView View()
    {
        ChannelState state;
        string reason;
        if (restoring) (state, reason) = (ChannelState.Restoring, "Возврат управления BIOS/EC…");
        else if (conflict != null) (state, reason) = (ChannelState.Conflict, conflict);
        else if (fault != null || store.RecoveryPending && captured == null)
            (state, reason) = (ChannelState.Error, fault ?? "Предыдущий захват не завершён. Проверьте управление в BIOS/EC перед продолжением.");
        else if (testing) (state, reason) = (ChannelState.Unverified, "Калибровка выполняется. Можно отменить.");
        else if (active) (state, reason) = (ChannelState.Controlled, "Управление AirDesk активно");
        else if (sample?.Identity == null) (state, reason) = (ChannelState.MonitorOnly, sample?.Reason ?? "Обнаружение оборудования…");
        else if (!HardwareReady) (state, reason) = (ChannelState.Unavailable, UnavailableReason());
        else if (verified != null) (state, reason) = (ChannelState.Verified, "Привязка проверена. Управление остаётся у BIOS/EC до включения.");
        else (state, reason) = (ChannelState.Unverified, "Нужно подтвердить подключение и откалибровать канал.");
        return new(state, reason, lastAction, restoreStatus, conflict, CanCalibrate, CanEnable, active && conflict == null && !restoring,
            captured != null && !restoring, store.RecoveryPending && captured == null && conflict == null && !closed && sample?.Identity != null,
            Idle && candidate != null && candidate.Identity == sample?.Identity,
            verified?.Floor ?? candidate?.Floor, verified?.Group ?? store.Saved?.Group, sample?.Identity?.Control,
            sample?.Rpm, command, sample?.Identity, LastPollUtc?.ToString("O"), LastWriteUtc?.ToString("O"),
            Idle && HardwareReady && store.Saved?.Identity == sample?.Identity && store.Saved != null,
            requested, sentCommand, sourceLabel, Idle && verified != null, store.RecoveryPending && captured == null);
    }

    string UnavailableReason()
    {
        if (sample?.Available != true) return sample?.Reason ?? "Контроллер недоступен.";
        if (!SafeCpu) return "Нет свежего валидного датчика температуры CPU.";
        if (!(sample.Rpm > 0)) return "Нет рабочего тахометра канала. Запись запрещена.";
        return "Нет достоверного показания выхода управления.";
    }

    public void Observe(FanSample value)
    {
        if (sample?.Identity != value.Identity)
        {
            baseline.Clear();
            verified = candidate = null;
        }
        sample = value;
        lastPoll = clock();
        LastPollUtc = DateTimeOffset.UtcNow;
        if (captured != null && captured != value.Identity)
        {
            Interrupt("Изменилась идентичность канала.", false);
            return;
        }
        if (!HardwareReady && captured != null && !restoring)
        {
            Interrupt(UnavailableReason(), true);
            return;
        }
        if (restoring) return;
        if (captured == null && HardwareReady && conflict == null)
        {
            baseline.Enqueue(value.Rpm!.Value);
            if (baseline.Count > 5) baseline.Dequeue();
        }
        else if (captured == null) baseline.Clear();
        if (command is float sent && (active || testing))
        {
            if (value.Duty is float duty && Math.Abs(duty - sent) <= 2)
            {
                mismatches = 0;
                lastWrite = clock();
            }
            else if (++mismatches >= 2)
            {
                Interrupt("Показания выхода изменились без команды AirDesk. Кто управляет каналом, установить не удалось.", false);
                return;
            }
        }
        if (testing && value.Cpu >= 75)
        {
            Interrupt("Тест прерван: CPU достиг 75 °C.", false);
            return;
        }
        if (testing && testEnd is { } end && clock() < end)
        {
            if (testingFloor) floorSamples.Add(value.Rpm!.Value);
            else if (clock() >= end - TimeSpan.FromSeconds(4)) response.Add(value.Rpm!.Value);
        }
    }

    public void Tick()
    {
        string? detected = conflictScan();
        if (detected != conflict) log(detected ?? "Известные конкурирующие процессы больше не обнаружены.");
        conflict = detected;
        if (conflict != null) baseline.Clear();
        if (captured != null && !restoring)
        {
            if (closed || stopRequested()) Interrupt("Управление остановлено.", false);
            else if (conflict != null) Interrupt(conflict, false);
            else if (!SafeCpu || lastWrite is { } sent && clock() - sent >= Freshness)
                Interrupt("Превышен срок последнего опроса или подтверждения записи.", true);
            else if (testing && clock() >= testEnd) EndTest();
            else if (testing && testingFloor && clock() >= testEnd - TimeSpan.FromSeconds(5)) RaiseTestSpeed();
            else if (active) ApplyProfile();
        }
        if (restoring && clock() >= restoreAt) RestoreStep();
    }

    public void TickBeforeSample()
    {
        if (captured == null || restoring) return;
        if (!Fresh) Interrupt("Истёк срок последнего успешного опроса.", true);
        else if (testing && clock() >= testEnd) EndTest();
    }

    public void BeginCalibration(Calibration proposal)
    {
        conflict = conflictScan();
        if (!CanCalibrate || proposal.Identity != sample!.Identity || !Valid(proposal) || proposal.Floor < Math.Ceiling(sample.Duty!.Value))
            throw new InvalidOperationException("Калибровка недоступна. Нужны свежий CPU ниже 75 °C, стабильный тахометр, подтверждённое подключение и минимум не ниже текущего выхода BIOS.");
        candidate = verified = null;
        response.Clear();
        floorSamples.Clear();
        try
        {
            Capture(proposal.Identity);
            pendingProposal = proposal;
            testing = true;
            testEnd = clock() + TestDuration;
            testingFloor = proposal.Floor < 100;
            requested = proposal.Floor;
            Write(proposal.Floor);
            SetAction(testingFloor ? $"Калибровка: проверяется удержание оборотов на {proposal.Floor:0}%." :
                "Калибровка: выход 100%, собираются показания тахометра хаба.");
        }
        catch (Exception error) { Interrupt(error.Message, false); }
    }

    Calibration? pendingProposal;

    void RaiseTestSpeed()
    {
        if (floorSamples.Count < 2 || floorSamples.Min() < baseline.Min() - Math.Max(100, baseline.Min() * .1f))
        {
            Interrupt("Не подтверждено удержание оборотов на выбранном минимуме. Тест остановлен.", false);
            return;
        }
        try
        {
            testingFloor = false;
            requested = 100;
            Write(100);
            SetAction("Калибровка: выход 100%, собираются показания тахометра хаба.");
        }
        catch (Exception error) { Interrupt(error.Message, false); }
    }

    void EndTest()
    {
        var before = baseline.ToArray();
        var after = response.ToArray();
        if (!testingFloor && HasResponse(before, after))
        {
            candidate = pendingProposal;
            SetAction($"Отклик тахометра {Median(before):0} → {Median(after):0} об/мин. Подтвердите физическое назначение после возврата BIOS/EC.");
        }
        else SetAction("Отклик не подтверждён: шум, недостаточно измерений или вентилятор уже на максимуме.");
        BeginRestore();
    }

    public static bool HasResponse(float[] before, float[] after) => before.Length >= 3 && after.Length >= 3 &&
        before.Concat(after).All(v => float.IsFinite(v) && v > 0) &&
        Median(after) - Median(before) > Math.Max(80, 2 * Math.Max(before.Max() - before.Min(), after.Max() - after.Min()));

    static float Median(float[] data) => data.OrderBy(value => value).ElementAt(data.Length / 2);
    static bool Valid(Calibration value) => !string.IsNullOrWhiteSpace(value.Group) && value.Group.Length <= 48 &&
        !string.IsNullOrWhiteSpace(value.Topology) && value.Topology.Length <= 150 &&
        value.Signal is SignalMode.Pwm or SignalMode.Dc && value.Floor is >= 40 and <= 100;

    public void SaveCalibration()
    {
        if (!Idle || candidate == null || candidate.Identity != sample?.Identity || restoreUncertain)
            throw new InvalidOperationException("Нет завершённой калибровки с возвратом управления.");
        store.Save(candidate);
        verified = candidate;
        candidate = null;
        fault = null;
        SetAction("Назначение и проверенный минимум сохранены.");
    }

    public void ConfirmUnchangedTopology()
    {
        conflict = conflictScan();
        var saved = store.Saved;
        if (!Idle || !HardwareReady || saved == null || !Valid(saved) || saved.Identity != sample?.Identity)
            throw new InvalidOperationException("Сохранённая привязка отсутствует или устарела. Нужна новая калибровка.");
        verified = saved;
        fault = null;
        SetAction("Сохранённая привязка подтверждена для текущего запуска.");
    }

    public void Rename(string name)
    {
        if (!View().CanRename || string.IsNullOrWhiteSpace(name) || name.Trim().Length > 48)
            throw new InvalidOperationException("Название группы должно содержать от 1 до 48 символов.");
        var renamed = verified! with { Group = name.Trim() };
        store.Save(renamed);
        verified = renamed;
        SetAction("Название группы сохранено.");
    }

    public void Enable(FanProfile next)
    {
        conflict = conflictScan();
        if (!CanEnable) throw new InvalidOperationException(conflict ?? "Сначала подтвердите привязку и доступность датчиков.");
        ValidateProfile(next);
        try
        {
            Capture(verified!.Identity);
            active = true;
            responsePolicy.Reset();
            SetProfile(next);
        }
        catch (Exception error) { Interrupt(error.Message, false); throw; }
        ApplyRequestedProfile();
    }

    public void Adjust(FanProfile next)
    {
        if (!active || restoring) throw new InvalidOperationException("Управление недоступно.");
        ValidateProfile(next);
        SetProfile(next);
        ApplyRequestedProfile();
    }

    void SetProfile(FanProfile next)
    {
        if (profile != next) responsePolicy.ProfileChanged();
        profile = next;
    }

    void ApplyRequestedProfile()
    {
        Tick();
        if (!active || !command.HasValue) throw new InvalidOperationException(conflict ?? fault ?? "Применить профиль не удалось.");
    }

    void ValidateProfile(FanProfile next)
    {
        if (!Curve.Valid(next)) throw new ArgumentException("Некорректные точки кривой или скорость.");
        if (next.Kind != ProfileKind.Manual && !Resolve(next).Temperature.HasValue)
            throw new InvalidOperationException("Выбранный источник температуры недоступен. Проверьте привязку.");
    }

    (float? Temperature, string Label) Resolve(FanProfile value) => Curve.Resolve(value, sample?.Cpu, sample?.Inputs ?? []);

    void ApplyProfile()
    {
        var (temperature, label) = profile.Kind == ProfileKind.Manual ? (sample!.Cpu, "CPU Package (защита)") : Resolve(profile);
        sourceLabel = label;
        if (!temperature.HasValue)
        {
            Interrupt("Потерян один из источников кривой: " + label, true);
            return;
        }
        requested = profile.Kind == ProfileKind.Manual ? profile.Manual : Curve.Interpolate(
            profile.Kind == ProfileKind.Custom ? profile.Custom : Curve.DefaultPoints(profile.Kind), temperature.Value);
        float target = responsePolicy.Target(profile, verified!.Floor, sample!.Cpu!.Value, temperature.Value, clock());
        try { if (command != target) Write(target); }
        catch (Exception error) { Interrupt(error.Message, false); }
    }

    void Capture(ChannelIdentity identity)
    {
        Guard(false);
        output.Acquire();
        try { store.MarkCapture(identity); }
        catch { output.ReleaseLease(); throw; }
        captured = identity;
        restoreUncertain = false;
        restoreStatus = "";
        fault = null;
        mismatches = 0;
        sentCommand = requested = null;
        sourceLabel = null;
    }

    void Write(float target, bool emergency = false)
    {
        SetAction($"Команда принята: {target:0}%.");
        float? duty = output.Write(target, () => Guard(emergency));
        sentCommand = target;
        log($"Команда передана драйверу: {target:0}%.");
        if (duty is not float measured || !float.IsFinite(measured) || Math.Abs(measured - target) > 2)
            throw new InvalidOperationException($"Выход не подтвердил команду {target:0}% (показание: {duty?.ToString("0.#") ?? "нет"}).");
        command = target;
        lastWrite = clock();
        LastWriteUtc = DateTimeOffset.UtcNow;
        SetAction($"Регистр команды подтвердил {target:0}%. Фактические обороты показывает отдельный тахометр.");
    }

    void Guard(bool emergency)
    {
        conflict = conflictScan();
        if (conflict != null) throw new InvalidOperationException(conflict);
        if (closed || stopRequested() || restoring || testEnd is { } end && clock() >= end)
            throw new InvalidOperationException("Команда отменена или время теста истекло.");
        if (!emergency && !SafeCpu) throw new InvalidOperationException("Температура CPU недоступна или устарела.");
    }

    public void Interrupt(string reason, bool trySafeSpeed)
    {
        fault = conflict == null ? reason : null;
        if (fault != null) verified = null;
        candidate = null;
        SetAction(reason);
        if (captured != null && trySafeSpeed && !testing && !restoring && conflict == null)
        {
            try { Write(100, true); log("Регистр команды подтвердил попытку 100%."); }
            catch (Exception error) { log("Попытка 100% не подтверждена: " + error.Message); }
            SetAction(reason + " Запрошен возврат управления BIOS/EC.");
        }
        BeginRestore();
    }

    public void Stop(string reason)
    {
        if (testing) { candidate = null; SetAction("Калибровка прервана: " + reason); }
        BeginRestore();
    }

    public void Close()
    {
        closed = true;
        Stop("выход из приложения");
    }

    public void AcknowledgeRecovery()
    {
        if (!View().CanRecover) throw new InvalidOperationException("Нельзя снять блокировку сейчас.");
        store.ClearBinding();
        store.ClearCapture();
        verified = candidate = null;
        fault = null;
        SetAction("Подтверждена проверка BIOS/EC. Перед записью нужна новая калибровка.");
    }

    void BeginRestore()
    {
        active = testing = false;
        testEnd = lastWrite = null;
        pendingProposal = null;
        if (captured == null || restoring || restoreUncertain) return;
        restoring = true;
        restoreAttempts = 0;
        restoreAt = clock();
        restoreDeadline = clock() + RestoreWindow;
    }

    void RestoreStep()
    {
        RestoreResult result;
        restoreAttempts++;
        try { result = output.Restore(); }
        catch (Exception error) { result = new(false, false, error.Message); }
        restoreStatus = $"{captured!.Control} · попытка {restoreAttempts}: {result.Detail}";
        log(restoreStatus);
        if ((!result.Completed || !result.Confirmed) && restoreAttempts < 3 && clock() + TimeSpan.FromMilliseconds(200) < restoreDeadline)
        {
            restoreAt = clock() + TimeSpan.FromMilliseconds(200);
            return;
        }
        restoring = false;
        restoreUncertain = !result.Completed || !result.Confirmed;
        if (restoreUncertain)
        {
            candidate = verified = null;
            fault = "Возврат BIOS/EC не подтверждён. Проверьте охлаждение и настройки BIOS/EC.";
        }
        else
        {
            try { store.ClearCapture(); }
            catch (Exception error) { storageFailed = true; fault = "Ошибка журнала восстановления: " + error.Message; }
        }
        output.ReleaseLease();
        captured = null;
        command = null;
        requested = null;
        sourceLabel = null;
        LastWriteUtc = null;
        baseline.Clear();
    }

    void SetAction(string text) { lastAction = text; log(text); }
}
