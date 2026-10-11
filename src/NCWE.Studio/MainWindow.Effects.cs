// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using Microsoft.UI.Xaml;
using NCWE.Core;
using NCWE.Core.Fx;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;
using Path = System.IO.Path;

namespace NCWE.Studio;

// Visual effects in the editor's interface: playing those around the camera (World menu), what a
// selected particle system or effect is made of and a button to play it again (Inspector), a
// preview of the one chosen in Add ▸ Effects in front of the camera, and the sounds effects play.
public sealed partial class MainWindow
{
    private void OnPlayEffectsMenu(object sender, RoutedEventArgs e) => SetPlayEffects(PlayEffectsMenu.IsChecked);

    internal void SetPlayEffects(bool on)
    {
        if (Session is not { } s) return;
        s.PlayEffects = on;
        if (PlayEffectsMenu.IsChecked != on) PlayEffectsMenu.IsChecked = on;
        SaveSettings();
        s.Status = on ? Loc.T("Visual effects play around the camera.") : Loc.T("Visual effects stopped.");
    }

    internal void SetEffectExposure(float exposure) { if (_viewport is { } v) v.Renderer.ParticleExposure = Math.Clamp(exposure, .001f, 100); }
    internal void SetLightGradientExposure(float exposure) { if (_viewport is { } v) v.Renderer.LightGradientExposure = Math.Clamp(exposure, 0, 100); }

    // The session tells which sound an effect reaches: heard when the game's sounds are (or for the
    // effect being previewed), from where the effect is.
    private void ConnectEffectSounds(StudioSession s)
    {
        s.EffectSound = (name, position) =>
        {
            if (!_hearGameSounds && s.EffectPreviewPath is null) return;
            if (s.GameSoundsReady is not { } sounds) { if (!s.GameSoundsLoading) _ = s.GameSoundsAsync(); return; }
            if (sounds.Find(name) is not { Playable: true } info) return;
            var listener = Coordinates.ViewToGame(s.Camera.Eye);
            var forward = Coordinates.ViewToGame(s.Camera.Forward);
            var right = Vector3.Cross(forward, Vector3.UnitZ);
            right = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : Vector3.UnitX;
            Audio.PlayAt(sounds, name, position, listener, right, Math.Clamp(info.MaxAttenuation > 0 ? info.MaxAttenuation : 30, 5, 150));
        };
    }

    // --- Inspector ------------------------------------------------------------------------------------

    private string _shownEffect = "\0";
    private void ShowEffectPlay(StudioSession s, ScenePlacement selected, ElementProperties p)
    {
        bool effect = p.Kind is ElementKind.Particle or ElementKind.Effect && p.Resource.Length > 0;
        EffectPlayRow.Visibility = effect ? Visibility.Visible : Visibility.Collapsed;
        if (!effect || s.Index is not { } index) { _shownEffect = "\0"; return; }
        if (_shownEffect == p.Resource) return;
        _shownEffect = p.Resource;
        EffectPlayInfo.Text = Loc.T("Reading…");
        string resource = p.Resource;
        _ = Task.Run(() => DescribeEffect(index, resource)).ContinueWith(t =>
        {
            string text = t.IsCompletedSuccessfully ? t.Result : Loc.T("It could not be read.");
            DispatcherQueue.Post(() => { if (_shownEffect == resource) EffectPlayInfo.Text = text; });
        }, TaskScheduler.Default);
    }

    // What the view plays of a particle system or an effect, in a sentence.
    internal static string DescribeEffect(NCWE.Formats.GameArchiveIndex index, string resource)
    {
        if (resource.EndsWith(".effect", StringComparison.OrdinalIgnoreCase))
        {
            if (index.ReadEffect(resource) is not { } effect) return Loc.T("This effect is not in the game's files.");
            var systems = effect.Particles.Select(i => index.ReadParticleSystem(i.System)).OfType<FxSystem>().ToList();
            int emitters = systems.Sum(x => x.Emitters.Length), drawn = systems.Sum(x => x.Emitters.Count(e => e.Drawable));
            return Loc.F("Played in the view: {0} of {1} emitters{2}{3}{4}", drawn, emitters, effect.Loop is not null ? Loc.T(" · loops") : Loc.F(" · {0:0.#} s, then again", effect.Length),
                effect.Sounds.Length > 0 ? Loc.F(" · plays {0}", string.Join(", ", effect.Sounds.Select(x => x.Event).Distinct().Take(3))) : "",
                effect.Skipped.Length > 0 ? Loc.F(" · not shown: {0}", string.Join(", ", effect.Skipped)) : "");
        }
        if (index.ReadParticleSystem(resource) is not { } system) return Loc.T("This particle system is not in the game's files.");
        return Loc.F("Played in the view: {0}{1}", FxBatches.Coverage(system), system.OneShot ? Loc.T(" · plays once, then again") : Loc.T(" · loops"));
    }

