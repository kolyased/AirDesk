using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace AirDesk;
public static class Program
{
    [STAThread] public static void Main(string[] args) {
        using var single = new Mutex(true,"Local\\AirDesk-0.1",out bool first);
        if(!first) { MessageBox.Show("AirDesk уже работает. Откройте значок в трее."); return; }
        var app=new System.Windows.Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_,e)=> { Monitor.Log(e.Exception); MessageBox.Show(e.Exception.Message,"AirDesk"); e.Handled=true; };
        var window=new Dashboard(); app.MainWindow=window;
        if(!args.Contains("--tray")) window.Show();
        window.Start(args.Contains("--smoke"),args.Contains("--fan-test")); app.Run();
    }
}
public sealed class Dashboard : Window
{
    static readonly SolidColorBrush Bg=Brush("#10151E"), Panel=Brush("#19212E"), Muted=Brush("#9CAFC5"), Accent=Brush("#6DE0BD");
    readonly Monitor monitor=new();
    readonly Forms.NotifyIcon tray;
    readonly TextBlock status=Text("Подключение к датчикам…",12), cpuValue=Text("—",34), ramValue=Text("—",34), gpuValue=Text("—",34), diskValue=Text("—",34);
    readonly TextBlock cpuSub=Text("Процессор",12), ramSub=Text("Память",12), gpuSub=Text("Видеокарта",12), diskSub=Text("Накопители",12), notice=Text("",12);
    readonly Polyline graph=new() { Stroke=Accent,StrokeThickness=2 };
    readonly Queue<double> history=new();
    readonly System.Windows.Controls.Primitives.UniformGrid temperatureCards=new(){Columns=4,Margin=new Thickness(0,10,0,18)};
    readonly TextBlock hubStatus=Text("Подключение…",18), otherFan=Text("",12);
    readonly ComboBox channel=new(){MinWidth=250}, mode=new(){MinWidth=200};
    readonly Slider percent=new(){Minimum=40,Maximum=100,Value=60,TickFrequency=5,IsSnapToTickEnabled=true,Width=180};
    readonly TextBlock percentText=Text("60%",16);
    readonly Button apply, test;
    Snapshot? latest;
    bool quitting;
    Window? flyout;
    readonly TextBlock flyText=Text("Подключение…",16);
    public Dashboard() {
        Title="AirDesk — температура и охлаждение"; Width=1060; Height=900; MinWidth=930; MinHeight=700;
        Background=Bg; Foreground=Brushes.White; FontFamily=new FontFamily("Segoe UI"); FontSize=14;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var root=new DockPanel {Margin=new Thickness(28)}; Content=root;
        var footer=new DockPanel{Margin=new Thickness(0,18,0,0)}; DockPanel.SetDock(footer,Dock.Bottom); root.Children.Add(footer); footer.Children.Add(status);
        var hide=Button("Свернуть в трей",()=>Hide()); DockPanel.SetDock(hide,Dock.Right); footer.Children.Insert(0,hide);
        var head=new StackPanel{Margin=new Thickness(0,0,0,22)}; DockPanel.SetDock(head,Dock.Top); root.Children.Add(head);
        head.Children.Add(Text("◉  AirDesk",28)); head.Children.Add(Text("ТЕМПЕРАТУРА  /  НАГРУЗКА  /  ОХЛАЖДЕНИЕ",11));
        var scroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto}; root.Children.Add(scroll);
        var body=new StackPanel(); scroll.Content=body;
        var cards=new System.Windows.Controls.Primitives.UniformGrid{Columns=4,Margin=new Thickness(0,0,0,18)};
        cards.Children.Add(Card("CPU",cpuValue,cpuSub)); cards.Children.Add(Card("RAM",ramValue,ramSub)); cards.Children.Add(Card("GPU",gpuValue,gpuSub)); cards.Children.Add(Card("ДИСК",diskValue,diskSub)); body.Children.Add(cards);
        var chart=new Grid{Height=65,Background=Panel,Margin=new Thickness(0,0,8,14)};
        chart.Children.Add(new TextBlock{Text="ЗАГРУЗКА CPU · ПОСЛЕДНИЕ 60 СЕКУНД",FontSize=10,Foreground=Muted,Margin=new Thickness(16,8,0,0)});
        var canvas=new Canvas{Margin=new Thickness(16,30,16,10)}; canvas.Children.Add(graph); chart.Children.Add(canvas); canvas.SizeChanged+=(_,_)=>DrawGraph(canvas.ActualWidth,canvas.ActualHeight); body.Children.Add(chart);
        body.Children.Add(Text("Температуры",18)); body.Children.Add(temperatureCards);
        var control=new StackPanel(); control.Children.Add(Text("Управление охлаждением",18));
        control.Children.Add(hubStatus); control.Children.Add(otherFan);
        var row=new WrapPanel{Margin=new Thickness(0,12,0,10)}; channel.Margin=new Thickness(0,0,12,0); mode.Margin=new Thickness(0,0,12,0);
        mode.Items.Add("Авто · по CPU Package"); mode.Items.Add("Вручную · 40–100%"); mode.SelectedIndex=0;
        row.Children.Add(mode); row.Children.Add(percent); row.Children.Add(percentText); control.Children.Add(row);
        percent.ValueChanged+=(_,_)=>percentText.Text=$"{percent.Value:0}%"; mode.SelectionChanged+=(_,_)=>{percent.IsEnabled=mode.SelectedIndex==1; percentText.Opacity=mode.SelectedIndex==1?1:0.4;}; percent.IsEnabled=false; percentText.Opacity=0.4;
        var buttons=new WrapPanel(); apply=Button("Применить",Apply); buttons.Children.Add(apply); test=Button("Проверить отклик · 8 сек",()=>notice.Text=monitor.TestHub()); buttons.Children.Add(test);
        buttons.Children.Add(Button("Вернуть всё BIOS",()=>{try {monitor.Release(); notice.Text="Все захваченные каналы возвращены BIOS.";} catch(Exception e){notice.Text=e.Message;}}));
        buttons.Children.Add(Button("Сохранить отчёт",Export)); control.Children.Add(buttons);
        notice.TextWrapping=TextWrapping.Wrap; notice.Margin=new Thickness(0,10,0,0); notice.Foreground=Accent; control.Children.Insert(3,notice);
        control.Children.Add(Text("Авто: 40°C → 40% · 60°C → 65% · 75°C → 90% · 85°C → 100%",12));
        body.Children.Add(new Border{Background=Panel,CornerRadius=new CornerRadius(12),Padding=new Thickness(18),Child=control,Margin=new Thickness(0,0,8,0)});
        tray=new Forms.NotifyIcon{Icon=System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,Visible=true,Text="AirDesk — мониторинг"};
        var menu=new Forms.ContextMenuStrip(); menu.Items.Add("Открыть AirDesk",null,(_,_)=>Dispatcher.Invoke(ShowMain)); menu.Items.Add("Вернуть управление BIOS",null,(_,_)=>Dispatcher.Invoke(()=>{try{monitor.Release();}catch(Exception e){MessageBox.Show(e.Message);}})); menu.Items.Add("Выход",null,(_,_)=>Dispatcher.Invoke(Exit)); tray.ContextMenuStrip=menu;
        tray.MouseClick+=(_,e)=>{if(e.Button==Forms.MouseButtons.Left) Dispatcher.Invoke(ToggleFlyout);}; tray.DoubleClick+=(_,_)=>Dispatcher.Invoke(ShowMain);
        Closing+=(_,e)=>{if(!quitting){e.Cancel=true;Hide();}};
        monitor.Updated+=s=>Dispatcher.BeginInvoke(()=>Render(s));
    }
    public void Start(bool smoke,bool fanTest) { Task.Run(()=>{try{monitor.Start();}catch(Exception e){Monitor.Log(e);Dispatcher.BeginInvoke(()=>status.Text="Не удалось подключиться: "+e.Message);}});
        if(fanTest) { var ft=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(5)}; ft.Tick+=(_,_)=>{ft.Stop();notice.Text=monitor.TestHub();};ft.Start(); }
        if(smoke) { var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(fanTest?22:12)}; timer.Tick+=(_,_)=>{timer.Stop();try {File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory,"smoke.json"),JsonSerializer.Serialize(latest,new JsonSerializerOptions{WriteIndented=true})); SaveRender(this,"preview.png"); ToggleFlyout(); flyout!.UpdateLayout(); SaveRender(flyout,"tray-preview.png");} finally {Exit();}};timer.Start(); }
    }
    void Apply() { if(latest?.HubId is not string id) return;
        notice.Text=monitor.Apply(id,mode.SelectedIndex==0,(float)percent.Value);
    }
    void ShowMain(){flyout?.Hide();Show();WindowState=WindowState.Normal;Activate();}
    void ToggleFlyout() {
        if(flyout?.IsVisible==true){flyout.Hide();return;}
        if(flyout==null){
            flyout=new Window{Title="AirDesk · кратко",Width=330,Height=285,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,Topmost=true,Background=Bg,Foreground=Brushes.White};
            var p=new StackPanel{Margin=new Thickness(22)}; p.Children.Add(Text("◉  AirDesk",22)); flyText.Margin=new Thickness(0,16,0,18); p.Children.Add(flyText); p.Children.Add(Button("Открыть панель",ShowMain)); flyout.Content=p; flyout.Deactivated+=(_,_)=>flyout.Hide();
        }
        var a=SystemParameters.WorkArea; flyout.Left=a.Right-flyout.Width-12;flyout.Top=a.Bottom-flyout.Height-12;flyout.Show();flyout.Activate();
    }
    void Render(Snapshot s) {
        latest=s; status.Text=$"{s.Status}  ·  {s.Time:HH:mm:ss}";
        var r=s.Readings;
        float? Value(string id,string name,string kind)=>r.FirstOrDefault(x=>x.Id.StartsWith(id)&&x.Name==name&&x.Kind==kind)?.Value;
        float? cpu=Value("/intelcpu/","CPU Total","Load")??Value("/amdcpu/","CPU Total","Load"), ram=Value("/ram/","Memory","Load");
        var gpu=r.FirstOrDefault(x=>x.Id.StartsWith("/gpu")&&x.Kind=="Load"&&x.Name=="GPU Core");
        var disk=r.Where(x=>(x.Id.StartsWith("/nvme/")||x.Id.StartsWith("/ahci/"))&&x.Kind=="Load"&&x.Name.Contains("Total Activity")).Select(x=>x.Value).Where(x=>x.HasValue).DefaultIfEmpty(null).Max();
        cpuValue.Text=Pct(cpu);ramValue.Text=Pct(ram);gpuValue.Text=Pct(gpu?.Value);diskValue.Text=Pct(disk);
        var temp=r.FirstOrDefault(x=>x.Kind=="Temperature"&&x.Name=="CPU Package"&&x.Id.Contains("cpu/"));
        cpuSub.Text=$"{temp?.Value:0} °C · CPU Package";if(temp?.Value==null)cpuSub.Text="Температура недоступна";
        var used=Value("/ram/","Memory Used","Data"); var free=Value("/ram/","Memory Available","Data");ramSub.Text=used.HasValue?$"{used:0.0} / {used+free:0.0} ГБ":"Физическая память";
        var gpuTemp=r.FirstOrDefault(x=>x.Id.StartsWith("/gpu")&&x.Kind=="Temperature"&&x.Name=="GPU Core");
        gpuSub.Text=$"{Degrees(gpuTemp?.Value)} · {gpu?.Device}";diskSub.Text="Макс. активность дисков";
        if(cpu.HasValue){history.Enqueue(cpu.Value);while(history.Count>60)history.Dequeue();}DrawGraph(Math.Max(400,ActualWidth-110),25);
        temperatureCards.Children.Clear();
        temperatureCards.Children.Add(Card("ПРОЦЕССОР",Text(Degrees(temp?.Value),28),Text("CPU Package",11)));
        var hot=r.FirstOrDefault(x=>x.Id.StartsWith("/gpu")&&x.Kind=="Temperature"&&x.Name=="GPU Hot Spot");
        temperatureCards.Children.Add(Card("ВИДЕОКАРТА",Text(Degrees(gpuTemp?.Value),28),Text($"Hotspot {Degrees(hot?.Value)}",11)));
        foreach(var diskGroup in r.Where(x=>x.Id.StartsWith("/nvme/")||x.Id.StartsWith("/ahci/")).GroupBy(x=>x.Device).Take(2)) {
            var t=diskGroup.FirstOrDefault(x=>x.Kind=="Temperature"&&x.Value.HasValue&&(x.Name=="Temperature"||x.Name.Contains("Composite")))??diskGroup.FirstOrDefault(x=>x.Kind=="Temperature"&&!x.Name.Contains("Critical")&&!x.Name.Contains("Warning"));
            temperatureCards.Children.Add(Card("НАКОПИТЕЛЬ",Text(Degrees(t?.Value),28),Text(diskGroup.Key,11)));
        }
        var memoryTemp=r.FirstOrDefault(x=>x.Kind=="Temperature"&&(x.Id.StartsWith("/ram/")||x.Device.Contains("DIMM")));
        ramSub.Text+=(memoryTemp?.Value.HasValue==true?$" · {Degrees(memoryTemp.Value)}":" · °C недоступна");
        hubStatus.Text=s.HubId==null?"Канал управления не найден":$"Группа Fan #2    {s.HubRpm:0} об/мин";
        var firstFan=r.FirstOrDefault(x=>x.Kind=="Fan"&&x.Id.Contains("/nct6779d/")&&x.Id.EndsWith("/fan/0"));
        otherFan.Text=firstFan?.Value>0?$"Fan #1: {firstFan.Value:0} об/мин · только наблюдение, назначение не установлено":"";
        apply.IsEnabled=test.IsEnabled=!s.Conflict&&s.HubId!=null;
        notice.Text=s.Conflict?"Закройте другую программу управления вентиляторами.":s.ControlStatus;
        if(!string.IsNullOrEmpty(s.TestResult)&&s.TestResult!=s.ControlStatus) notice.Text+="\n"+s.TestResult;
        flyText.Text=$"CPU    {Pct(cpu)}   ·   {Degrees(temp?.Value)}\nGPU   {Pct(gpu?.Value)}   ·   {Degrees(gpuTemp?.Value)}\nRAM   {Pct(ram)}\nFan #2   {s.HubRpm:0} об/мин";
        tray.Text=$"AirDesk | CPU {Pct(cpu)} | {temp?.Value:0} °C | RAM {Pct(ram)}";
    }
    void DrawGraph(double w,double h){graph.Points=new PointCollection(history.Select((v,i)=>new System.Windows.Point(i*w/59,h*(1-Math.Clamp(v,0,100)/100))));}
    void Export(){var dialog=new Microsoft.Win32.SaveFileDialog{Filter="JSON|*.json",FileName="AirDesk-sensors.json"};if(dialog.ShowDialog()==true)File.WriteAllText(dialog.FileName,JsonSerializer.Serialize(latest,new JsonSerializerOptions{WriteIndented=true}));}
    static void SaveRender(Window window,string name){window.UpdateLayout();var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using var file=File.Create(System.IO.Path.Combine(AppContext.BaseDirectory,name));encoder.Save(file);}
    void Exit(){try{monitor.Dispose();}catch(Exception e){Monitor.Log(e);MessageBox.Show("Не удалось полностью вернуть управление BIOS: "+e.Message);}quitting=true;tray.Dispose();flyout?.Close();System.Windows.Application.Current.Shutdown();}
    static string Degrees(float? v)=>v.HasValue?$"{v:0} °C":"—";
    static string Pct(float? v)=>v.HasValue?$"{v:0}%":"—";
    static SolidColorBrush Brush(string hex)=>new((Color)ColorConverter.ConvertFromString(hex));
    static TextBlock Text(string text,double size)=>new(){Text=text,FontSize=size,Foreground=size<=12?Muted:Brushes.White,Margin=new Thickness(0,0,0,5)};
    static Button Button(string text,Action action){var b=new Button{Content=text,Padding=new Thickness(14,8,14,8),Margin=new Thickness(0,0,10,0),Background=Brush("#29394A"),Foreground=Brushes.White,BorderThickness=new Thickness(0),Cursor=System.Windows.Input.Cursors.Hand};b.Click+=(_,_)=>action();return b;}
    static Border Card(string title,TextBlock value,TextBlock sub){var p=new StackPanel();p.Children.Add(Text(title,11));value.Foreground=Accent;p.Children.Add(value);sub.TextTrimming=TextTrimming.CharacterEllipsis;p.Children.Add(sub);return new(){Background=Panel,CornerRadius=new CornerRadius(12),Padding=new Thickness(18),Margin=new Thickness(0,0,8,0),Child=p};}
    static void ConfigureList(ListView list,(string,string,double)[] cols){list.Background=Panel;list.Foreground=Brushes.White;list.BorderThickness=new Thickness(0);list.Margin=new Thickness(0,10,0,0);var g=new GridView();var style=new Style(typeof(GridViewColumnHeader));style.Setters.Add(new Setter(Control.BackgroundProperty,Brush("#29394A")));style.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.White));style.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0)));g.ColumnHeaderContainerStyle=style;foreach(var (title,path,width) in cols)g.Columns.Add(new GridViewColumn{Header=title,Width=width,DisplayMemberBinding=new System.Windows.Data.Binding(path)});list.View=g;}
    record Channel(string Id,string Name){public override string ToString()=>Name;}
}
