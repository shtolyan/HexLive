using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace HexLive.AgentStudio;

public sealed class StudioStrings : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private Dictionary<string, string> _values = new();
    public string Language { get; private set; } = "ru";
    public StudioStrings() => SetLanguage(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en");
    public string this[string key] => _values.TryGetValue(key, out var text) ? text : key;
    public void SetLanguage(string language)
    {
        Language = language == "ru" ? "ru" : "en";
        using var stream = typeof(StudioStrings).Assembly.GetManifestResourceStream(
            $"HexLive.AgentStudio.Strings.{Language}.json")!;
        _values = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
    }
}
