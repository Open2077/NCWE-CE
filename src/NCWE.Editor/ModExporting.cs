// SPDX-License-Identifier: GPL-3.0-only
using NCWE.Export;

namespace NCWE.Editor;

internal sealed partial class EditorWindow
{
    private string? _lastExportDirectory;
    private void ExportModDialog()
    {
        string folder = Path.Combine(Program.StateDirectory, "Exports"); Directory.CreateDirectory(folder);
        using var picker = new FolderBrowserDialog { Description = "Choose an export folder outside the game installation", InitialDirectory = folder, UseDescriptionForTitle = true };
        if (picker.ShowDialog(this) == DialogResult.OK) _ = ExportMod(picker.SelectedPath);
    }

    private async Task ExportMod(string folder, AutomationReply? response = null)
    {
        if (_busy || _document is null || _index is null)
        { response?.SetResult(new { ok = false, error = "Open a world project and wait for the current operation to finish." }); return; }
        _busy = true; _error = "";
        var project = _document.Project; // An immutable export snapshot, independent of subsequent edits.
        try
        {
            var result = await ModExporter.ExportAsync(project, _index, folder, new Progress<string>(s => _status = s), _shutdown.Token);
            _lastExportDirectory = result.Directory;
            _status = $"Exported {result.Manifest.Transformed} transforms, {result.Manifest.Deleted} deletions, {result.Manifest.Added} additions. Game verification pending. Folder: {result.Directory}";
            response?.SetResult(new { ok = true, result.Directory, result.ManifestPath, result.Manifest });
        }
        catch (OperationCanceledException) { response?.SetResult(new { ok = false, error = "Export cancelled." }); }
        catch (Exception ex) { _error = ex.Message; _status = "Export failed"; response?.SetResult(new { ok = false, error = ex.Message }); }
        finally { EndOperation(); }
    }

    private async Task InstallExport(string package, AutomationReply? response = null)
    {
        if (_busy || _index is null) { response?.SetResult(new { ok = false, error = "Wait for the game archive index to become available." }); return; }
        _busy = true; _error = ""; _status = "Revalidating the export before installation...";
        try
        {
            string receipt = await ModInstallation.InstallAsync(package, _index, Path.Combine(Program.StateDirectory, "InstalledMods"), _shutdown.Token);
            _status = "NCWE mod installed. Start the game to check the changes. Removal receipt: " + receipt;
            response?.SetResult(new { ok = true, receipt });
        }
        catch (OperationCanceledException) { response?.SetResult(new { ok = false, error = "Installation cancelled." }); }
        catch (Exception ex) { _error = ex.Message; response?.SetResult(new { ok = false, error = ex.Message }); }
        finally { EndOperation(); }
    }
    private void RemoveModDialog()
    {
        string folder = Path.Combine(Program.StateDirectory, "InstalledMods"); Directory.CreateDirectory(folder);
        using var picker = new OpenFileDialog { Title = "Select an NCWE installation receipt", InitialDirectory = folder, Filter = "NCWE installation receipts|*.json" };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try { RemoveMod(picker.FileName); } catch (Exception ex) { _error = ex.Message; }
    }
    private void RemoveMod(string receipt)
    {
        if (_index is null || _busy) throw new InvalidOperationException("Wait for the game archive index to become available.");
        ModInstallation.Uninstall(receipt, _index.GameDirectory);
        _status = "Removed the verified files belonging to this NCWE mod."; _error = "";
    }
}
