using System.ComponentModel;
using System.Diagnostics;
using GameShift.Core;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace GameShift.App;

// Only explicitly chosen executable paths are eligible. Never identify AI by a
// generic name such as python.exe or node.exe; these can belong to other apps.
internal sealed class PriorityController : IDisposable
{
    private sealed record Saved(int Id, long StartTime, ProcessPriorityClass Original);
    private readonly Dictionary<int, Saved> saved = [];

    public int Apply(string[] paths, bool lower)
    {
        if ((!lower || paths.Length == 0) && saved.Count == 0) return 0;
        var selected = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<int>();
        var failures = 0;
        foreach (var process in lower && selected.Count > 0 ? Process.GetProcesses() : [])
        {
            using (process)
            {
                try
                {
                    if (!lower || !IsSelected(process, selected)) continue;
                    seen.Add(process.Id);
                    var started = process.StartTime.ToUniversalTime().Ticks;
                    if (saved.TryGetValue(process.Id, out var old) && old.StartTime != started) saved.Remove(process.Id);
                    if (saved.ContainsKey(process.Id)) continue;
                    var original = process.PriorityClass;
                    // Already-low processes do not need modification or restoration.
                    if (original is ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal) continue;
                    saved[process.Id] = new(process.Id, started, original);
                    process.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
                { failures++; }
            }
        }
        foreach (var pair in saved.ToArray())
        {
            if (lower && seen.Contains(pair.Key)) continue;
            if (Restore(pair.Value)) saved.Remove(pair.Key); else failures++;
        }
        return failures;
    }

    public static bool GameRunning(string[] paths, IReadOnlyList<InstalledGame>? installed = null)
    {
        if (paths.Length == 0 && (installed is null || installed.Count == 0)) return false;
        var selected = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var result = false;
        using var current = Process.GetCurrentProcess();
        var interactiveSession = current.SessionId;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != interactiveSession) continue;
                    if (IsSelected(process, selected)) { result = true; continue; }
                    if (installed is { Count: > 0 } && ExecutablePath(process) is { } location && GameLibraryScanner.IsGameExecutable(location, installed)) result = true;
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return result;
    }

    private static bool IsSelected(Process process, HashSet<string> selected)
    {
        // Cheap name prefilter avoids opening every process's executable module.
        if (!selected.Any(p => Path.GetFileNameWithoutExtension(p).Equals(process.ProcessName, StringComparison.OrdinalIgnoreCase))) return false;
        return ExecutablePath(process) is { } path && selected.Contains(path);
    }

    private static string? ExecutablePath(Process process)
    {
        using var handle = OpenProcess(0x1000, false, process.Id); // Query-limited; never requires debug privilege.
        if (handle.IsInvalid) return null;
        var buffer = new StringBuilder(32768);
        var length = buffer.Capacity;
        return QueryFullProcessImageName(handle, 0, buffer, ref length) ? buffer.ToString() : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder fileName, ref int size);

    private static bool Restore(Saved item)
    {
        try
        {
            using var process = Process.GetProcessById(item.Id);
            if (process.StartTime.ToUniversalTime().Ticks != item.StartTime) return true;
            // Respect changes made by another application after our adjustment.
            if (process.PriorityClass == ProcessPriorityClass.BelowNormal) process.PriorityClass = item.Original;
            return true;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (Win32Exception) { return false; }
    }

    public void Dispose()
    {
        foreach (var item in saved.Values) Restore(item);
        saved.Clear();
    }
}
