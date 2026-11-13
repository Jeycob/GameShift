using System.Runtime.Versioning;
using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace GameShift.Core;

public sealed record InstalledGame(string Store, string Name, string Directory, string? LaunchExecutable);
public sealed record GameLibraryScanResult(IReadOnlyList<InstalledGame> Games, IReadOnlyList<string> Warnings);
public sealed record GogGameRegistration(string? Name, string? InstallPath, string? LaunchExecutable);
public sealed record GameLibrarySources
{
    public IReadOnlyList<string> SteamRoots { get; init; } = [];
    public IReadOnlyList<string> EpicManifestDirectories { get; init; } = [];
    public IReadOnlyList<GogGameRegistration> GogGames { get; init; } = [];
}

/// <summary>Reads store metadata only. Never launches executables or recursively searches disks.</summary>
public static class GameLibraryScanner
{
    private const int MaxManifestBytes = 2 * 1024 * 1024;
    private const int MaxScanBytes = 32 * 1024 * 1024;
    private const int MaxManifests = 2000;
    private const int MaxLibraries = 64;
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> HelperNames = new(["steam", "steamwebhelper", "gameoverlayui", "epicgameslauncher", "galaxyclient", "galaxyclientservice", "goggalaxy", "dxsetup", "setup", "install", "installer", "uninstall", "updater", "patcher", "crashreportclient", "unitycrashhandler64", "unitycrashhandler32"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> HelperDirectories = new(["_commonredist", "redist", "redistributables", "prerequisites", "installers", "easyanticheat", "battleye"], StringComparer.OrdinalIgnoreCase);
    private static readonly string[] HelperPrefixes = ["unins", "vcredist", "vc_redist", "easyanticheat", "battleye", "crashreport", "crashhandler"];
    private static readonly Lazy<HashSet<string>> GenericRoots = new(() => new(new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
        Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData }
        .Select(Environment.GetFolderPath).Where(x => x.Length > 0).Select(Path.TrimEndingDirectorySeparator), Paths));

    [SupportedOSPlatform("windows")]
    public static GameLibraryScanResult ScanInstalled()
    {
        var steam = new List<string>();
        var gog = new List<GogGameRegistration>();
        var warnings = new List<string>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using (var key = registry.OpenSubKey(@"SOFTWARE\Valve\Steam"))
                {
                    foreach (var value in new[] { "SteamPath", "InstallPath" })
                        if (key?.GetValue(value) is string root && !string.IsNullOrWhiteSpace(root)) steam.Add(root);
                    if (key?.GetValue("SteamExe") is string exe && SafeFullPath(exe) is { } full)
                        steam.Add(Path.GetDirectoryName(full)!);
                }
                using var games = registry.OpenSubKey(@"SOFTWARE\GOG.com\Games");
                if (games is null) continue;
                foreach (var name in games.GetSubKeyNames().Order(StringComparer.Ordinal).Take(MaxManifests))
                {
                    if (gog.Count >= MaxManifests) break;
                    using var game = games.OpenSubKey(name);
                    if (game is null) continue;
                    gog.Add(new(game.GetValue("gameName") as string ?? game.GetValue("name") as string,
                        game.GetValue("path") as string ?? game.GetValue("InstallPath") as string,
                        game.GetValue("exe") as string));
                }
            }
            catch (Exception e) when (Expected(e)) { Warn(warnings, "A game-store registry location could not be read: " + e.GetType().Name); }
        }
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (programFiles.Length > 0) steam.Add(Path.Combine(programFiles, "Steam"));
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var result = Scan(new GameLibrarySources
        {
            SteamRoots = steam,
            EpicManifestDirectories = programData.Length == 0 ? [] : [Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests")],
            GogGames = gog
        });
        return result with { Warnings = warnings.Concat(result.Warnings).Take(100).ToArray() };
    }

