// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;

namespace NCWE.Studio;

// The interface languages. English is the source text and always complete; a translation is a
// catalog (Localization/<code>.json, embedded) keyed by that English text, gettext style: a plain
// text, or a format with {0} holes for Loc.F. A text missing from a catalog shows in English.
// Code wraps what it shows in Loc.T / Loc.F; the XAML windows are translated once built
// (Loc.Translate). The language comes from the settings (Help ▸ Language; "auto" follows Windows),
// --lang <code> or NCWE_LANGUAGE override it, and a change applies at the next start.
internal static class Loc
{
    public sealed record Language(string Code, string Name, string Culture);

    public static readonly IReadOnlyList<Language> Languages =
    [
        new("en", "English", "en-US"),
        new("fr", "Français", "fr-FR"),
        new("es", "Español", "es-ES"),
        new("ja", "日本語", "ja-JP"),
        new("pl", "Polski", "pl-PL"),
        new("pt", "Português (Brasil)", "pt-BR"),
    ];

    private static Dictionary<string, string> _catalog = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _original = new(StringComparer.Ordinal);

    public static string Code { get; private set; } = "en";
    // Formatting for shown texts: the app's own numbers (it runs with the invariant culture),
    // with the language's month and day names.
    public static CultureInfo Culture { get; private set; } = CultureInfo.CurrentCulture;

    // Windows' display language (the app itself runs with the invariant culture, App).
    public static string WindowsLanguage
    {
        get
        {
            try { return CultureInfo.GetCultureInfo(GetUserDefaultUILanguage()).TwoLetterISOLanguageName; }
            catch (CultureNotFoundException) { return "en"; }
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    // "auto" (or nothing): Windows' display language when NCWE has it, else English.
    public static string Resolve(string? preference)
    {
        string wanted = string.IsNullOrWhiteSpace(preference) || preference == "auto" ? WindowsLanguage : preference.Trim().ToLowerInvariant();
        return Languages.Any(l => l.Code == wanted) ? wanted : "en";
    }

    public static void Initialize(string? preference)
    {
        Code = Resolve(preference);
        Culture = CultureInfo.CurrentCulture;
        if (Code != "en")
        {
            var culture = (CultureInfo)CultureInfo.GetCultureInfo(Languages.First(l => l.Code == Code).Culture).Clone();
            culture.NumberFormat = (NumberFormatInfo)CultureInfo.CurrentCulture.NumberFormat.Clone();
            Culture = CultureInfo.ReadOnly(culture);
        }
        _catalog = Code == "en" ? new(StringComparer.Ordinal) : Catalog(Code);
        _original = new(StringComparer.Ordinal);
        foreach (var (english, translated) in _catalog) _original.TryAdd(translated, english);
    }

    // A catalog as shipped (tests check them all).
    public static Dictionary<string, string> Catalog(string code)
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"NCWE.Studio.Localization.{code}.json");
        if (stream is null) return new(StringComparer.Ordinal);
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        return entries.Where(e => !string.IsNullOrEmpty(e.Value)).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
    }

    public static string T(string english) => _catalog.TryGetValue(english, out var translated) ? translated : english;

    public static string F(string english, params object?[] args)
    {
        try { return string.Format(Culture, T(english), args); }
        catch (FormatException) { return string.Format(Culture, english, args); }
    }

    // The English text behind a shown one (a combo box item read back by the code).
    public static string Original(string shown) => _original.TryGetValue(shown, out var english) ? english : shown;

    // --- XAML ---------------------------------------------------------------------------------

    private static readonly ConditionalWeakTable<object, object> Done = new();

    // Translates the texts written in a window's XAML: right after InitializeComponent, before
    // the code fills its own (already translated) values.
    public static void Translate(Window window)
    {
        if (Code == "en") return;
        if (!string.IsNullOrEmpty(window.Title)) window.Title = T(window.Title);
        // The language picks the fonts XAML falls back on (Japanese kanji, not Chinese ones).
        if (window.Content is FrameworkElement root) root.Language = Languages.First(l => l.Code == Code).Culture;
        Walk(window.Content);
    }

    public static void Translate(object? root)
    {
        if (Code == "en") return;
        Walk(root);
    }

