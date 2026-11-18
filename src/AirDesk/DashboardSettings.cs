using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace AirDesk;

public sealed partial class Dashboard
{
    string driverStatus = "Драйвер: проверка…";
    readonly ListBox cardOrder = new() { Margin = new(0, 2, 0, 8) };
    readonly TextBlock startupStatus = Text("", 12);
    bool rebuildingCards, readingStartup;

    FrameworkElement BuildSettings()
    {
        var body = new StackPanel { Margin = new(0, 12, 0, 0) };
        var display = new StackPanel();
        display.Children.Add(Text("Оформление", 19));
        var theme = new ComboBox { ItemsSource = new[] { "Как в Windows", "Тёмная", "Светлая" },
            SelectedIndex = (int)preferences.Theme, MaxWidth = 240, HorizontalAlignment = HorizontalAlignment.Left };
        NameForAutomation(theme, "Цветовая тема"); display.Children.Add(theme);
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedIndex < 0) return;
            preferences.Theme = (Appearance)theme.SelectedIndex; AppTheme.Apply(preferences.Theme); SavePreference();
        };
        body.Children.Add(Surface(display, new Thickness(14), new Thickness(0, 0, 0, 12)));

        var overview = new StackPanel();
        overview.Children.Add(Text("Карточки обзора", 19));
        AddWrapped(overview, "Снимите флажок, чтобы скрыть карточку. Для смены порядка выберите карточку и переместите её.");
        NameForAutomation(cardOrder, "Порядок карточек и видимость");
        var cardItems = new Style(typeof(ListBoxItem));
        cardItems.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 1, 6, 1)));
        cardItems.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 30d));
        cardOrder.ItemContainerStyle = cardItems;
        overview.Children.Add(cardOrder); PopulateCardSettings(0);
        var orderButtons = new WrapPanel(); overview.Children.Add(orderButtons);
        orderButtons.Children.Add(Button("Выше", () => MoveCard(-1)));
        orderButtons.Children.Add(Button("Ниже", () => MoveCard(1)));
        orderButtons.Children.Add(Button("Восстановить порядок", () =>
        {
            preferences.Cards = DesktopLayout.DefaultCards(); ReorderCards(); PopulateCardSettings(0); SavePreference();
        }));
        body.Children.Add(Surface(overview, new Thickness(14), new Thickness(0, 0, 0, 12)));

        var systemTray = new StackPanel();
        systemTray.Children.Add(Text("Трей и запуск", 19));
        var launch = new CheckBox { Content = "Запускать AirDesk при входе в Windows", Margin = new(0, 4, 0, 6) };
        var minimized = new CheckBox { Content = "Запускать свёрнутым в трей", IsChecked = preferences.StartMinimized, Margin = new(0, 0, 0, 8) };
        systemTray.Children.Add(launch); systemTray.Children.Add(minimized); systemTray.Children.Add(startupStatus);
        startupStatus.TextWrapping = TextWrapping.Wrap;
        void LoadStartup()
        {
            readingStartup = true;
            try
            {
                string? command = WindowsStartup.Read();
                launch.IsEnabled = WindowsStartup.Command != null;
                launch.IsChecked = command != null && command == WindowsStartup.Command;
                startupStatus.Text = command != null && command != WindowsStartup.Command
                    ? "Автозапуск настроен для другой копии AirDesk. Включите флажок, чтобы выбрать эту копию."
                    : "При входе в Windows AirDesk запускается в режиме мониторинга.";
            }
            catch (Exception error) { Monitor.Log(error); launch.IsEnabled = false; startupStatus.Text = "Не удалось прочитать настройки запуска Windows."; }
            finally { readingStartup = false; }
        }
        void SetStartup()
        {
            if (readingStartup) return;
            try { WindowsStartup.Set(launch.IsChecked == true); LoadStartup(); }
            catch (Exception error) { Monitor.Log(error); LoadStartup(); startupStatus.Text = error.Message; }
        }
        launch.Checked += (_, _) => SetStartup(); launch.Unchecked += (_, _) => SetStartup();
        minimized.Checked += (_, _) => SetMinimized(true); minimized.Unchecked += (_, _) => SetMinimized(false);
        LoadStartup();
        systemTray.Children.Add(Text("Подсказка при наведении на значок", 15));
        var tooltip = new WrapPanel(); systemTray.Children.Add(tooltip);
        foreach (var metric in Enum.GetValues<TrayMetric>())
        {
            var choice = new CheckBox { Content = TrayMetricName(metric), IsChecked = preferences.Tooltip.Contains(metric), Margin = new(0, 3, 14, 8) };
            choice.Checked += (_, _) => SetTrayMetric(metric, true); choice.Unchecked += (_, _) => SetTrayMetric(metric, false);
            tooltip.Children.Add(choice);
        }
        AddWrapped(systemTray, "Подсказка Windows вмещает только часть значений. Остальные доступны по щелчку по значку.");
        body.Children.Add(Surface(systemTray, new Thickness(14), new Thickness(0, 0, 0, 12)));

        try
        {
            driverStatus = WindowsCooling.DriverVersion() is { } version ? $"Драйвер PawnIO {version} найден. Доступ к управлению указан в статусе охлаждения."
                : "Драйвер PawnIO не обнаружен или его файл недоступен. AirDesk покажет доступные датчики.";
        }
        catch (Exception error) { Monitor.Log(error); driverStatus = "Не удалось определить версию драйвера. Проверьте диагностику."; }
        body.Children.Add(Button("Показать подсказку первого запуска", () =>
        {
            preferences.WelcomeDismissed = false; SavePreference(); welcome.Visibility = Visibility.Visible; Navigate(OverviewTab);
        }));
        return body;
    }

    void SetMinimized(bool minimized) { preferences.StartMinimized = minimized; SavePreference(); }

    void SetTrayMetric(TrayMetric metric, bool enabled)
    {
        preferences.Tooltip = enabled ? preferences.Tooltip.Append(metric).Distinct().ToArray() : preferences.Tooltip.Where(item => item != metric).ToArray();
        SavePreference();
    }

    static string CardName(OverviewCard card) => card switch
    {
        OverviewCard.Cpu => "Процессор", OverviewCard.Gpu => "Видеокарта", OverviewCard.Memory => "Память", _ => "Накопители"
    };

    void PopulateCardSettings(int selected)
    {
        rebuildingCards = true;
        cardOrder.Items.Clear();
        foreach (var setting in preferences.Cards)
        {
            var check = new CheckBox { Content = CardName(setting.Card), IsChecked = setting.Visible };
            check.GotKeyboardFocus += (_, _) => cardOrder.SelectedItem = check;
            check.Checked += (_, _) => SetCardVisibility(setting.Card, true); check.Unchecked += (_, _) => SetCardVisibility(setting.Card, false);
            cardOrder.Items.Add(check);
        }
        cardOrder.SelectedIndex = selected;
        rebuildingCards = false;
    }

    void SetCardVisibility(OverviewCard card, bool visible)
    {
        if (rebuildingCards) return;
        preferences.Cards = preferences.Cards.Select(item => item.Card == card ? item with { Visible = visible } : item).ToArray();
        ReorderCards(); SavePreference();
    }

    void MoveCard(int offset)
    {
        int from = cardOrder.SelectedIndex, to = from + offset;
        if (from < 0 || to < 0 || to >= preferences.Cards.Length) return;
        (preferences.Cards[from], preferences.Cards[to]) = (preferences.Cards[to], preferences.Cards[from]);
        ReorderCards(); PopulateCardSettings(to); SavePreference();
    }
}
