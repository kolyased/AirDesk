using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using LibreHardwareMonitor.Hardware;

namespace AirDesk;
public record Reading(string Device, string Name, string Kind, float? Value, string Id);
public record Snapshot(DateTime Time, Reading[] Readings, string Status, bool Conflict, string ControlStatus, string? HubId, float? HubRpm, float? Command, string TestResult);
public class Monitor : IDisposable
{
    readonly Computer computer = new() { IsCpuEnabled=true, IsMotherboardEnabled=true, IsMemoryEnabled=true, IsGpuEnabled=true, IsStorageEnabled=true, IsNetworkEnabled=true };
    readonly object gate = new();
    readonly Dictionary<string, ISensor> controls = new();
    readonly Dictionary<string, (bool Auto, float Percent)> owned = new();
    readonly Dictionary<string, float> previous = new();
    Timer? timer;
    bool disposed;
    string? hubId;
    float? hubRpm;
    string controlStatus="Управление BIOS";
    string testResult="";
    DateTime? testStarted;
    float? testBefore;
    (bool Auto,float Percent)? beforeMode;
    public event Action<Snapshot>? Updated;
    public static bool Conflict => Process.GetProcessesByName("FanControl").Length > 0 || Process.GetProcessesByName("MSIAfterburner").Length > 0;
    public void Start() { lock(gate) { computer.Open(); } timer = new Timer(_ => Poll(),null,0,1000); }
    static IEnumerable<IHardware> Walk(IHardware h) { yield return h; foreach(var s in h.SubHardware) foreach(var x in Walk(s)) yield return x; }
    public string Apply(string id, bool automatic, float percent)
    {
        lock(gate) {
            if(disposed) return "Мониторинг остановлен.";
            if(Conflict) return "Закройте FanControl / MSI Afterburner перед включением управления.";
            if(!controls.TryGetValue(id,out var s) || s.Control == null) return "Канал управления недоступен.";
            if(testStarted.HasValue) return "Дождитесь окончания проверки.";
            if(id!=hubId) return "Этот канал не выбран как общий контроллер.";
            owned[id]=(automatic,Math.Clamp(percent,40,100));
            controlStatus="Команда принята, ожидается запись…";
            return controlStatus;
        }
    }
    public string TestHub() {
        lock(gate) {
            if(Conflict) return "Сначала закройте FanControl / MSI Afterburner.";
            if(hubId==null || !hubRpm.HasValue) return "Нет доступного контроллера или показаний оборотов.";
            if(testStarted.HasValue) return "Проверка уже выполняется.";
            testResult="";beforeMode=owned.TryGetValue(hubId,out var m)?m:null;
            testBefore=hubRpm;testStarted=DateTime.UtcNow;
            owned[hubId]=(false,100);controlStatus="Проверка: 100% на 8 секунд…";
            return controlStatus;
        }
    }
    public void Release(string? id=null) {
        lock(gate) {
            foreach(var k in owned.Keys.ToArray()) if(id==null || id==k) {
                // Keep ownership on failure so the next poll retries restoration.
                controls[k].Control!.SetDefault(); owned.Remove(k); previous.Remove(k);
            }
            if(id==null) {testStarted=null;controlStatus="Управление возвращено BIOS";}
        }
    }
    void Poll() {
        if(!System.Threading.Monitor.TryEnter(gate)) return;
        try {
            if(disposed) return;
            var readings = new List<Reading>();
            foreach(var h in computer.Hardware.SelectMany(Walk)) {
                h.Update();
                foreach(var s in h.Sensors) {
                    float? value=s.Value.HasValue && float.IsFinite(s.Value.Value) ? s.Value : null;
                    readings.Add(new(h.Name,s.Name,s.SensorType.ToString(),value,s.Identifier.ToString()));
                    if(h.HardwareType==HardwareType.SuperIO && s.SensorType==SensorType.Control && s.Control!=null) controls[s.Identifier.ToString()]=s;
                }
            }
            bool conflict=Conflict;
            if(conflict && owned.Count>0) Release();
            // This board's saved FanControl calibration identifies Fan #2 as responsive.
            // No blind fallback to a different header on other motherboards.
            hubId=controls.Keys.FirstOrDefault(k=>k.Contains("/nct6779d/") && k.EndsWith("/control/1"));
            hubRpm=hubId==null?null:readings.FirstOrDefault(r=>r.Id==hubId.Replace("/control/","/fan/"))?.Value;
            float? cpu=readings.FirstOrDefault(r=>r.Id.StartsWith("/intelcpu/") && r.Name=="CPU Package")?.Value
                ?? readings.FirstOrDefault(r=>r.Id.StartsWith("/amdcpu/") && r.Kind=="Temperature" && r.Name.Contains("Tctl"))?.Value;
            foreach(var (id,mode) in owned) {
                float target=FanPolicy.Target(cpu,mode.Auto,mode.Percent);
                // Fast rise, slow fall; emergency always bypasses smoothing.
                if(previous.TryGetValue(id,out float old) && target<old) target=Math.Max(target,old-2);
                controls[id].Control!.SetSoftware(target); previous[id]=target;
                if(!testStarted.HasValue) controlStatus=$"Записано {target:0}% · {(mode.Auto?"авто по CPU":"ручной режим")} · фактически {hubRpm:0} об/мин";
            }
            if(testStarted.HasValue && (DateTime.UtcNow-testStarted.Value).TotalSeconds>=8 && hubId!=null) {
                string result=$"Проверка: {testBefore:0} → {hubRpm:0} об/мин. ";
                result+=hubRpm>testBefore*1.10f?"Рост оборотов подтверждён.":"Заметного роста нет: уже максимум или хаб не реагирует на PWM.";
                if(beforeMode.HasValue) owned[hubId]=beforeMode.Value;else Release(hubId);
                testStarted=null;testResult=result;controlStatus=result;
            }
            Updated?.Invoke(new(DateTime.Now,readings.ToArray(), conflict ? "Мониторинг • закройте другую программу управления" : owned.Count>0 ? "Управление активно" : "Мониторинг • управление BIOS",conflict,controlStatus,hubId,hubRpm,hubId!=null&&previous.TryGetValue(hubId,out var cmd)?cmd:null,testResult));
        } catch(Exception e) {
            foreach(var id in owned.Keys) try { controls[id].Control!.SetSoftware(100); } catch { }
            Log(e);
            controlStatus="Ошибка записи/чтения: "+e.Message;
            Updated?.Invoke(new(DateTime.Now,Array.Empty<Reading>(),"Ошибка датчиков: " + e.Message,Conflict,controlStatus,hubId,null,null,testResult));
        } finally { System.Threading.Monitor.Exit(gate); }
    }
    public static void Log(Exception e) { try { string p=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AirDesk"); Directory.CreateDirectory(p); File.AppendAllText(Path.Combine(p,"errors.log"),DateTime.Now+" "+e+Environment.NewLine); } catch {} }
    public void Dispose() { timer?.Dispose(); lock(gate) { try { Release(); } finally { disposed=true; computer.Close(); } } }
}