    /// <summary>Injectable metadata locations allow fixture tests without registry or installed-game access.</summary>
    public static GameLibraryScanResult Scan(GameLibrarySources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.SteamRoots is null || sources.EpicManifestDirectories is null || sources.GogGames is null)
            throw new ArgumentException("Game-library source collections must not be null.");
        var warnings = new List<string>();
        var games = new Dictionary<string, InstalledGame>(Paths);
        var budget = new ReadBudget(warnings);
        var roots = new SortedSet<string>(Paths);
        foreach (var candidate in sources.SteamRoots.Take(MaxLibraries))
        {
            if (SafeDirectory(candidate) is not { } root) continue;
            roots.Add(root);
            var folders = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(folders)) continue;
            try
            {
                var document = Vdf.Parse(budget.Read(folders));
                if (!document.TryGetValue("libraryfolders", out var node) || node.Children is null) continue;
                foreach (var entry in node.Children.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    if (!uint.TryParse(entry.Key, out _)) continue;
                    var value = entry.Value.Children is { } child ? Text(child, "path") : entry.Value.Value;
                    if (roots.Count >= MaxLibraries) { Warn(warnings, "Steam library count limit reached."); break; }
                    if (SafeDirectory(value) is { } library) roots.Add(library);
                }
            }
            catch (Exception e) when (Expected(e)) { Warn(warnings, "A Steam library list was skipped: " + e.GetType().Name); }
        }
        foreach (var library in roots)
        {
            var metadata = SafeDirectory(Path.Combine(library, "steamapps"));
            if (metadata is null) continue;
            foreach (var file in Manifests(metadata, "appmanifest_*.acf", warnings))
            {
                if (budget.Exhausted) break;
                try
                {
                    var document = Vdf.Parse(budget.Read(file));
                    if (!document.TryGetValue("AppState", out var state) || state.Children is not { } app) continue;
                    var install = Text(app, "installdir");
                    if (!SafeRelative(install)) { Warn(warnings, "An unsafe Steam installation path was ignored."); continue; }
                    var directory = SafeDirectory(Path.Combine(library, "steamapps", "common", install!));
                    Add("Steam", Text(app, "name"), directory, null);
                }
                catch (Exception e) when (Expected(e)) { Warn(warnings, "A Steam app manifest was skipped: " + e.GetType().Name); }
            }
        }
        foreach (var metadata in sources.EpicManifestDirectories.Take(MaxLibraries).Select(SafeDirectory).OfType<string>().Distinct(Paths).Order(Paths))
        foreach (var file in Manifests(metadata, "*.item", warnings))
        {
            if (budget.Exhausted) break;
            try
            {
                using var json = JsonDocument.Parse(budget.Read(file), new JsonDocumentOptions { MaxDepth = 32 });
                if (json.RootElement.ValueKind != JsonValueKind.Object) continue;
                var directory = SafeDirectory(JsonText(json.RootElement, "InstallLocation"));
                Add("Epic", JsonText(json.RootElement, "DisplayName"), directory,
                    LaunchPath(directory, JsonText(json.RootElement, "LaunchExecutable")));
            }
            catch (Exception e) when (Expected(e)) { Warn(warnings, "An Epic app manifest was skipped: " + e.GetType().Name); }
        }
        foreach (var registration in sources.GogGames.Take(MaxManifests))
        {
            if (registration is null) continue;
            var directory = SafeDirectory(registration.InstallPath);
            Add("GOG", registration.Name, directory, LaunchPath(directory, registration.LaunchExecutable));
        }
        return new(games.Values.OrderBy(x => x.Store, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Directory, Paths).ToArray(), warnings.ToArray());

        void Add(string store, string? name, string? directory, string? executable)
        {
            if (directory is null || IsGenericRoot(directory) || string.IsNullOrWhiteSpace(name) || name.Length > 300 || name.Any(char.IsControl) || HelperGame(name, directory)) return;
            if (games.TryGetValue(directory, out var previous))
            {
                if (previous.LaunchExecutable is null && executable is not null) games[directory] = previous with { LaunchExecutable = executable };
            }
            else if (games.Count < MaxManifests) games.Add(directory, new(store, name.Trim(), directory, executable));
            else Warn(warnings, "Installed game count limit reached.");
        }
    }

    public static bool IsGameExecutable(string executable, IReadOnlyList<InstalledGame> games)
    {
        ArgumentNullException.ThrowIfNull(games);
        if (SafeFullPath(executable) is not { } full || !full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || HelperExecutable(full)) return false;
        foreach (var game in games)
        {
            if (game is null || string.IsNullOrWhiteSpace(game.Directory) || !Inside(full, game.Directory)) continue;
            var directory = SafeFullPath(game.Directory);
            if (directory is null || IsGenericRoot(directory) || !Inside(full, directory)) continue;
            // Metadata was validated during scanning. Recheck only a matching
            // game's ancestry, not every library for every running process.
            if (SafeDirectory(directory) is null || HasReparseComponent(full)) continue;
            return true; // Exact launch target and real binaries in subfolders are accepted.
        }
        return false;
    }

    private static IEnumerable<string> Manifests(string directory, string pattern, List<string> warnings)
    {
        try
        {
            var files = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Take(MaxManifests + 1).ToArray();
            if (files.Length > MaxManifests) Warn(warnings, "A game manifest directory exceeded the scan limit.");
            return files.Take(MaxManifests).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception e) when (Expected(e)) { Warn(warnings, "A game manifest directory could not be read: " + e.GetType().Name); return []; }
    }

    private static string? LaunchPath(string? directory, string? executable)
    {
        if (directory is null || string.IsNullOrWhiteSpace(executable)) return null;
        var full = Path.IsPathFullyQualified(executable) ? SafeFullPath(executable)
            : SafeRelative(executable) ? SafeFullPath(Path.Combine(directory, executable)) : null;
        return full is not null && Inside(full, directory) && full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !HasReparseComponent(full) && File.Exists(full) && !HelperExecutable(full) ? full : null;
    }

    private static string? SafeDirectory(string? value)
    {
        var full = SafeFullPath(value);
        return full is not null && !HasReparseComponent(full) && Directory.Exists(full) ? full : null;
    }
    private static string? SafeFullPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32700 || value.Any(char.IsControl) || value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal)) return null;
        if (value.Split(['\\', '/']).Any(x => x is "." or "..") || !Path.IsPathFullyQualified(value)) return null;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network) return null;
            return full;
        }
        catch (Exception e) when (Expected(e)) { return null; }
    }
    private static bool SafeRelative(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length < 2048 &&
        !Path.IsPathRooted(value) && !value.Contains(':') && !value.Any(char.IsControl) && !value.Split(['\\', '/']).Any(x => x is "." or "..");
    private static bool HasReparseComponent(string full)
    {
        try
        {
            var ancestry = new Stack<string>();
            for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current)) ancestry.Push(current);
            foreach (var current in ancestry)
            {
                // Inspect ancestors first, without Exists() following a junction
                // into a possibly remote target before its reparse bit is read.
                try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true; }
                catch (FileNotFoundException) { return false; }
                catch (DirectoryNotFoundException) { return false; }
            }
            return false;
        }
        catch (Exception e) when (Expected(e)) { return true; }
    }
    private static bool Inside(string executable, string directory) => executable.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool IsGenericRoot(string directory)
    {
        if (Paths.Equals(Path.TrimEndingDirectorySeparator(directory), Path.TrimEndingDirectorySeparator(Path.GetPathRoot(directory)!))) return true;
        return GenericRoots.Value.Contains(directory);
    }
    private static bool HelperGame(string name, string directory) => name.Equals("Steamworks Common Redistributables", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(directory).Equals("Steamworks Shared", StringComparison.OrdinalIgnoreCase);
    private static bool HelperExecutable(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable);
        if (HelperNames.Contains(name)) return true;
        if (HelperPrefixes.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase)) || name.EndsWith("launcher", StringComparison.OrdinalIgnoreCase)) return true;
        return executable.Split(['\\', '/']).Any(HelperDirectories.Contains);
    }
    private static string? Text(Dictionary<string, Vdf.Node> value, string key) => value.TryGetValue(key, out var node) ? node.Value : null;
    private static string? JsonText(JsonElement value, string key) => value.TryGetProperty(key, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;
    private static bool Expected(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or SecurityException or ArgumentException or FormatException or JsonException or NotSupportedException;
    private static void Warn(List<string> warnings, string message) { if (warnings.Count < 100 && !warnings.Contains(message, StringComparer.Ordinal)) warnings.Add(message); }

    private sealed class ReadBudget(List<string> warnings)
    {
        private int count;
        private long bytes;
        public bool Exhausted => count >= MaxManifests || bytes >= MaxScanBytes;
        public string Read(string file)
        {
            if (Exhausted) { Warn(warnings, "Game metadata scan budget reached."); throw new InvalidDataException("Scan budget reached."); }
            count++;
            if (SafeFullPath(file) is null || HasReparseComponent(file)) throw new InvalidDataException("Unsafe metadata path.");
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length > MaxManifestBytes || input.Length > MaxScanBytes - bytes) throw new InvalidDataException("Metadata is too large.");
            var snapshotLength = (int)input.Length;
            bytes += snapshotLength;
            var buffer = new byte[snapshotLength + 1];
            var size = 0;
            while (size < buffer.Length)
            {
                var read = input.Read(buffer, size, buffer.Length - size);
                if (read == 0) break;
                size += read;
            }
            if (size > snapshotLength) throw new InvalidDataException("Metadata grew during the scan.");
            using var snapshot = new MemoryStream(buffer, 0, size, writable: false);
            using var text = new StreamReader(snapshot, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return text.ReadToEnd();
        }
    }

    private static class Vdf
    {
        internal sealed record Node(string? Value, Dictionary<string, Node>? Children);
        private enum Kind { Word, Open, Close }
        private sealed record Token(Kind Kind, string Text);
        public static Dictionary<string, Node> Parse(string text)
        {
            var tokens = new List<Token>();
            for (var i = 0; i < text.Length;)
            {
                if (tokens.Count > 100000) throw new FormatException("Too many VDF tokens.");
                if (char.IsWhiteSpace(text[i]) || text[i] == '\uFEFF') { i++; continue; }
                if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
                if (text[i] is '{' or '}') { tokens.Add(new(text[i] == '{' ? Kind.Open : Kind.Close, "")); i++; continue; }
                if (text[i] == '"')
                {
                    i++; var value = new StringBuilder(); var closed = false;
                    while (i < text.Length)
                    {
                        var c = text[i++];
                        if (c == '"') { closed = true; break; }
                        if (c == '\\' && i < text.Length)
                        {
                            var escaped = text[i++];
                            if (escaped is '\\' or '"') value.Append(escaped);
                            else { value.Append('\\'); value.Append(escaped); }
                        }
                        else value.Append(c);
                    }
                    if (!closed) throw new FormatException("Unterminated VDF string.");
                    tokens.Add(new(Kind.Word, value.ToString()));
                }
                else
                {
                    var start = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}')) i++;
                    tokens.Add(new(Kind.Word, text[start..i]));
                }
            }
            var position = 0;
            return Object(0, false);
            Dictionary<string, Node> Object(int depth, bool nested)
            {
                if (depth > 16) throw new FormatException("VDF nesting limit reached.");
                var result = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
                while (position < tokens.Count)
                {
                    var key = tokens[position++];
                    if (key.Kind == Kind.Close && nested) return result;
                    if (key.Kind != Kind.Word || position >= tokens.Count) throw new FormatException("Invalid VDF key/value pair.");
                    var value = tokens[position++];
                    result[key.Text] = value.Kind switch
                    {
                        Kind.Open => new(null, Object(depth + 1, true)),
                        Kind.Word => new(value.Text, null),
                        _ => throw new FormatException("Invalid VDF value.")
                    };
                }
                if (nested) throw new FormatException("Unclosed VDF object.");
                return result;
            }
        }
    }
}
