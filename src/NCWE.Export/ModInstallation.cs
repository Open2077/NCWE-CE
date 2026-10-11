// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text.Json;
using NCWE.Core;
using NCWE.Formats;

namespace NCWE.Export;

public sealed record ModReceipt(int SchemaVersion, Guid ProjectId, string GameDirectory, string PackageDirectory,
    DateTimeOffset InstalledUtc, PackageFile[] Files);

public static class ModInstallation
{
    public static async Task<string> InstallAsync(string packageDirectory, GameArchiveIndex index, string receiptDirectory,
        CancellationToken cancellation = default)
    {
        RequireGameStopped();
        var manifest = JsonSerializer.Deserialize<ExportManifest>(File.ReadAllText(Path.Combine(packageDirectory, "manifest.json")), ProjectStorage.JsonOptions)
            ?? throw new InvalidDataException("Export manifest is missing.");
        var project = ProjectStorage.Read(Path.Combine(packageDirectory, "project.ncwe.json"));
        if (manifest.SchemaVersion != 1 || manifest.ProjectId != project.Id || manifest.ArchiveProfile != project.ArchiveProfile ||
            !manifest.SourceBaselines.OrderBy(s => s.Hash).SequenceEqual(project.Sources.Values.OrderBy(s => s.Hash)))
            throw new InvalidDataException("The package manifest and project baseline differ.");
        var version = FileVersionInfo.GetVersionInfo(Path.Combine(index.GameDirectory, "bin", "x64", "Cyberpunk2077.exe"));
        if (version.FileVersion != manifest.GameFileVersion || version.ProductVersion != manifest.GameProductVersion)
            throw new InvalidDataException("Game version changed since export. Reopen and export the project again.");
        string xl = Path.Combine(index.GameDirectory, "red4ext", "plugins", "ArchiveXL", "ArchiveXL.dll");
        if (!File.Exists(xl) || FileVersionInfo.GetVersionInfo(xl).FileVersion?.StartsWith(ArchiveXlConfiguration.TargetVersion + ".", StringComparison.Ordinal) != true)
            throw new InvalidOperationException($"This export targets ArchiveXL {ArchiveXlConfiguration.TargetVersion}. Install the matching version before testing it.");
        var sources = new Dictionary<ulong, SectorSnapshot>();
        foreach (var source in project.Sources.Values) sources.Add(source.Hash, await index.LoadSectorAsync(source.Hash, cancellation));
        var errors = ExportPreparation.Validate(project, sources);
        if (!errors.IsEmpty) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        foreach (var mesh in manifest.MeshDependencies)
        {
            var current = await index.LoadMeshAsync(mesh.Reference.Hash, cancellation);
            if (current.SourceHash != mesh.Fingerprint) throw new InvalidDataException("Mesh dependency changed since export: " + mesh.Reference.Path);
        }
        cancellation.ThrowIfCancellationRequested(); RequireGameStopped();
        Directory.CreateDirectory(receiptDirectory);
        // Game root is part of the receipt name, allowing distinct installations.
        string gameId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(index.GameDirectory.ToUpperInvariant())))[..12];
        string receiptPath = Path.Combine(receiptDirectory, $"{gameId}-{project.Id:N}.json");
        InstallFiles(packageDirectory, manifest, index.GameDirectory, receiptPath);
        return receiptPath;
    }

    // File transaction independently testable without game assets. Normal UI/CLI
    // installation goes through InstallAsync, including fresh source validation.
    public static void InstallFiles(string packageDirectory, ExportManifest manifest, string gameDirectory, string receiptPath)
    {
        ValidateFileList(manifest.ProjectId, manifest.Files);
        string package = Path.GetFullPath(packageDirectory), game = Path.GetFullPath(gameDirectory);
        if (ModExporter.IsWithin(package, game) || ModExporter.IsWithin(receiptPath, game)) throw new InvalidOperationException("Package and receipt must be stored outside the game installation.");
        if (package.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("An unfinished export cannot be installed.");
        var targets = manifest.Files.Select(f => (File: f, Source: Path.Combine(package, f.RelativePath), Target: Path.Combine(game, f.RelativePath))).ToArray();
        foreach (var entry in targets)
        {
            if (File.Exists(entry.Target)) throw new IOException("Existing file will not be overwritten: " + entry.Target);
            VerifyFile(entry.Source, entry.File);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(receiptPath))!);
        var receipt = new ModReceipt(1, manifest.ProjectId, game, package, DateTimeOffset.UtcNow, manifest.Files);
        // Create the receipt before any mod files, so an interrupted installation
        // can still remove exactly the successfully installed files.
        using (var stream = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, receipt, ProjectStorage.JsonOptions); stream.Flush(true); }
        foreach (var entry in targets)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(entry.Target)!);
            string temporary = entry.Target + ".ncwe-" + Guid.NewGuid().ToString("N") + ".pending";
            try
            {
                File.Copy(entry.Source, temporary, false); VerifyFile(temporary, entry.File);
                File.Move(temporary, entry.Target, false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public static void Uninstall(string receiptPath, string gameDirectory)
    {
        RequireGameStopped();
        UninstallFiles(receiptPath, gameDirectory);
    }

    // Matching file-only transaction for deterministic synthetic tests. UI/CLI
    // use Uninstall, which first checks the running game process.
    public static void UninstallFiles(string receiptPath, string gameDirectory)
    {
        var receipt = JsonSerializer.Deserialize<ModReceipt>(File.ReadAllText(receiptPath), ProjectStorage.JsonOptions)
            ?? throw new InvalidDataException("Invalid installation receipt.");
        if (receipt.SchemaVersion != 1 || !Path.GetFullPath(receipt.GameDirectory).Equals(Path.GetFullPath(gameDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Receipt belongs to a different game installation.");
        ValidateFileList(receipt.ProjectId, receipt.Files);
        var files = receipt.Files.Select(f => (File: f, Path: Path.Combine(receipt.GameDirectory, f.RelativePath))).ToArray();
        // Validate every surviving file before deleting any. Changed files remain
        // untouched, even when their names match this project's namespace.
        foreach (var file in files) if (File.Exists(file.Path)) VerifyFile(file.Path, file.File);
        foreach (var file in files) if (File.Exists(file.Path)) File.Delete(file.Path);
        File.Delete(receiptPath);
    }

    private static void ValidateFileList(Guid project, PackageFile[] files)
    {
        string stem = $"archive/pc/mod/ncwe_{project:N}.archive";
        if (project == Guid.Empty || files.Length is < 1 or > 2 || files.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length ||
            !files.Any(f => f.RelativePath == stem + ".xl") || files.Any(f => (f.RelativePath != stem && f.RelativePath != stem + ".xl") || f.Bytes <= 0 || f.Sha256.Length != 64))
            throw new InvalidDataException("Package does not contain a valid, isolated NCWE mod file list.");
    }
    private static void VerifyFile(string path, PackageFile expected)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expected.Bytes || ModExporter.HashFile(path) != expected.Sha256)
            throw new InvalidDataException("File is missing or changed; no overwrite/removal permitted: " + path);
    }
    private static void RequireGameStopped()
    {
        var games = Process.GetProcessesByName("Cyberpunk2077");
        try { if (games.Length != 0) throw new InvalidOperationException("Close Cyberpunk 2077 before installing or removing a test mod."); }
        finally { foreach (var game in games) game.Dispose(); }
    }
}
