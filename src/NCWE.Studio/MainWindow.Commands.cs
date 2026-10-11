// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NCWE.Core;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;
using Microsoft.Windows.Storage.Pickers;

namespace NCWE.Studio;

public sealed partial class MainWindow
{
    // --- Edit commands -------------------------------------------------------------------------

    private void OnUndo(object sender, RoutedEventArgs e) { if (!IsTextInputFocused()) Session?.Undo(); }
    private void OnRedo(object sender, RoutedEventArgs e) { if (!IsTextInputFocused()) Session?.Redo(); }
    private void OnDuplicate(object sender, RoutedEventArgs e) => Session?.DuplicateSelected();
    private void OnDelete(object sender, RoutedEventArgs e) => Session?.DeleteSelected();
    private void OnResetTransform(object sender, RoutedEventArgs e) => Session?.ResetSelected();
    private void OnDropToGround(object sender, RoutedEventArgs e) { if (!IsTextInputFocused()) Session?.DropSelectedToGround(); }
    private void OnDeselect(object sender, RoutedEventArgs e) => Session?.Select(null);
    private void OnFrameSelection(object sender, RoutedEventArgs e) => Session?.FrameSelection();
    private void OnDismissError(object sender, RoutedEventArgs e) { if (Session is not null) Session.Error = ""; }

    // --- Project commands -------------------------------------------------------------------------

    private void UpdateProjectPanel()
    {
        if (Session is not { } s) return;
        var changes = s.Document.Project.Changes;
        if (FocusManager.GetFocusedElement(Root.XamlRoot) != ProjectNameBox) ProjectNameBox.Text = s.Document.Project.Name;
        ProjectPathText.Text = s.ProjectPath
            ?? (CloudLinked ? Loc.T("In your Open//77 cloud: Ctrl+S saves a new version there (Save as… keeps a file on this PC).")
            : _live is { } live ? Loc.F("Live session {0}: everyone's edits arrive as they are made. Save to your cloud or as a file to keep a copy.", live.Code)
            : Loc.T("Not saved yet. Autosave keeps a recovery copy every 30 seconds."));
        int moved = changes.Modified.Values.Count(m => !m.Deleted), deleted = changes.Modified.Values.Count(m => m.Deleted);
        int folders = (changes.Outliner ?? ProjectOutliner.Empty).Folders.Count;
        int drawn = changes.Traffic?.Values.Count(l => l.IsCreated) ?? 0, lanes = (changes.Traffic?.Count ?? 0) - drawn;
        ProjectChanges.Text = Loc.F("{0} added · {1} elements · {2} collisions · {3} roads · {4} traffic lane{5} drawn\n{6} game objects moved · {7} deleted · {8} game lane{9} changed · {10} folder{11}{12}", changes.Added.Count, changes.Elements.Count, changes.Collisions.Count, changes.Roads.Count, drawn, (drawn == 1 ? "" : "s"), moved, deleted, lanes, (lanes == 1 ? "" : "s"), folders, (folders == 1 ? "" : "s"), (s.Document.IsDirty ? Loc.T(" · unsaved changes") : ""));
        GamePathText.Text = s.Index?.GameDirectory ?? _settings.Game ?? Loc.T("No installation selected");
        GameIndexText.Text = s.Index is null ? "" : Loc.F("{0:N0} meshes · {1} vanilla archives · {2:N0} streaming sectors", s.Index.Meshes.Length, s.Index.ArchiveCount, s.Spatial?.Count ?? 0);
        OpenExportButton.IsEnabled = InstallButton.IsEnabled = s.LastExportDirectory is not null;
    }

