# FPS и экранная панель

AirDesk читает локальный CSV с кадрами отдельно запущенного Intel PresentMon. PresentMon использует ETW. AirDesk запускается с обычными правами и не запускает, не устанавливает и не обновляет PresentMon. На этом ПК живой сеанс ETW без нужной группы или повышения прав был отклонён Windows. [Выпуски PresentMon](https://github.com/GameTechDev/PresentMon/releases), [формат CSV и права](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/README-ConsoleApplication.md)

1. Возьмите консольную x64-версию PresentMon из официального релиза и проверьте подпись. Найдите PID нужной программы в «Диспетчер задач → Подробности».
2. В папке PresentMon откройте Windows PowerShell 5.1. Замените `1234` на нужный PID и вставьте следующий блок в окно PowerShell. Он записывает строки в `frames.csv` и оставляет файл доступным для чтения. Чтобы не собирать метрики ввода, оставьте `--no_track_input`.

```powershell
$presentMonPath = (Resolve-Path -LiteralPath '.\PresentMon-2.6.0-x64.exe' -ErrorAction Stop).Path
$framesCsv = [System.IO.StreamWriter]::new((Join-Path $PWD 'frames.csv'), $false, [System.Text.UTF8Encoding]::new($false))
try {
    $framesCsv.AutoFlush = $true
    & $presentMonPath --process_id 1234 --v1_metrics --no_track_input --output_stdout |
        ForEach-Object { $framesCsv.WriteLine($_) }
    if ($LASTEXITCODE -ne 0) { Write-Error "PresentMon завершился с кодом $LASTEXITCODE." }
} finally {
    $framesCsv.Dispose()
}
```

3. В «Настройки → FPS и экранная панель» укажите тот же PID и выберите CSV. AirDesk читает новые строки после подключения. FPS вычисляется как 1000, делённое на средний интервал между вызовами Present у цепочки с наибольшим числом кадров за период. Можно отдельно вывести медианный интервал. Эти показатели описывают темп передачи кадров и интервал между ними. Задержку от ввода до экрана они не измеряют.
4. Отдельно включите OSD и выберите угол основного монитора, масштаб, непрозрачность и метрики. Окно пропускает указатель и не забирает фокус. После прекращения потока FPS показывает прочерк. Кнопка «Отключить FPS» останавливает чтение, а отключение панели закрывает окно. Оба режима по умолчанию выключены. CSV и PID не сохраняются между запусками.

В PresentMon 2.6.0 параметр `--output_file` блокирует одновременное чтение CSV. Для живого FPS используйте `--output_stdout` со сценарием выше. [Запись CSV в PresentMon 2.6.0](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/PresentMon/CsvOutput.cpp#L1314-L1333), [режим открытия файла в Windows](https://learn.microsoft.com/en-us/cpp/c-runtime-library/reference/fopen-s-wfopen-s?view=msvc-170)

Состояние источника показано в настройках FPS. При ошибке чтения появляется прочерк. Если новые замеры перестали поступать, последнее значение истекает через три секунды. После временного сбоя AirDesk продолжает проверять выбранный файл и возобновляет расчёт, когда появляются пригодные кадры. Ошибки запуска или завершения PresentMon проверяйте в том же окне PowerShell. При отказе ETW там будет указана причина. Обычные датчики AirDesk продолжают работать. [Диагностика ошибок PresentMon 2.6.0](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/PresentMon/MainThread.cpp#L331-L352)

| Среда | Что подтверждено |
|---|---|
| DXGI и D3D9 | Официальные трассы PresentMon 2.6.0 обработаны локально. Есть кадры DXGI и D3D9, композиция DWM, Independent Flip и Legacy Flip |
| Оконный и безрамочный режимы | WPF-панель и чтение непрерывного CSV проверены в оконной среде Windows 10. Безрамочный режим зависит от композиции Windows |
| Эксклюзивный полноэкранный режим | PresentMon может записывать кадры. Обычное окно OSD может быть скрыто полноэкранным приложением |
| Vulkan, OpenGL, конкретные игры и античиты | Нужны отдельные проверки. AirDesk не внедряет код в игры и не пытается обходить ограничения |

Результаты замера OSD и ограничения проверки есть в [протоколе](validation.md).
