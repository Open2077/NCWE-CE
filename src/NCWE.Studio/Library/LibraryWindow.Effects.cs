// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NCWE.Studio.Viewport;
using Windows.System;
using Path = System.IO.Path;

namespace NCWE.Studio;

// An effect or a particle system of the game in the library's list.
public sealed class LibraryEffectItem(string path)
{
    public string Path { get; } = path;
    public bool IsEffect { get; } = path.EndsWith(".effect", StringComparison.OrdinalIgnoreCase);
    public string Name { get; } = System.IO.Path.GetFileNameWithoutExtension(path);
    public string Folder { get; } = System.IO.Path.GetDirectoryName(path) ?? "";
    public string Glyph => IsEffect ? "" : "";
}

// Library ▸ Visual effects: the game's effects (.effect) and particle systems (.particle), searched
// by name and folder, played in a viewer beside the list, placed in the project at a click.
public sealed partial class LibraryWindow
{
    private const int MaxEffectRows = 3000;
    private List<LibraryEffectItem> _effects = [];
    private List<LibraryEffectItem> _effectResults = [];
    private EffectPreview? _effectPreview;
    private CancellationTokenSource? _effectSearchDelay;
    private int _effectVersion;
    private bool _effectsRequested;
    private string _effectKind = "All";

    private bool EffectsShown => EffectsPage.Visibility == Visibility.Visible;

    private void ShowEffectsPage(bool show)
    {
        EffectsPage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (_effectPreview is { } preview) preview.Active = show;
        if (!show) return;
        HideHover();
        if (!_effectsRequested) { _effectsRequested = true; _ = LoadEffectsAsync(); }
        EffectSearchBox.Focus(FocusState.Programmatic);
    }

    private async Task LoadEffectsAsync()
    {
        EffectsMessage.Visibility = Visibility.Visible; EffectsMessageText.Text = Loc.T("Reading the game's effects…");
        var index = _index;
        var items = await Task.Run(() => index.PathsWithExtension(".effect", ".particle").Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase).Select(p => new LibraryEffectItem(p)).ToList());
        if (_closed) return;
        _effects = items;
        if (_browse.FirstOrDefault(n => n.Kind == "Effects") is { } nav) nav.Count = items.Count;
        RefreshEffects();
    }

    private async void OnEffectSearchChanged(object sender, TextChangedEventArgs e)
    {
        _effectSearchDelay?.Cancel();
        var delay = _effectSearchDelay = new();
        try { await Task.Delay(140, delay.Token); } catch (TaskCanceledException) { return; }
        RefreshEffects();
    }

    private void OnEffectKind(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string kind }) return;
        _effectKind = kind;
        EffectKindAll.IsChecked = kind == "All"; EffectKindEffects.IsChecked = kind == "Effects"; EffectKindSystems.IsChecked = kind == "Systems";
        RefreshEffects();
    }

    private async void RefreshEffects()
    {
        if (_effects.Count == 0) return;
        int version = ++_effectVersion;
        var words = EffectSearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries); string kind = _effectKind; var all = _effects;
        var results = await Task.Run(() => all.Where(i => (kind == "All" || (kind == "Effects") == i.IsEffect) && words.All(w => i.Path.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList());
        if (version != _effectVersion || _closed) return;
        _effectResults = results;
        var selected = EffectList.SelectedItem as LibraryEffectItem;
        EffectList.ItemsSource = results.Count > MaxEffectRows ? results.GetRange(0, MaxEffectRows) : results;
        if (selected is not null && results.Contains(selected)) EffectList.SelectedItem = selected;
        EffectResultCount.Text = results.Count > MaxEffectRows ? Loc.F("{0:N0} effects, the first {1:N0} shown: search to narrow", results.Count, MaxEffectRows)
            : results.Count == 1 ? Loc.T("1 effect") : Loc.F("{0:N0} effects", results.Count);
        EffectsMessage.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (results.Count == 0) EffectsMessageText.Text = Loc.T("No effect of the game matches. Try fewer or other words (fire, smoke, steam, sparks, dust, water, explosion…).");
        StatusText.Text = Loc.T("Click an effect to play it · drag to turn around it, wheel to zoom · Enter or double-click places it in front of the camera");
    }

    private void OnEffectSelected(object sender, SelectionChangedEventArgs e)
    {
        if (EffectList.SelectedItem is not LibraryEffectItem item) return;
        ShowEffect(item);
    }

    private void ShowEffect(LibraryEffectItem item)
    {
        if (_effectPreview is { Failed: true } broken) { broken.Dispose(); _effectPreview = null; }
        if (_effectPreview is null)
        {
            _effectPreview = new EffectPreview(EffectPanel, _index);
            _effectPreview.InfoChanged += () => { if (!_closed && _effectPreview is { } p) EffectInfo.Text = p.Info; };
            // The effect's own sounds, heard as it reaches them.
            _effectPreview.Sound = name => { if (EffectSoundsBox.IsChecked == true && EffectsShown) _ = _main.ListenGameEventAsync(name, restart: true); };
        }
        _effectPreview.Active = EffectsShown; _effectPreview.Paused = false; EffectPauseButton.IsChecked = false;
        _effectPreview.Show(item.Path);
        EffectPanelHint.Visibility = Visibility.Collapsed;
        EffectName.Text = item.Name; EffectPath.Text = item.Path; EffectPath.Visibility = Visibility.Visible; EffectPlaceButton.IsEnabled = true;
    }

    private void OnEffectRestart(object sender, RoutedEventArgs e) => _effectPreview?.Restart();
    private void OnEffectPause(object sender, RoutedEventArgs e) { if (_effectPreview is { } p) p.Paused = EffectPauseButton.IsChecked == true; }

    private void OnEffectPlace(object sender, RoutedEventArgs e) => PlaceSelectedEffect();
    private void OnEffectDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => PlaceSelectedEffect();

    private void PlaceSelectedEffect()
    {
        if (EffectList.SelectedItem is not LibraryEffectItem item) return;
        StatusText.Text = _main.PlaceEffectInFront(item.Path) ? Loc.F("{0} placed in front of the editor's camera.", item.Name) : Loc.T("It could not be placed: open a project first.");
    }

    private void OnEffectSearchKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Down && EffectList.Items.Count > 0) { EffectList.SelectedIndex = Math.Max(0, EffectList.SelectedIndex); EffectList.Focus(FocusState.Keyboard); e.Handled = true; }
        else if (e.Key == VirtualKey.Enter && EffectList.SelectedItem is not null) { PlaceSelectedEffect(); e.Handled = true; }
    }

    private void DisposeEffects() { _effectPreview?.Dispose(); _effectPreview = null; }

    // Automation.
    internal object EffectsState() => new
    {
        shown = EffectsShown, loaded = _effects.Count, results = _effectResults.Count, query = EffectSearchBox.Text, kind = _effectKind,
        first = _effectResults.Take(5).Select(i => i.Path).ToArray(), selected = (EffectList.SelectedItem as LibraryEffectItem)?.Path, preview = _effectPreview?.Diagnostic(),
        message = EffectsMessage.Visibility == Visibility.Visible ? EffectsMessageText.Text : null
    };
    internal void SearchEffects(string text) { EffectSearchBox.Text = text; RefreshEffects(); }
    internal void SelectEffect(int index) { if (index >= 0 && index < _effectResults.Count) { EffectList.SelectedItem = _effectResults[index]; EffectList.ScrollIntoView(_effectResults[index]); } }
    internal (byte[] Pixels, int Width, int Height)? CaptureEffect() => _effectPreview?.Capture();
}