    private void OnEffectRestart(object sender, RoutedEventArgs e)
    {
        if (Session is { } s && s.SelectedIds.Count > 0) foreach (var id in s.SelectedIds) s.RestartEffect(id);
    }

    // --- Add ▸ Effects: the one chosen in the list ----------------------------------------------------

    private ElementResourceItem? ChosenVfx() => _addCategory == "Vfx" ? ElementCatalogList.SelectedItem as ElementResourceItem : null;

    private void UpdateVfxSelection()
    {
        var item = ChosenVfx();
        AddSelectionBar.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        VfxActions.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        SoundActions.Visibility = Visibility.Collapsed; AddPlaceButtons.Visibility = Visibility.Collapsed; RoadActions.Visibility = Visibility.Collapsed;
        AddAppearanceBox.Visibility = Visibility.Collapsed; CustomActions.Visibility = Visibility.Collapsed;
        if (item is null || Session is not { } s) return;
        AddSelectionName.Text = item.Name;
        AddSelectionDetail.Text = Loc.F("{0}\nPreview plays it in front of you; click in the world to place it.", item.Detail);
        bool previewing = s.EffectPreviewPath == item.Path;
        VfxPreviewIcon.Glyph = previewing ? StopGlyph : PlayGlyph; VfxPreviewText.Text = previewing ? Loc.T("Stop preview") : Loc.T("Preview it");
        // Another effect chosen while one is previewed: the preview follows the choice.
        if (s.EffectPreviewPath is { } shown && shown != item.Path && _vfxPreviewFollows) s.PreviewEffect(item.Path);
    }

    private bool _vfxPreviewFollows;
    private void OnVfxPreview(object sender, RoutedEventArgs e)
    {
        if (ChosenVfx() is not { } item || Session is not { } s) return;
        bool stop = s.EffectPreviewPath == item.Path;
        s.PreviewEffect(stop ? null : item.Path); _vfxPreviewFollows = !stop;
        s.Status = stop ? Loc.T("Preview stopped.") : Loc.F("{0} plays in front of you (not placed).", item.Name);
        UpdateVfxSelection();
    }

    private void OnVfxPlaceFront(object sender, RoutedEventArgs e)
    {
        if (ChosenVfx() is { } item) PlaceEffectInFront(item.Path);
    }

    // Places a particle system or an effect of the game in front of the camera (the library's button too).
    internal bool PlaceEffectInFront(string path)
    {
        if (Session is not { } s || _viewport is null) return false;
        var kind = path.EndsWith(".effect", StringComparison.OrdinalIgnoreCase) ? ElementKind.Effect : ElementKind.Particle;
        string name = Path.GetFileNameWithoutExtension(path);
        _suppressInspectorSwitch = true;
        if (s.AddElementAt(new StudioSession.ElementStamp(name, new ElementProperties(kind) { Resource = path }), FrontOfCamera(s)) is null) return false;
        s.Status = Loc.F("{0} placed in front of you.", name);
        return true;
    }

    // Automation: Add ▸ Effects with this effect chosen in the list (and its preview started).
    internal bool ChooseVfx(string path, bool preview)
    {
        ShowTab("Add"); ShowAddCategory("Vfx");
        AddSearch.Text = Path.GetFileNameWithoutExtension(path);
        RefreshAddContent();
        if (ElementCatalogList.ItemsSource is not IEnumerable<ElementResourceItem> items || items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)) is not { } item) return false;
        ElementCatalogList.SelectedItem = item; ElementCatalogList.ScrollIntoView(item);
        UpdateAddSelection();
        if (preview && Session?.EffectPreviewPath != item.Path) OnVfxPreview(this, new RoutedEventArgs());
        return true;
    }
}
