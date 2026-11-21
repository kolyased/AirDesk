using System.ComponentModel;

namespace AirDesk;

internal sealed class DisplayMetric : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Value { get; private set; } = "—";
    public string Detail { get; private set; } = "Ожидание показаний";

    public void Set(string value, string detail)
    {
        if (Value != value) { Value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); }
        if (Detail != detail) { Detail = detail; PropertyChanged?.Invoke(this, new(nameof(Detail))); }
    }
}