    private static void Walk(object? node)
    {
        if (node is null or string || Done.TryGetValue(node, out _)) return;
        Done.Add(node, node);
        switch (node)
        {
            case MenuBar bar: foreach (var item in bar.Items) Walk(item); break;
            case MenuBarItem barItem: barItem.Title = T(barItem.Title); foreach (var item in barItem.Items) Walk(item); break;
            case MenuFlyoutSubItem sub: sub.Text = T(sub.Text); foreach (var item in sub.Items) Walk(item); break;
            case MenuFlyoutItem item: item.Text = T(item.Text); break;
            case MenuFlyout menu: foreach (var item in menu.Items) Walk(item); return;
            case Flyout flyout: Walk(flyout.Content); return;
        }
        if (node is not DependencyObject d) return;
        if (node is FrameworkElement element)
        {
            switch (ToolTipService.GetToolTip(element))
            {
                case string tip: ToolTipService.SetToolTip(element, T(tip)); break;
                case ToolTip { Content: string content } tip: tip.Content = T(content); break;
            }
            if (AutomationProperties.GetName(element) is { Length: > 0 } name) AutomationProperties.SetName(element, T(name));
            Walk(element.ContextFlyout);
            Walk(FlyoutBase.GetAttachedFlyout(element));
        }
        switch (node)
        {
            case TextBlock text:
                if (text.ReadLocalValue(TextBlock.TextProperty) is not BindingExpression) text.Text = T(text.Text);
                break;
            case TextBox box: box.Header = Header(box.Header); box.PlaceholderText = T(box.PlaceholderText); break;
            case NumberBox number: number.Header = Header(number.Header); number.PlaceholderText = T(number.PlaceholderText); break;
            case AutoSuggestBox suggest: suggest.Header = Header(suggest.Header); suggest.PlaceholderText = T(suggest.PlaceholderText); break;
            case ToggleSwitch toggle:
                toggle.Header = Header(toggle.Header);
                if (toggle.OnContent is string on) toggle.OnContent = T(on);
                if (toggle.OffContent is string off) toggle.OffContent = T(off);
                break;
            case Slider slider: slider.Header = Header(slider.Header); break;
            case ComboBox combo:
                combo.Header = Header(combo.Header); combo.PlaceholderText = T(combo.PlaceholderText);
                // String items stay English (the code reads them); they are shown translated.
                if (combo.ItemTemplate is null && combo.Items.Any(i => i is string)) combo.ItemTemplate = StringTemplate.Value;
                foreach (var item in combo.Items) Walk(item);
                break;
            case SelectorBarItem selectorItem: selectorItem.Text = T(selectorItem.Text); break;
            case SelectorBar selector: foreach (var item in selector.Items) Walk(item); break;
            case PanelSection section:
                section.Header = T(section.Header); Walk(section.HeaderActions);
                foreach (var child in section.Children) Walk(child);
                break;
            case Expander expander: expander.Header = Header(expander.Header); Walk(expander.Content); break;
            case ContentControl control:
                if (control.Content is string content) control.Content = T(content); else Walk(control.Content);
                if (control is Button { Flyout: { } buttonFlyout }) Walk(buttonFlyout);
                break;
            case UserControl user: Walk(user.Content); break;
            case Panel panel: foreach (var child in panel.Children) Walk(child); break;
            case Border border: Walk(border.Child); break;
            case Viewbox box: Walk(box.Child); break;
        }
        if (node is ItemsControl items and not ComboBox)
        {
            foreach (var item in items.Items) Walk(item);
            // Items made from a template appear later: their buttons' tips are translated as they come.
            if (items is ListViewBase list)
                list.ContainerContentChanging += (_, args) => { if (!args.InRecycleQueue) TranslateTips(args.ItemContainer.ContentTemplateRoot); };
        }
    }

    private static object? Header(object? header) => header is string text ? T(text) : header;

    // Template content: only tool tips and fixed button labels (bound texts are data).
    private static void TranslateTips(DependencyObject? root)
    {
        if (root is null) return;
        if (root is FrameworkElement element && ToolTipService.GetToolTip(element) is string tip && !Done.TryGetValue(element, out _))
        {
            Done.Add(element, element);
            ToolTipService.SetToolTip(element, T(tip));
        }
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++) TranslateTips(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i));
    }

    // Shows a string item translated (the item itself stays the English text).
    private static readonly Lazy<DataTemplate> StringTemplate = new(() =>
    {
        Application.Current.Resources["NcweLocText"] = new LocTextConverter();
        return (DataTemplate)XamlReader.Load(
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
            "<TextBlock Text=\"{Binding Converter={StaticResource NcweLocText}}\" /></DataTemplate>");
    });
}

internal sealed partial class LocTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, string language) => value is string text ? Loc.T(text) : value;
    public object? ConvertBack(object? value, Type targetType, object? parameter, string language) => value is string text ? Loc.Original(text) : value;
}
