# Дизайн и архитектура

AirDesk: спокойный тёмный фон, мятный акцент для текущих показателей, четыре карточки нагрузки, минутный график, температуры и вентиляторы рядом. Управление расположено ниже мониторинга, чтобы включение было явным.

Системная область из пользовательского примера — overflow системного трея. Windows размещает там значок NotifyIcon, произвольное окно внутрь overflow встроить нельзя. AirDesk открывает собственную компактную панель над панелью задач. Перенос значка в видимую область выполняет пользователь через Windows.

Мониторинг: LibreHardwareMonitor + PawnIO. Опрос раз в секунду в рабочем потоке; аппаратные обращения сериализованы. UI обновляется через Dispatcher, скрытие окна не останавливает мониторинг. Только каналы SuperIO платы доступны для записи. Настройки управления остаются в памяти текущего запуска.

Изученные первичные источники:
- https://github.com/LibreHardwareMonitor/LibreHardwareMonitor — API Computer, поддержка аппаратуры, MPL-2.0.
- https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LibreHardwareMonitorLib/Hardware/IControl.cs — SetSoftware / SetDefault.
- https://learn.microsoft.com/en-us/dotnet/desktop/winforms/controls/notifyicon-component-overview-windows-forms — системный трей.
- https://learn.microsoft.com/en-us/windows/win32/perfctrs/about-performance-counters — источники системной нагрузки. Первая версия использует показатели LibreHardwareMonitor, а не отдельный набор Windows Performance Counters.

Будущие улучшения: пользовательские кривые, подтверждённое сопоставление разъёмов, отдельная служба мониторинга/watchdog, сохранение профилей с явным разрешением восстановления, выбор GPU, графики остальных компонентов.
