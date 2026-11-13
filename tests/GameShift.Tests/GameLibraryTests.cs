using System.Text.Json;
using GameShift.Core;

internal static class GameLibraryTests
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "GameShift-libraries-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var steam = Path.Combine(root, "Steam");
            var library = Path.Combine(root, "Second Library");
            var steamGame = Path.Combine(library, "steamapps", "common", "My Unicode Game Ω");
            Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
            Directory.CreateDirectory(Path.Combine(library, "steamapps"));
            Directory.CreateDirectory(Path.Combine(steamGame, "bin", "x64"));
            var gameExe = Path.Combine(steamGame, "bin", "x64", "game.exe");
            File.WriteAllText(gameExe, "fixture, never executable");
            File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
                "\"libraryfolders\" { \"0\" { \"path\" \"" + Escape(steam) + "\" } \"1\" { \"path\" \"" + Escape(library) + "\" } }");
            File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_123.acf"),
                "// fixture\n\"AppState\" { \"appid\" \"123\" \"name\" \"My Unicode Game Ω\" \"installdir\" \"My Unicode Game Ω\" }");
            File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_124.acf"),
                "\"AppState\" { \"name\" \"Duplicate\" \"installdir\" \"My Unicode Game Ω\" }");
            File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_125.acf"),
                "\"AppState\" { \"name\" \"Traversal\" \"installdir\" \"../outside\" }");
            File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_126.acf"), "\"AppState\" { \"name\" \"broken");
            var epicMetadata = Path.Combine(root, "Epic Manifests");
            var epicGame = Path.Combine(root, "Epic Game");
            Directory.CreateDirectory(epicMetadata); Directory.CreateDirectory(Path.Combine(epicGame, "Binaries"));
            var epicExe = Path.Combine(epicGame, "Binaries", "epic-game.exe"); File.WriteAllText(epicExe, "fixture");
            File.WriteAllText(Path.Combine(epicMetadata, "one.item"), JsonSerializer.Serialize(new { DisplayName = "Epic Game", InstallLocation = epicGame, LaunchExecutable = Path.Combine("Binaries", "epic-game.exe") }));
            File.WriteAllText(Path.Combine(epicMetadata, "two.item"), "[]");
            File.WriteAllText(Path.Combine(epicMetadata, "oversized.item"), new string('x', 2 * 1024 * 1024 + 1));
            var gogGame = Path.Combine(root, "GOG Game"); Directory.CreateDirectory(gogGame);
            var gogExe = Path.Combine(gogGame, "gog-game.exe"); File.WriteAllText(gogExe, "fixture");
            var sources = new GameLibrarySources { SteamRoots = [steam, steam.ToUpperInvariant()], EpicManifestDirectories = [epicMetadata],
                GogGames = [new("GOG Game", gogGame, gogExe), new("Bad network", @"\\server\games", @"\\server\games\game.exe"), new("Bad relative", "relative", null)] };
            var scanned = GameLibraryScanner.Scan(sources);
            check(scanned.Games.Count == 3, "Steam/Epic/GOG discovered; duplicate, unsafe, missing and malformed entries ignored");
            check(scanned.Games.Single(x => x.Store == "Steam").Name == "My Unicode Game Ω", "Steam VDF names and Unicode preserved");
            check(scanned.Games.Single(x => x.Store == "Steam").LaunchExecutable is null, "Steam metadata never invents/executes an EXE");
            check(scanned.Games.Single(x => x.Store == "Epic").LaunchExecutable == epicExe, "Epic relative launch target resolved inside game directory");
            check(scanned.Games.Single(x => x.Store == "GOG").LaunchExecutable == gogExe, "GOG exact launch path accepted");
            check(scanned.Warnings.Count > 0, "Malformed/oversized/unsafe metadata produces bounded diagnostics");
            check(GameLibraryScanner.IsGameExecutable(gameExe, scanned.Games), "Steam binary in nested directory detected");
            check(GameLibraryScanner.IsGameExecutable(epicExe, scanned.Games), "Epic launch executable detected");
            check(GameLibraryScanner.IsGameExecutable(gogExe, scanned.Games), "GOG launch executable detected");
            check(!GameLibraryScanner.IsGameExecutable(Path.Combine(steamGame + "-other", "game.exe"), scanned.Games), "Sibling prefix is not a game path");
            foreach (var helper in new[] { "setup.exe", "unins000.exe", "GameLauncher.exe", "crashreportclient.exe", "vc_redist.x64.exe", "EasyAntiCheat_EOS.exe" })
                check(!GameLibraryScanner.IsGameExecutable(Path.Combine(steamGame, helper), scanned.Games), "Helper excluded: " + helper);
            check(!GameLibraryScanner.IsGameExecutable(Path.Combine(steamGame, "_CommonRedist", "tool.exe"), scanned.Games), "Redistribution helper directory excluded");
            check(!GameLibraryScanner.IsGameExecutable("game.exe", scanned.Games), "Relative process path rejected");
            check(!GameLibraryScanner.IsGameExecutable(@"\\server\games\game.exe", scanned.Games), "UNC executable rejected");
            check(!GameLibraryScanner.IsGameExecutable(Path.Combine(steamGame, "..", "game.exe"), scanned.Games), "Traversal process path rejected");
            check(scanned.Games.Select(x => x.Directory).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3, "Stable case-insensitive deduplication");
            check(GameLibraryScanner.Scan(new GameLibrarySources()).Games.Count == 0, "Empty sources are passive");
            var escaped = GameLibraryScanner.Scan(new GameLibrarySources { GogGames = [new("Escape", gogGame, Path.Combine(root, "outside.exe"))] });
            check(escaped.Games.Count == 1 && escaped.Games[0].LaunchExecutable is null, "Out-of-directory launch target is never trusted");
            var missing = GameLibraryScanner.Scan(new GameLibrarySources { GogGames = [new("Removed game", Path.Combine(root, "missing"), null)] });
            check(missing.Games.Count == 0, "Uninstalled/missing directories ignored");
            var drive = Path.GetPathRoot(root)!;
            check(GameLibraryScanner.Scan(new GameLibrarySources { GogGames = [new("Drive root", drive, null)] }).Games.Count == 0, "Whole drive cannot be a game installation");
            File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), "\"LibraryFolders\" { \"1\" \"" + Escape(library) + "\" }");
            check(GameLibraryScanner.Scan(new GameLibrarySources { SteamRoots = [steam] }).Games.Count == 1, "Legacy Steam libraryfolders format accepted");
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid fixture cleanup path");
            Directory.Delete(root, recursive: true);
        }
    }
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