    private void OnProjectNameKey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { CommitProjectName(); e.Handled = true; ViewportFocus.Focus(FocusState.Programmatic); }
    }

    private void OnProjectNameCommit(object sender, RoutedEventArgs e) => CommitProjectName();

    // The title bar: a click on the world's name edits it in place (Enter or leaving keeps it, Esc cancels).
    private void OnProjectTitleClick(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        ProjectTitleBox.Text = s.Document.Project.Name;
        ProjectTitleButton.Visibility = Visibility.Collapsed; ProjectTitleBox.Visibility = Visibility.Visible;
        ProjectTitleBox.Focus(FocusState.Programmatic); ProjectTitleBox.SelectAll();
    }

    private void OnProjectTitleEnter(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => ProjectTitleIcon.Opacity = .8;
    private void OnProjectTitleExit(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => ProjectTitleIcon.Opacity = 0;

    private void OnProjectTitleKey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { EndTitleEdit(keep: true); e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.Escape) { EndTitleEdit(keep: false); e.Handled = true; }
    }

    private void OnProjectTitleLost(object sender, RoutedEventArgs e) => EndTitleEdit(keep: true);

    internal void EndTitleEdit(bool keep)
    {
        if (ProjectTitleBox.Visibility != Visibility.Visible) return;
        ProjectTitleBox.Visibility = Visibility.Collapsed; ProjectTitleButton.Visibility = Visibility.Visible; ProjectTitleIcon.Opacity = 0;
        if (keep && Session is { } s && ProjectTitleBox.Text.Trim() is { Length: > 0 } name && name != s.Document.Project.Name)
        {
            if (s.Document.ReadOnly) { s.Status = Loc.T("You are a viewer in this session: only editors rename the world."); ViewportFocus.Focus(FocusState.Programmatic); return; }
            s.Document.Rename(name);
            s.Status = s.ProjectPath is null ? Loc.F("World renamed “{0}” (saved with the project).", name) : Loc.F("World renamed “{0}”: save the project to keep it (Ctrl+S).", name);
            UpdateProjectPanel();
        }
        ViewportFocus.Focus(FocusState.Programmatic);
    }

    // Automation (tests): the same as clicking the title and typing a name.
    internal object RenameFromTitle(string? name)
    {
        OnProjectTitleClick(this, new RoutedEventArgs());
        bool editing = ProjectTitleBox.Visibility == Visibility.Visible;
        if (name is not null) { ProjectTitleBox.Text = name; EndTitleEdit(keep: true); }
        return new { ok = true, editing, name = Session?.Document.Project.Name, title = ProjectTitle.Text, dirty = Session?.Document.IsDirty };
    }

    private void CommitProjectName()
    {
        if (Session is not { } s || ProjectNameBox.Text.Trim() is not { Length: > 0 } name || name == s.Document.Project.Name) return;
        if (s.Document.ReadOnly) { ProjectNameBox.Text = s.Document.Project.Name; s.Status = Loc.T("You are a viewer in this session: only editors rename the world."); return; }
        s.Document.Rename(name);
        s.Status = Loc.T("Project renamed (saved with the project).");
    }
    private async void OnBrowseGame(object sender, RoutedEventArgs e)
    {
        if (await PickFolder(PickerLocationId.ComputerFolder) is not { } folder) return;
        if (!GameLocator.IsInstallation(folder)) { WelcomeSubtitle.Text = _gameError = Loc.T("That folder does not contain bin\\x64\\Cyberpunk2077.exe."); if (Session is not null) Session.Error = WelcomeSubtitle.Text; return; }
        await OpenGame(folder);
    }

    private async void OnUseDetectedGame(object sender, RoutedEventArgs e) { if (_detectedGame is not null) await OpenGame(_detectedGame); }

    private async void OnNewProject(object sender, RoutedEventArgs e)
    {
        if (Session is null || !await ConfirmDiscardChanges()) return;
        Session.NewProject(); UpdateProjectPanel(); _entitiesDirty = true;
    }

    private async void OnOpenProject(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s || !await ConfirmDiscardChanges()) return;
        if (await PickFile(PickerLocationId.DocumentsLibrary, [".json"]) is not { } file) return;
        await s.OpenProjectAsync(file); UpdateProjectPanel(); _entitiesDirty = true;
    }

    private async void OnSaveProject(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        // A shared world: its session goes into the world. A world from the cloud with no file here:
        // a new version there.
        if (InWorld) { await SaveWorldAsync(automatic: false); return; }
        if (s.ProjectPath is null && CloudLinked) { await SaveToCloudAsync(note: null, interactive: false); return; }
        if (s.ProjectPath is null) { await SaveAs(); return; }
        try { s.SaveProject(s.ProjectPath); } catch (Exception ex) { s.Error = ex.Message; }
        UpdateProjectPanel();
    }

    private async void OnSaveProjectAs(object sender, RoutedEventArgs e) => await SaveAs();

    private async Task SaveAs()
    {
        if (Session is not { } s) return;
        if (await PickSaveFile(PickerLocationId.DocumentsLibrary, s.Document.Project.Name + ".ncwe", Loc.T("NCWE world project"), [".json"]) is not { } file) return;
        var path = file.EndsWith(".ncwe.json", StringComparison.OrdinalIgnoreCase) ? file : Path.ChangeExtension(file, null).TrimEnd('.') + ".ncwe.json";
        // A dialog that created the file it was given leaves it empty: not kept under the other name.
        if (path != file && File.Exists(file) && new FileInfo(file).Length == 0) { try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        try { s.SaveProject(path); } catch (Exception ex) { s.Error = ex.Message; }
        UpdateProjectPanel();
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (Session is null) return;
        if (await PickFolder(PickerLocationId.DocumentsLibrary) is not { } folder) return;
        await ExportWithWindow(folder);
    }

    // Open77: writes (or updates) a world resource folder such as
    // <server>\resources\assets\maps\ncwe_local, remembered per project.
    private async void OnExportOpen77(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        string? folder = s.Open77ResourceFolder is { } known && Directory.Exists(known) ? known : null;
        if (folder is not null)
        {
            var choice = await new ContentDialog
            {
                XamlRoot = Root.XamlRoot, Title = Loc.T("Export for Open77"),
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Text = Loc.F("Update the world resource\n{0}\n\nIts package is replaced and its version raised.", folder) },
                PrimaryButtonText = Loc.T("Export"), SecondaryButtonText = Loc.T("Another folder…"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary
            }.ShowAsync();
            if (choice == ContentDialogResult.None) return;
            if (choice == ContentDialogResult.Secondary) folder = null;
        }
        if (folder is null)
        {
            if (await PickFolder(PickerLocationId.ComputerFolder) is not { } picked) return;
            folder = picked;
            // A parent folder (e.g. resources\assets\maps): create the resource folder inside it.
            if (!NCWE.Export.Open77ResourceExporter.IsResourceName(Path.GetFileName(folder)))
            {
                string suggested = "ncwe_" + new string(s.Document.Project.Name.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '_').ToArray()).Trim('_');
                var box = new TextBox { Text = suggested.Length > 5 ? suggested[..Math.Min(suggested.Length, 48)] : "ncwe_world", Header = Loc.T("Resource name (lowercase letters, digits, _ or -)") };
                var create = await new ContentDialog
                {
                    XamlRoot = Root.XamlRoot, Title = Loc.T("New Open77 resource"),
                    Content = new StackPanel { Spacing = 10, Children = { new TextBlock { TextWrapping = TextWrapping.Wrap, Text = Loc.F("\"{0}\" is not a resource name. Create the resource folder in\n{1}", Path.GetFileName(folder), folder) }, box } },
                    PrimaryButtonText = Loc.T("Create and export"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary
                }.ShowAsync();
                if (create != ContentDialogResult.Primary) return;
                string name = box.Text.Trim();
                if (!NCWE.Export.Open77ResourceExporter.IsResourceName(name)) { s.Error = Loc.F("\"{0}\" is not a valid resource name: lowercase letters, digits, _ or -, starting with a letter or digit.", name); return; }
                folder = Path.Combine(folder, name);
                Directory.CreateDirectory(folder);
            }
        }
        await ExportOpen77WithWindow(folder);
    }

    // In the export window, which then tells what the resource holds.
    internal async Task ExportOpen77WithWindow(string folder)
    {
        if (Session is not { } s) return;
        var window = new ExportWindow(this, Loc.F("Exporting “{0}” for Open77", s.Document.Project.Name), s.CancelExport);
        window.Show();
        var report = await s.ExportOpen77Async(folder, window.Progress);
        UpdateProjectPanel();
        if (report is null)
        {
            if (window.Cancelled || s.LastExportCancelled || s.ShutdownRequested) window.Close();
            else window.Fail(Loc.T("Open77 export failed"), s.Error.Length > 0 ? s.Error : s.Status);
            await window.Closed;
            return;
        }
        string entries = string.Join("\n", report.PackageEntries.Select(p => "   " + p));
        string server = report.Notes.Count == 0 ? "" : "\n\nServer\n" + string.Join("\n", report.Notes.Select(n => "• " + n));
        window.Succeed(report.ManifestCreated ? Loc.T("Open77 resource created") : Loc.T("Open77 resource updated"),
            Loc.F("Resource  {0}  {1}\nRequired mod  {2}\n{3}\n\nPackage  {4}  ({5:N0} bytes)\nsha256 {6}\n{7}{8}\n\nNext: restart the dedicated server (preloads are read when it boots), then start the game from the Open77 launcher: it installs the world before Cyberpunk starts.", report.ResourceName, report.Version, report.RequiredModId, report.ResourceDirectory, Path.GetRelativePath(report.ResourceDirectory, report.PackagePath), report.PackageBytes, report.PackageSha256, entries, server),
            (Loc.T("Open folder"), () => Process.Start(new ProcessStartInfo(report.ResourceDirectory) { UseShellExecute = true })));
        await window.Closed;
    }

    private async void OnExportWorldBuilder(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        if (await PickFolder(PickerLocationId.DocumentsLibrary) is not { } folder || s.ExportWorldBuilder(folder) is not { } report) return;
        await new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.T("World Builder export"),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                Text = Loc.F("{0} objects written to\n{1}\n\nTarget: {2}\n\n{3}", report.ExportedNodes, report.JsonPath, report.TargetImporter, string.Join("\n", report.Limitations.Select(l => "• " + l))) },
            CloseButtonText = Loc.T("OK")
        }.ShowAsync();
    }

    private void OnOpenExportFolder(object sender, RoutedEventArgs e)
    {
        if (Session?.LastExportDirectory is { } folder) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private async void OnInstall(object sender, RoutedEventArgs e) => await InstallLastExport();

    private async Task InstallLastExport()
    {
        if (Session is not { } s) return;
        if (Process.GetProcessesByName("Cyberpunk2077").Length != 0) { s.Error = Loc.T("Close Cyberpunk 2077 before installing a mod."); return; }
        await s.InstallLastExportAsync();
    }

    private async void OnRemoveMod(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        if (await PickFile(PickerLocationId.DocumentsLibrary, [".json"]) is { } file) s.RemoveMod(file);
    }

    private async void OnRecover(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        Directory.CreateDirectory(s.RecoveryDirectory);
        var latest = new DirectoryInfo(s.RecoveryDirectory).EnumerateFiles("*.ncwe.json").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (latest is null) { s.Status = Loc.T("There is no autosave to recover."); return; }
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.T("Recover autosave"),
            Content = Loc.F("Open the most recent autosave from {0:g}?\n{1}", latest.LastWriteTime, latest.FullName),
            PrimaryButtonText = Loc.T("Recover"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) { await s.OpenProjectAsync(latest.FullName); UpdateProjectPanel(); }
    }

    // Help ▸ Controls: the keyboard shortcuts window, at the view and the navigation.
    private async void OnShowControls(object sender, RoutedEventArgs e) => await ShowShortcutsAsync(Loc.T("View and navigation"));
}
