using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AirDesk;

public sealed partial class Dashboard
{
    readonly TextBlock driverAccess = Text("Ожидание опроса датчиков…", 12);
    readonly TextBlock updateStatus = Text("Выполняется только по запросу. В GitHub не передаются показания или настройки.", 12);
    CancellationTokenSource? updateRequest;

    FrameworkElement BuildDriverDiagnostics()
    {
        var body = new StackPanel();
        body.Children.Add(Text("Драйвер и доступ", 17));
        AddWrapped(body, startupDriver.Description);
        AddWrapped(body, startupDriver.Elevated ? "Сеанс с правами администратора." : "Обычный сеанс Windows. Повышение прав для мониторинга не требуется.");
        driverAccess.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(driverAccess);
        var actions = new WrapPanel();
        if (startupDriver.Registration != DriverRegistration.Registered)
            actions.Children.Add(Button("Официальная страница PawnIO…", ShowDriverSource));
        if (!startupDriver.Elevated)
            actions.Children.Add(Button("Права для управления…", () => MessageBox.Show(this,
                "Для изменения оборотов на поддерживаемой плате закройте AirDesk через «Выход» в трее. Затем нажмите правой кнопкой на AirDesk.exe, выберите «Запуск от имени администратора» и подтвердите запрос Windows. При отмене можно снова открыть AirDesk для мониторинга.",
                "Управление охлаждением", MessageBoxButton.OK, MessageBoxImage.Information)));
        body.Children.Add(actions);
        return Surface(body, new Thickness(12), new Thickness(0, 0, 0, 8));
    }

    void ShowDriverSource()
    {
        if (MessageBox.Show(this,
            "PawnIO — сторонний драйвер ядра Windows. Он может дать доступ к дополнительным датчикам и контроллеру. Устанавливайте его с pawnio.eu, проверяйте подпись установщика и подтверждайте права администратора только для этого файла. Драйвер работает с расширенными правами и может быть несовместим с некоторыми конфигурациями. Установку можно отменить, мониторинг продолжит работу. Открыть сайт?",
            "PawnIO", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        OpenReleaseLink("https://pawnio.eu/");
    }

    FrameworkElement BuildUpdates()
    {
        var body = new StackPanel();
        body.Children.Add(Text("Версия и обновления", 19));
        AddWrapped(body, ReleaseUpdates.VersionLabel);
        updateStatus.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(updateStatus);
        var actions = new WrapPanel();
        var check = Button("Проверить обновления");
        var cancel = Button("Отменить", () => updateRequest?.Cancel());
        check.Click += (_, _) => _ = CheckForUpdate();
        actions.Children.Add(check);
        actions.Children.Add(cancel);
        actions.Children.Add(Button("Релизы на GitHub", () => OpenReleaseLink($"https://github.com/{ReleasePackage.Repository}/releases")));
        body.Children.Add(actions);
        cancel.IsEnabled = false;
        return Surface(body, new Thickness(14), new Thickness(0, 0, 0, 12));

        async Task CheckForUpdate()
        {
            check.IsEnabled = false;
            cancel.IsEnabled = true;
            using var request = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            updateRequest = request;
            try
            {
                updateStatus.Text = "Проверяем опубликованные версии AirDesk на GitHub…";
                var package = await ReleaseUpdates.CheckAsync(request.Token);
                if (package == null) { updateStatus.Text = "Более новой стабильной версии на GitHub нет."; return; }
                updateStatus.Text = $"Доступна версия {package.Version}.";
                if (startupDriver.Elevated)
                {
                    updateStatus.Text += " Откройте AirDesk без повышения прав, чтобы скачать установщик.";
                    return;
                }
                if (!CanPrepareUpdate()) return;
                if (MessageBox.Show(this,
                    $"Скачать AirDesk {package.Version} с GitHub и проверить SHA-256 и цифровую подпись? Программа сохранит проверенный установщик. Для установки закройте AirDesk через «Выход» в трее и запустите файл вручную.",
                    "Обновление AirDesk", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes || !CanPrepareUpdate()) return;
                var save = new SaveFileDialog { FileName = package.FileName, Filter = "Установщик AirDesk|*.exe", DefaultExt = ".exe" };
                if (save.ShowDialog(this) != true || !CanPrepareUpdate()) return;
                if (!save.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || File.Exists(save.FileName))
                {
                    updateStatus.Text = "Укажите новое имя установщика с расширением .exe.";
                    return;
                }
                updateStatus.Text = "Загружаем и проверяем установщик…";
                await ReleaseUpdates.SaveVerifiedAsync(package, save.FileName, request.Token);
                updateStatus.Text = $"Установщик проверен и сохранён: {save.FileName}.";
            }
            catch (OperationCanceledException) { updateStatus.Text = "Проверка обновлений отменена или истекло время ожидания."; }
            catch (Exception error) { Monitor.Log(error); updateStatus.Text = error.Message; }
            finally { updateRequest = null; check.IsEnabled = true; cancel.IsEnabled = false; }
        }
    }

    bool CanPrepareUpdate()
    {
        if (!quitting && !viewModel.Stalled && viewModel.Latest?.Cooling is
            { CanRelease: false, NeedsRecovery: false, State: not (ChannelState.Controlled or ChannelState.Restoring) }) return true;
        updateStatus.Text = "Дождитесь подключения датчиков и подтверждённого возврата BIOS/EC перед обновлением.";
        return false;
    }

    void OpenReleaseLink(string url)
    {
        try { using var browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception error) { Monitor.Log(error); MessageBox.Show(this, "Не удалось открыть страницу: " + url, "AirDesk"); }
    }
}
