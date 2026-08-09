using System.Diagnostics;
using System.Text;
using HaloMeister.App.Localization;
using HaloMeister.App.Models;

namespace HaloMeister.App.Services;

public sealed class NativeTagModExportService
{
    private static readonly string[] OverlayExtensions = [".utoc", ".ucas", ".pak"];
    private readonly RuntimeTagModService _tagMods = new();

    public async Task<NativeTagModExportResult> ExportAsync(
        RuntimeTagModDocument document,
        string requestedUtoc,
        string? definitionsDirectory = null)
    {
        string exporter = ResolveExporter();
        string paks = ResolvePaksDirectory();
        string definitions =
            RuntimeTagDefinitionLocator.ResolveCampaignEvolved(definitionsDirectory);

        string output = EnsurePrioritySuffix(requestedUtoc);
        string sidecar = Path.ChangeExtension(output, ".hmtagmod");
        string temporary = Path.Combine(
            Path.GetTempPath(), $"halomeister-{Guid.NewGuid():N}.hmtagmod");
        try
        {
            _tagMods.Save(document, temporary);
            var start = new ProcessStartInfo
            {
                FileName = exporter,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            start.ArgumentList.Add("--paks");
            start.ArgumentList.Add(paks);
            start.ArgumentList.Add("--definitions");
            start.ArgumentList.Add(definitions);
            start.ArgumentList.Add("--mod");
            start.ArgumentList.Add(temporary);
            start.ArgumentList.Add("--output");
            start.ArgumentList.Add(output);

            using Process process = Process.Start(start)
                ?? throw new InvalidOperationException(L.Get("change_biped.error_exporter_missing"));
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(timeout.Token);
            string outputText = (await stdout).Trim();
            string errorText = (await stderr).Trim();
            if (process.ExitCode != 0)
                throw new InvalidDataException(
                    string.IsNullOrWhiteSpace(errorText)
                        ? L.Get("change_biped.error_export_failed")
                        : L.Format("change_biped.error_export_failed_detail", errorText));

            string ucas = Path.ChangeExtension(output, ".ucas");
            string pak = Path.ChangeExtension(output, ".pak");
            if (!File.Exists(output) || !File.Exists(ucas) || !File.Exists(pak))
                throw new IOException(L.Get("change_biped.error_export_incomplete"));
            _tagMods.Save(document, sidecar);
            return new NativeTagModExportResult(
                output, ucas, pak, sidecar, outputText);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch { }
        }
    }

    public NativeTagModInstallResult InstallOverlay(string sourceUtoc)
        => InstallOverlayCore(sourceUtoc, replaceExisting: false);

    /// <summary>
    /// Replaces only the named HaloMeister-managed overlay. Installation does
    /// not require the game to be closed; the pack mounts on the next launch.
    /// Replacing a currently mounted triplet may still be denied by Windows.
    /// </summary>
    public NativeTagModInstallResult ReplaceManagedOverlay(
        string sourceUtoc,
        string managedStem)
    {
        string paks = ResolvePaksDirectory();
        if (IsGameRunning && HasCompleteTriplet(paks, managedStem))
            throw new InvalidOperationException(L.Get("change_biped.character_overlay_file_in_use"));

        foreach (string extension in OverlayExtensions)
        {
            string installed = Path.Combine(paks, managedStem + extension);
            if (!File.Exists(installed)) continue;
            try { File.Delete(installed); }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                throw new InvalidOperationException(
                    L.Get("change_biped.character_overlay_file_in_use"), ex);
            }
        }

        string source = Path.GetFullPath(sourceUtoc);
        string sourceStem = Path.GetFileNameWithoutExtension(source);
        if (!sourceStem.Equals(managedStem, StringComparison.OrdinalIgnoreCase))
        {
            // Install under the managed stem even when the source filename differs.
            string tempDir = Path.Combine(Path.GetTempPath(), $"hm-overlay-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                string staged = Path.Combine(tempDir, managedStem + ".utoc");
                foreach (string extension in OverlayExtensions)
                {
                    string from = Path.ChangeExtension(source, extension);
                    string to = Path.ChangeExtension(staged, extension);
                    if (!File.Exists(from))
                        throw new FileNotFoundException(
                            L.Get("change_biped.error_export_incomplete"),
                            from);
                    File.Copy(from, to, false);
                }
                return InstallOverlayCore(staged, replaceExisting: true);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); }
                catch { }
            }
        }

        return InstallOverlayCore(source, replaceExisting: true);
    }

    public IReadOnlyList<string> RemoveManagedOverlay(string managedStem)
    {
        if (IsGameRunning)
            throw new InvalidOperationException(L.Get("change_biped.character_overlay_file_in_use"));

        string paks = ResolvePaksDirectory();
        var removed = new List<string>();
        foreach (string extension in OverlayExtensions)
        {
            string path = Path.Combine(paks, managedStem + extension);
            if (!File.Exists(path)) continue;
            try
            {
                File.Delete(path);
                removed.Add(path);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                throw new InvalidOperationException(
                    L.Get("change_biped.character_overlay_file_in_use"), ex);
            }
        }
        return removed;
    }

    public bool IsManagedOverlayInstalled(string managedStem)
    {
        string? paks = TryResolvePaksDirectory();
        return paks is not null && HasCompleteTriplet(paks, managedStem);
    }

    public string? TryResolvePaksDirectory()
    {
        try { return ResolvePaksDirectory(); }
        catch (DirectoryNotFoundException) { return null; }
    }

    public static bool HasCompleteTriplet(string directory, string stem) =>
        OverlayExtensions.All(extension =>
            File.Exists(Path.Combine(directory, stem + extension)));

    public static bool HasAnyOverlayFiles(string directory, string stem) =>
        OverlayExtensions.Any(extension =>
            File.Exists(Path.Combine(directory, stem + extension)));

    private NativeTagModInstallResult InstallOverlayCore(
        string sourceUtoc,
        bool replaceExisting)
    {
        string source = Path.GetFullPath(sourceUtoc);
        string stem = Path.GetFileNameWithoutExtension(source);
        if (!stem.EndsWith("_P", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.Get("change_biped.error_export_incomplete"));

        string[] sources =
        [
            source,
            Path.ChangeExtension(source, ".ucas"),
            Path.ChangeExtension(source, ".pak"),
        ];
        foreach (string file in sources)
            if (!File.Exists(file))
                throw new FileNotFoundException(
                    L.Get("change_biped.error_export_incomplete"),
                    file);

        string paks = ResolvePaksDirectory();
        string[] destinations = sources
            .Select(file => Path.Combine(paks, Path.GetFileName(file)))
            .ToArray();
        if (!replaceExisting)
        {
            foreach (string destination in destinations)
                if (File.Exists(destination))
                    throw new IOException(L.Get("change_biped.character_overlay_file_in_use"));
        }
        else
        {
            foreach (string destination in destinations)
            {
                if (!File.Exists(destination)) continue;
                try { File.Delete(destination); }
                catch (IOException ex) when (IsSharingViolation(ex))
                {
                    throw new InvalidOperationException(
                        L.Get("change_biped.character_overlay_file_in_use"), ex);
                }
            }
        }

        var copied = new List<string>();
        try
        {
            for (int index = 0; index < sources.Length; index++)
            {
                string temporary = destinations[index] + $".{Guid.NewGuid():N}.tmp";
                File.Copy(sources[index], temporary, false);
                File.Move(temporary, destinations[index], true);
                copied.Add(destinations[index]);
            }
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            foreach (string file in copied)
            {
                try { File.Delete(file); }
                catch { }
            }
            throw new InvalidOperationException(
                L.Get("change_biped.character_overlay_file_in_use"), ex);
        }
        catch
        {
            foreach (string file in copied)
            {
                try { File.Delete(file); }
                catch { }
            }
            throw;
        }
        return new NativeTagModInstallResult(stem, paks, destinations);
    }

    public static string EnsurePrioritySuffix(string path)
    {
        string full = Path.GetFullPath(path);
        string stem = Path.GetFileNameWithoutExtension(full);
        if (!stem.EndsWith("_P", StringComparison.OrdinalIgnoreCase))
            stem += "_P";
        return Path.Combine(
            Path.GetDirectoryName(full)!, stem + ".utoc");
    }

    private static string ResolveExporter()
    {
        string[] candidates =
        [
            Path.Combine(
                AppContext.BaseDirectory, "Assets", "Native",
                "halomeister-tagmod-exporter.exe"),
            Path.Combine(
                AppContext.BaseDirectory, "halomeister-tagmod-exporter.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(L.Get("change_biped.error_exporter_missing"));
    }

    private static bool IsGameRunning =>
        Process.GetProcessesByName("HaloCampaignEvolved").Any(process => !process.HasExited);

    private static bool IsSharingViolation(IOException ex) =>
        (ex.HResult & 0xFFFF) is 32 or 33 ||
        ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("正由另一进程使用", StringComparison.OrdinalIgnoreCase);

    private static string ResolvePaksDirectory()
    {
        string? discovered = GameInstallationService.Current.TryGetPaksDirectory();
        if (discovered is not null)
            return discovered;

        foreach (string root in CandidateGameRoots())
        {
            string full;
            try { full = Path.GetFullPath(root); }
            catch { continue; }
            string[] candidates =
            [
                Path.Combine(full, "Content", "Meteorite", "Content", "Paks"),
                Path.Combine(full, "Meteorite", "Content", "Paks"),
                Path.Combine(full, "Content", "Paks"),
                full,
            ];
            foreach (string candidate in candidates)
                if (Directory.Exists(candidate) &&
                    Directory.EnumerateFiles(candidate, "*.utoc").Any())
                    return candidate;
        }
        throw new DirectoryNotFoundException(
            L.Get("change_biped.character_overlay_game_folder_missing"));
    }

    private static IEnumerable<string> CandidateGameRoots()
        => GameInstallationService.Current.EnumerateCandidateRoots();
}

public sealed record NativeTagModExportResult(
    string UtocPath,
    string UcasPath,
    string PakPath,
    string SidecarPath,
    string ExporterMessage);

public sealed record NativeTagModInstallResult(
    string Name,
    string PaksDirectory,
    IReadOnlyList<string> InstalledFiles);
