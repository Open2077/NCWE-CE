// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Studio.Session;

namespace NCWE.Studio;

// Inspector ▸ Streaming: how far from its position the game loads a project mesh (large or distant
// builds stay visible from afar). 0 keeps the exporter's automatic distance.
public sealed partial class MainWindow
{
    private NumberBox? _streamingBox;
    private TextBlock? _streamingHint;

    private void ShowStreaming(StudioSession s, ScenePlacement? selected)
    {
        var meshes = selected is null ? [] : s.SelectedIds.Select(s.Document.TryGetObject).OfType<ScenePlacement>().Where(o => o.Added is not null).ToArray();
        bool shown = meshes.Length > 0 && meshes.Length == s.SelectedIds.Count;
        StreamingSection.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown) return;
        if (_streamingBox is null)
        {
            _streamingHint = new TextBlock { Style = (Style)Application.Current.Resources["HintStyle"], TextWrapping = TextWrapping.Wrap };
            _streamingBox = new NumberBox
            {
                Header = Loc.T("Load distance (m)"), Minimum = 0, Maximum = AuthoredPlacement.MaxStreamingDistance, SmallChange = 50, LargeChange = 500,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _streamingBox.ValueChanged += OnStreamingDistance;
            StreamingPanel.Children.Add(_streamingHint);
            StreamingPanel.Children.Add(_streamingBox);
        }
        float automatic = meshes.Max(o => s.GameBounds(o) is { } b ? WorldResourceWriter.StreamingRange(b, o.Transform.Position, null) : 120);
        _streamingHint!.Text = Loc.F("How far from its position the game loads this mesh. 0: automatic ({0:0} m, from its size).", automatic);
        _streamingBox.Value = meshes[0].Added!.StreamingDistance ?? 0;
    }

    private void OnStreamingDistance(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingInspector || Session is not { } s || s.SelectedIds.Count == 0) return;
        float distance = double.IsFinite(sender.Value) ? (float)Math.Clamp(sender.Value, 0, AuthoredPlacement.MaxStreamingDistance) : 0;
        var ids = s.SelectedIds.ToArray();
        s.ApplyDocument(() => s.Document.SetStreamingDistance(ids, distance));
    }
}
