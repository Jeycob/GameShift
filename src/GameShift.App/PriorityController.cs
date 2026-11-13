using System.ComponentModel;
using System.Diagnostics;

namespace GameShift.App;

// Only explicitly chosen executable paths are eligible. Never identify AI by a
// generic name such as python.exe or node.exe; these can belong to other apps.
internal sealed class PriorityController : IDisposable
{
    private sealed record Saved(int Id, long StartTime, ProcessPriorityClass Original);
    private readonly Dictionary<int, Saved> saved = [];

    public int Apply(string[] paths, bool lower)
    {
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

    public static bool GameRunning(string[] paths)
    {
        if (paths.Length == 0) return false;
        var selected = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var result = false;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { result |= IsSelected(process, selected); }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return result;
    }

    private static bool IsSelected(Process process, HashSet<string> selected)
    {
        // Cheap name prefilter avoids opening every process's executable module.
        if (!selected.Any(p => Path.GetFileNameWithoutExtension(p).Equals(process.ProcessName, StringComparison.OrdinalIgnoreCase))) return false;
        return process.MainModule?.FileName is { } path && selected.Contains(path);
    }

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
