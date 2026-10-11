// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using NCWE.Core;
using NCWE.Formats.CustomSounds;
using NCWE.Studio.Audio;
using NCWE.Studio.Session;
using Path = System.IO.Path;

namespace NCWE.Studio;

// Custom sounds: the library in Add ▸ Sounds (import, listen, place), the Inspector section of a
// placed one, and the placed sounds heard around the camera (World ▸ Hear custom sounds), mixed as
// Open77 plays them. Only an Open77 export carries them.
public sealed partial class MainWindow
{
    private const string PlayGlyph = "", StopGlyph = "";
    private StudioAudio? _audioEngine;
    // Background runs mix without a device; NCWE_AUDIO_DEVICE=1 gives them the real output (checks of the sound path).
    private StudioAudio Audio => _audioEngine ??= new StudioAudio(silent: App.Background && Environment.GetEnvironmentVariable("NCWE_AUDIO_DEVICE") != "1");
    private bool _hearSounds;
    private List<CustomSoundInfo> _soundChoices = [];

    // --- Add ▸ Sounds ---------------------------------------------------------------------------------

    // Your sounds first, then copies of the game's emitters loaded around the camera.
    private static IReadOnlyList<ElementResourceItem> SoundItems(StudioSession s) =>
        s.CustomSounds.List().Select(i => new ElementResourceItem(i.Name, SoundDetail(s, i), i.Id, ElementKind.CustomSound))
            .Concat(s.SoundTemplates().Select(t => new ElementResourceItem(t.Name, t.Placement.Element!.SoundEvent.Length > 0
                ? Loc.F("game emitter · plays {0} · radius {1:0.#} m", t.Placement.Element.SoundEvent, t.Placement.Element.Radius)
                : Loc.F("game emitter · radius {0:0.#} m · copied from {1}", t.Placement.Element.Radius, t.Placement.Name), "", ElementKind.Sound, t.Placement)))
            .ToArray();

    private static string SoundDetail(StudioSession s, CustomSoundInfo info)
    {
        int uses = s.CustomSoundUses(info.Id);
        return Loc.F("your sound · Open77 · {0} · {1} {2} {3:0.#} kHz{4}", info.DurationText, info.Extension.TrimStart('.').ToUpperInvariant(), (info.Channels == 1 ? Loc.T("mono") : Loc.T("stereo")), info.SampleRate / 1000.0, (uses > 0 ? Loc.F(" · placed {0}×", uses) : ""));
    }

    private CustomSoundInfo? ChosenSound() =>
        _addCategory == "Sound" && ElementCatalogList.SelectedItem is ElementResourceItem { Kind: ElementKind.CustomSound } item ? Session?.CustomSounds.Get(item.Path) : null;

    private void UpdateSoundSelection()
    {
        var info = ChosenSound();
        // A game emitter: what it plays can be listened to; it is placed by a click in the world.
        string? gameEvent = ChosenGameSoundEvent();
        bool any = info is not null || gameEvent is not null;
        AddSelectionBar.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        SoundActions.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        SoundPlaceFrontButton.Visibility = SoundMenuButton.Visibility = info is not null ? Visibility.Visible : Visibility.Collapsed;
        AddPlaceButtons.Visibility = Visibility.Collapsed; RoadActions.Visibility = Visibility.Collapsed;
        AddAppearanceBox.Visibility = Visibility.Collapsed; CustomActions.Visibility = Visibility.Collapsed;
        if (info is null && gameEvent is not null && ElementCatalogList.SelectedItem is ElementResourceItem game && Session is { } session)
        {
            AddSelectionName.Text = game.Name;
            AddSelectionDetail.Text = Loc.F("Plays {0}{1}\nClick in the world to place a copy of this emitter.", gameEvent, session.GameSoundsReady?.Find(gameEvent) is { } found ? " · " + GameSoundDetail(found) : "");
            return;
        }
        if (info is null || Session is not { } s) return;
        AddSelectionName.Text = info.Name;
        AddSelectionDetail.Text = Loc.F("{0}\nClick in the world to place it. Open77 plays it (Open77 exports only).", SoundDetail(s, info));
    }

    private void OnSoundListen(object sender, RoutedEventArgs e)
    {
        if (ChosenSound() is { } info) PreviewSound(Audio.Previewing == Session?.CustomSounds.FilePath(info) ? null : info);
        else if (ChosenGameSoundEvent() is { } gameEvent) _ = ListenGameEventAsync(gameEvent);
    }

    // Plays a library sound once (null stops).
    internal void PreviewSound(CustomSoundInfo? info, float volume = 1)
    {
        if (info is null || Session is not { } s) { _audioEngine?.StopPreview(); return; }
        Audio.Preview(s.CustomSounds.FilePath(info), volume);
    }

