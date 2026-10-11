// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NCWE.Formats.GameAudio;
using Windows.System;

namespace NCWE.Studio;

// A game sound event in the library's list: click to listen, click again to stop.
public sealed partial class LibrarySoundItem(GameSoundEvent sound) : INotifyPropertyChanged
{
    internal GameSoundEvent Sound { get; } = sound;
    public string Name => Sound.Name;
    public string Detail { get; } = MainWindow.GameSoundDetail(sound);
    public string Tags { get; } = sound.Tags.Length > 0 ? string.Join(" · ", sound.Tags) : "";
    private bool _playing;
    public bool Playing { get => _playing; set { if (_playing == value) return; _playing = value; PropertyChanged?.Invoke(this, new(nameof(Glyph))); PropertyChanged?.Invoke(this, new(nameof(GlyphBrush))); } }
    public string Glyph => _playing ? "" : "";
    public Brush GlyphBrush => (Brush)Application.Current.Resources[_playing ? "AccentBrush" : "TextSecondaryBrush"];
    public event PropertyChangedEventHandler? PropertyChanged;
}

// Library ▸ Game sounds: the events of the game that play a recording (ambiences, machines,
// devices, weapons, vehicles, music), searched by name and by the game's own tags, heard at a click.
public sealed partial class LibraryWindow
{
    private const int MaxSoundRows = 2000;
    private List<LibrarySoundItem> _sounds = [];
    private List<LibrarySoundItem> _soundResults = [];
    private LibrarySoundItem? _playingSound;
    private CancellationTokenSource? _soundSearchDelay;
    private int _soundVersion;
    private bool _soundsRequested, _soundsSyncing;
    private string? _soundTag;

    private bool SoundsShown => SoundsPage.Visibility == Visibility.Visible;

    private void ShowSoundsPage(bool show)
    {
        SoundsPage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        HideHover();
        if (!_soundsRequested) { _soundsRequested = true; _ = LoadSoundsAsync(); }
        SoundSearchBox.Focus(FocusState.Programmatic);
    }

    private async Task LoadSoundsAsync()
    {
        SoundsMessage.Visibility = Visibility.Visible; SoundsMessageText.Text = Loc.T("Reading the game's sounds…");
        var sounds = await _main.GameSoundsAsync();
        if (_closed) return;
        if (sounds is null) { SoundsMessageText.Text = _main.GameSoundsError; _soundsRequested = false; return; }
        var items = await Task.Run(() => sounds.Events.Where(e => e.Playable).OrderBy(e => e.Name, StringComparer.Ordinal).Select(e => new LibrarySoundItem(e)).ToList());
        if (_closed) return;
        _sounds = items;
        // The tags most sounds carry, as a filter.
        var tags = items.SelectMany(i => i.Sound.Tags).GroupBy(t => t, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= 12)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g => new ComboBoxItem { Content = $"{g.Key}  ({g.Count():N0})", Tag = g.Key }).ToList();
        _soundsSyncing = true;
        SoundTagBox.Items.Clear();
        SoundTagBox.Items.Add(new ComboBoxItem { Content = Loc.T("Every tag"), Tag = "" });
        foreach (var tag in tags) SoundTagBox.Items.Add(tag);
        SoundTagBox.SelectedIndex = 0;
        _soundsSyncing = false;
        if (_browse.FirstOrDefault(n => n.Kind == "Sounds") is { } nav) nav.Count = items.Count;
        RefreshSounds();
    }

    private async void OnSoundSearchChanged(object sender, TextChangedEventArgs e)
    {
        _soundSearchDelay?.Cancel();
        var delay = _soundSearchDelay = new();
        try { await Task.Delay(140, delay.Token); } catch (TaskCanceledException) { return; }
        RefreshSounds();
    }

    private void OnSoundTagChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_soundsSyncing) return;
        _soundTag = (SoundTagBox.SelectedItem as ComboBoxItem)?.Tag as string is { Length: > 0 } tag ? tag : null;
        RefreshSounds();
    }

    private async void RefreshSounds()
    {
        if (_sounds.Count == 0) return;
        int version = ++_soundVersion;
        var words = SoundSearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries); string? tag = _soundTag; var all = _sounds;
        var results = await Task.Run(() => all.Where(i => (tag is null || i.Sound.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            && words.All(w => i.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Sound.Tags.Any(t => t.Contains(w, StringComparison.OrdinalIgnoreCase)))).ToList());
        if (version != _soundVersion || _closed) return;
        _soundResults = results;
        SoundList.ItemsSource = results.Count > MaxSoundRows ? results.GetRange(0, MaxSoundRows) : results;
        SoundResultCount.Text = results.Count > MaxSoundRows ? Loc.F("{0:N0} sounds, the first {1:N0} shown: search to narrow", results.Count, MaxSoundRows)
            : results.Count == 1 ? Loc.T("1 sound") : Loc.F("{0:N0} sounds", results.Count);
        SoundsMessage.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (results.Count == 0) SoundsMessageText.Text = Loc.T("No sound of the game matches. Try fewer or other words (the names are the game's: amb, dev, w_gun, v_car, mus…).");
        StatusText.Text = Loc.T("Click a sound to listen, again to stop · Enter plays the selected one · Ctrl+C copies its name");
    }

    private void OnSoundItemClick(object sender, ItemClickEventArgs e) { if (e.ClickedItem is LibrarySoundItem item) ToggleSound(item); }

    private void OnSoundListKey(object sender, KeyRoutedEventArgs e)
    {
        if (SoundList.SelectedItem is not LibrarySoundItem item) return;
        if (e.Key is VirtualKey.Enter or VirtualKey.Space) { ToggleSound(item); e.Handled = true; }
        else if (e.Key == VirtualKey.C && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(item.Name);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            StatusText.Text = Loc.F("{0} copied.", item.Name); e.Handled = true;
        }
    }

    private void OnSoundSearchKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Down || SoundList.Items.Count == 0) return;
        SoundList.SelectedIndex = Math.Max(0, SoundList.SelectedIndex); SoundList.Focus(FocusState.Keyboard); e.Handled = true;
    }

    private void ToggleSound(LibrarySoundItem item)
    {
        SoundList.SelectedItem = item;
        _ = _main.ListenGameEventAsync(item.Playing ? null : item.Name);
    }

    private void OnSoundStop(object sender, RoutedEventArgs e) => _ = _main.ListenGameEventAsync(null);

    // Per frame: the row of what plays shows a stop glyph.
    private void UpdateSoundsPage()
    {
        if (!SoundsShown && _playingSound is null) return;
        string? playing = _main.PlayingGameEvent;
        if (_playingSound is { } current && current.Name != playing) { current.Playing = false; _playingSound = null; }
        if (playing is not null && _playingSound is null && _sounds.Count > 0 && _soundResults.FirstOrDefault(i => i.Name == playing) is { } item) { item.Playing = true; _playingSound = item; }
        SoundStopButton.IsEnabled = playing is not null;
    }

    // Automation: the page's state, and searching or playing from a test.
    internal object SoundsState() => new
    {
        shown = SoundsShown, loaded = _sounds.Count, results = _soundResults.Count, query = SoundSearchBox.Text, tag = _soundTag, playing = _main.PlayingGameEvent,
        first = _soundResults.Take(5).Select(i => new { i.Name, i.Detail }).ToArray(), message = SoundsMessage.Visibility == Visibility.Visible ? SoundsMessageText.Text : null
    };
    internal void SearchSounds(string text) { SoundSearchBox.Text = text; RefreshSounds(); }
}
