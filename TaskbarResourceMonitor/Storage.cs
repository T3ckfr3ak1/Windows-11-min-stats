namespace TaskbarResourceMonitor;

internal static class Storage
{
    public static IEnumerable<string> AvailableDriveRoots()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            string root;
            try { root = d.Name; } catch { continue; }
            if (string.IsNullOrWhiteSpace(root)) continue;
            yield return root;
        }
    }

    public static (double usedPercent, long totalBytes, long freeBytes)? TryGetUsage(string root)
    {
        try
        {
            var d = new DriveInfo(root);
            if (!d.IsReady) return null;
            var total = d.TotalSize;
            var free = d.AvailableFreeSpace;
            if (total <= 0) return null;
            var usedPct = (1.0 - (free / (double)total)) * 100.0;
            return (usedPct, total, free);
        }
        catch
        {
            return null;
        }
    }
}