    internal (string? Previewing, int Voices, string Error) AudioState() => (_audioEngine?.Previewing, _audioEngine?.SpatialVoices ?? 0, _audioEngine?.Error ?? "");

    private void OnSoundPlaceFront(object sender, RoutedEventArgs e)
    {
        if (ChosenSound() is not { } info || Session is not { } s || _viewport is null) return;
        _suppressInspectorSwitch = true;
        if (s.AddElementAt(StudioSession.SoundStamp(info), FrontOfCamera(s)) is not null) s.Status = Loc.F("{0} placed in front of you.", info.Name);
    }

    private void OnSoundMenu(object sender, RoutedEventArgs e) { if (sender is FrameworkElement button && ChosenSound() is { } info) SoundMenu(info).ShowAt(button); }

    private void OnElementCatalogRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ElementResourceItem { Kind: ElementKind.CustomSound } item || Session?.CustomSounds.Get(item.Path) is not { } info) return;
        SoundMenu(info).ShowAt(ElementCatalogList, e.GetPosition(ElementCatalogList));
        e.Handled = true;
    }

    private MenuFlyout SoundMenu(CustomSoundInfo info)
    {
        var menu = new MenuFlyout();
        if (Session is not { } s) return menu;
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Listen"), Icon = new FontIcon { Glyph = PlayGlyph } }.With(() => PreviewSound(info)));
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Rename…"), Icon = new FontIcon { Glyph = "" } }.With(() => _ = RenameSoundAsync(info)));
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Show its folder"), Icon = new FontIcon { Glyph = "" } }.With(() => ShowInExplorer(s.CustomSounds.FilePath(info))));
        if (info.Warnings.Length > 0)
            menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Import notes…"), Icon = new FontIcon { Glyph = "" } }.With(() => _ = ShowSoundNotesAsync(info)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Delete from the library…"), Icon = new FontIcon { Glyph = "" } }.With(() => _ = DeleteSoundAsync(info)));
        return menu;
    }

    private async Task RenameSoundAsync(CustomSoundInfo info)
    {
        if (Session is not { } s) return;
        var box = new TextBox { Text = info.Name, SelectionStart = 0, SelectionLength = info.Name.Length, MinWidth = 300 };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = Loc.T("Rename sound"), Content = box, PrimaryButtonText = Loc.T("Rename"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || box.Text.Trim() is not { Length: > 0 } name || name == info.Name) return;
        try { s.CustomSounds.Rename(info.Id, name); CustomSoundsChanged(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { s.Error = ex.Message; }
    }

    private async Task DeleteSoundAsync(CustomSoundInfo info)
    {
        if (Session is not { } s) return;
        int uses = s.CustomSoundUses(info.Id);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.F("Delete {0} from your sounds?", info.Name),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = Loc.F("{0}Other projects that use it lose it too.", (uses > 0 ? Loc.F("{0} sound{1} of this project play it: the Open77 export stops until they are deleted or given another sound. ", uses, (uses == 1 ? "" : "s")) : "")) },
            PrimaryButtonText = Loc.T("Delete"), CloseButtonText = Loc.T("Cancel")
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (Audio.Previewing == s.CustomSounds.FilePath(info)) Audio.StopPreview();
        try { s.CustomSounds.Delete(info.Id); CustomSoundsChanged(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { s.Error = ex.Message; }
    }

    private async Task ShowSoundNotesAsync(CustomSoundInfo info) =>
        await new ContentDialog { XamlRoot = Root.XamlRoot, Title = info.Name, CloseButtonText = Loc.T("Close"),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Text = Loc.F("Imported from {0}:\n• {1}", info.SourceFile, string.Join("\n• ", info.Warnings)) } }.ShowAsync();

    // The library changed (import, rename, delete): the Sound list and the Inspector follow.
    internal void CustomSoundsChanged()
    {
        if (_addCategory == "Sound") RefreshAddContent();
        _inspected = null;
    }

    internal void SelectSound(string id, bool arm = true)
    {
        if (ElementCatalogList.ItemsSource is not IEnumerable<ElementResourceItem> items || items.FirstOrDefault(i => i.Kind == ElementKind.CustomSound && i.Path == id) is not { } item) return;
        _syncingElements = !arm;
        try { ElementCatalogList.SelectedItem = item; }
        finally { _syncingElements = false; }
        ElementCatalogList.ScrollIntoView(item);
        UpdateAddSelection();
    }

    // --- Import ------------------------------------------------------------------------------------------

    private async void OnImportSound(object sender, RoutedEventArgs e)
    {
        var files = await PickFiles(PickerLocationId.MusicLibrary, AudioFiles.Extensions);
        if (files.Count > 0) await ImportSoundsAsync(files.ToList(), null);
    }

    // Copies (or converts) the files into the library; dropped on the viewport, they are placed there.
    internal async Task ImportSoundsAsync(IReadOnlyList<string> paths, Vector3? placeAt)
    {
        if (Session is not { } s) return;
        var imported = new List<CustomSoundInfo>();
        foreach (var path in paths)
        {
            try
            {
                var progress = new Progress<string>(m => s.Status = m);
                var info = await Task.Run(() => s.CustomSounds.Import(path, null, progress));
                imported.Add(info);
                if (placeAt is { } at) s.AddElementAt(StudioSession.SoundStamp(info), at + new Vector3(imported.Count - 1, 0, 0));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException)
            { s.Error = $"{Path.GetFileName(path)}: {ex.Message}"; }
        }
        if (imported.Count == 0) return;
        ShowAddCategory("Sound");
        SelectSound(imported[^1].Id, arm: placeAt is null);
        var last = imported[^1];
        string converted = last.Converted && last.Warnings.Length > 0 ? " · " + last.Warnings[^1] : "";
        s.Status = imported.Count == 1
            ? Loc.F("{0} ({1}) is in your sounds{2}{3}", last.Name, last.DurationText, converted, (placeAt is null ? Loc.T(": click in the world to place it.") : Loc.T(", placed where you dropped it.")))
            : Loc.F("{0} sounds imported{1}", imported.Count, (placeAt is null ? Loc.T(": pick one, then click in the world.") : Loc.T(" and placed.")));
    }

    // --- Inspector ----------------------------------------------------------------------------------------

    private void ShowCustomSound(StudioSession s, ElementProperties p)
    {
        var audio = p.Audio ?? new SoundSettings();
        var sounds = s.CustomSounds.List().ToList();
        if (!sounds.Select(i => (i.Id, i.Name)).SequenceEqual(_soundChoices.Select(i => (i.Id, i.Name))))
        {
            _soundChoices = sounds;
            CustomSoundBox.ItemsSource = sounds.Select(i => i.Name).ToList();
        }
        int index = _soundChoices.FindIndex(i => i.Id == audio.Sound);
        CustomSoundBox.SelectedIndex = index;
        CustomSoundBox.PlaceholderText = index < 0 ? Loc.T("Missing: choose a sound") : "";
        CustomSoundDetail.Text = index < 0 ? Loc.F("\"{0}\" is no longer in your sounds: choose another one, or the Open77 export stops here.", audio.Sound) : SoundDetail(s, _soundChoices[index]);
        CustomSoundVolumeBox.Value = Math.Round(audio.Volume * 100);
        CustomSoundNearBox.Value = audio.MinDistance; CustomSoundFarBox.Value = p.Radius;
        CustomSoundPlaybackBox.SelectedIndex = audio.Playback == SoundPlayback.Interval ? 1 : 0;
        CustomSoundPauseRow.Visibility = audio.Playback == SoundPlayback.Interval ? Visibility.Visible : Visibility.Collapsed;
        CustomSoundPauseMinBox.Value = audio.IntervalMin; CustomSoundPauseMaxBox.Value = audio.IntervalMax;
        CustomSoundSyncedBox.Visibility = audio.Playback == SoundPlayback.Loop ? Visibility.Visible : Visibility.Collapsed;
        CustomSoundSyncedBox.IsChecked = audio.Synced;
    }

    private void ApplyCustomSound(Func<SoundSettings, ElementProperties, ElementProperties> change, string undoName)
    {
        if (_syncingElements || Session is not { SelectedElementProperties: { Kind: ElementKind.CustomSound, Audio: { } audio } p } s) return;
        var next = change(audio, p);
        if (next != p) s.UpdateSelectedElement(next, undoName);
    }

    private void OnCustomSoundChosen(object sender, SelectionChangedEventArgs e)
    {
        int index = CustomSoundBox.SelectedIndex;
        if (index < 0 || index >= _soundChoices.Count) return;
        string id = _soundChoices[index].Id;
        ApplyCustomSound((a, p) => p with { Audio = a with { Sound = id } }, Loc.T("Change sound"));
    }

    private void OnCustomSoundNumber(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        static float F(NumberBox box, float fallback) => double.IsNaN(box.Value) ? fallback : (float)box.Value;
        ApplyCustomSound((a, p) =>
        {
            float far = Math.Clamp(F(CustomSoundFarBox, p.Radius), .1f, Open77Audio.MaxDistance);
            float near = Math.Clamp(F(CustomSoundNearBox, a.MinDistance), 0, far - .05f);
            float pauseMin = Math.Max(0, F(CustomSoundPauseMinBox, a.IntervalMin));
            return p with
            {
                Radius = far,
                Audio = a with { Volume = Math.Clamp(F(CustomSoundVolumeBox, a.Volume * 100) / 100, 0, 1), MinDistance = near, IntervalMin = pauseMin, IntervalMax = Math.Max(pauseMin, F(CustomSoundPauseMaxBox, a.IntervalMax)) }
            };
        }, sender == CustomSoundVolumeBox ? Loc.T("Edit volume") : sender == CustomSoundNearBox || sender == CustomSoundFarBox ? Loc.T("Edit sound distance") : Loc.T("Edit pause"));
    }

    private void OnCustomSoundPlayback(object sender, SelectionChangedEventArgs e) =>
        ApplyCustomSound((a, p) => p with { Audio = a with { Playback = CustomSoundPlaybackBox.SelectedIndex == 1 ? SoundPlayback.Interval : SoundPlayback.Loop, Synced = a.Synced && CustomSoundPlaybackBox.SelectedIndex != 1 } }, Loc.T("Change playback"));

    private void OnCustomSoundSynced(object sender, RoutedEventArgs e) =>
        ApplyCustomSound((a, p) => p with { Audio = a with { Synced = CustomSoundSyncedBox.IsChecked == true } }, CustomSoundSyncedBox.IsChecked == true ? Loc.T("Sync sound") : Loc.T("Unsync sound"));

    private void OnCustomSoundPlay(object sender, RoutedEventArgs e)
    {
        if (Session is not { SelectedElementProperties: { Kind: ElementKind.CustomSound, Audio: { } audio } } s || s.CustomSounds.Get(audio.Sound) is not { } info) return;
        PreviewSound(Audio.Previewing == s.CustomSounds.FilePath(info) ? null : info);
    }

    // --- Hearing the placed sounds -----------------------------------------------------------------------

    private void OnHearSoundsMenu(object sender, RoutedEventArgs e) => SetHearSounds(HearSoundsMenu.IsChecked);

    internal void SetHearSounds(bool on)
    {
        _hearSounds = on;
        if (HearSoundsMenu.IsChecked != on) HearSoundsMenu.IsChecked = on;
        if (!on) _audioEngine?.StopSpatial();
        if (Session is { } s) s.Status = on ? Loc.T("Hearing the custom sounds around the camera, as Open77 mixes them (camera = listener).") : Loc.T("Custom sounds muted.");
    }

    // Per UI tick: the placed sounds around the camera, and the play/stop buttons.
    private void UpdateSounds(StudioSession s)
    {
        string? playing = _audioEngine?.Previewing;
        bool chosenPlays = (playing is not null && ChosenSound() is { } chosen && playing == s.CustomSounds.FilePath(chosen))
            || (_audioEngine?.PreviewingEvent is { } playingEvent && ChosenGameSoundEvent() == playingEvent);
        SoundListenIcon.Glyph = chosenPlays ? StopGlyph : PlayGlyph; SoundListenText.Text = chosenPlays ? Loc.T("Stop") : Loc.T("Listen");
        bool selectedPlays = playing is not null && s.SelectedElementProperties is { Kind: ElementKind.CustomSound, Audio: { } a } && s.CustomSounds.Get(a.Sound) is { } selected && playing == s.CustomSounds.FilePath(selected);
        CustomSoundPlayIcon.Glyph = selectedPlays ? StopGlyph : PlayGlyph;
        UpdateGameSounds(s);
        if (!_hearSounds) return;
        var sources = new List<StudioAudio.Source>();
        foreach (var element in s.Elements.Values)
            if (element.Properties is { Kind: ElementKind.CustomSound, Audio: { } audio } && s.CustomSounds.Get(audio.Sound) is { } info)
                sources.Add(new(element.Id, s.CustomSounds.FilePath(info), element.Transform.Position, audio, element.Properties.Radius));
        var forward = Coordinates.ViewToGame(s.Camera.Forward);
        var right = Vector3.Cross(forward, Vector3.UnitZ);
        right = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : Vector3.UnitX;
        Audio.UpdateSpatial(sources, Coordinates.ViewToGame(s.Camera.Eye), right);
    }

    private void DisposeAudio() => _audioEngine?.Dispose();

    // Automation: scrolls the Inspector (screenshots of its lower sections).
    internal void ScrollInspector(double offset) => InspectorPanel.ChangeView(null, offset, null, true);
}
