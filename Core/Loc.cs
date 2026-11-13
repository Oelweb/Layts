using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace Layts.Core;

/// <summary>
/// Übersetzungen. In XAML: <c>Text="{core:Tr Key}"</c> – aktualisiert sich beim Sprachwechsel sofort.
/// Im Code: <c>Loc.Get("Key")</c> bzw. <c>Loc.F("Key", wert)</c>.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    public static IReadOnlyList<string> Languages { get; } = new[] { "en", "de" };

    /// <summary>„en“ (Standard) oder „de“.</summary>
    public static string Language { get; private set; } = "en";

    public static bool IsGerman => Language == "de";

    public static CultureInfo Culture => IsGerman ? CultureInfo.GetCultureInfo("de-DE") : CultureInfo.GetCultureInfo("en-US");

    /// <summary>Wird nach jedem Sprachwechsel ausgelöst (für Texte, die im Code berechnet werden).</summary>
    public static event Action? Changed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Get(key);

    public static string Get(string key) =>
        Strings.Table.TryGetValue(key, out var t) ? (IsGerman ? t.De : t.En) : key;

    public static string F(string key, params object?[] args) => string.Format(Culture, Get(key), args);

    public static void SetLanguage(string? language)
    {
        var lang = language == "de" ? "de" : "en";
        if (lang == Language) return;
        Language = lang;
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
        Changed?.Invoke();
    }
}

/// <summary>XAML-Erweiterung: <c>{core:Tr Schlüssel}</c>.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class Tr : MarkupExtension
{
    public Tr() { }
    public Tr(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
