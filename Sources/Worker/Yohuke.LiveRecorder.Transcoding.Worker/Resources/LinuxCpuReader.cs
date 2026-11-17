using System.Globalization;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Resources;

internal static class LinuxCpuReader
{
    internal static CpuCounters Read()
    {
        var allowedLine = File.ReadLines("/proc/self/status").FirstOrDefault(line => line.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Unable to read Linux CPU affinity");
        var allowed = ParseCpuList(allowedLine.Split(':', 2)[1].Trim());
        if (allowed.Count == 0)
        {
            throw new InvalidOperationException("The Linux CPU set is empty");
        }
        var busy = 0d;
        var total = 0d;
        foreach (var line in File.ReadLines("/proc/stat"))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || !parts[0].StartsWith("cpu", StringComparison.Ordinal)
                || !int.TryParse(parts[0].AsSpan(3), out var cpu) || !allowed.Contains(cpu))
            {
                continue;
            }
            var values = parts.Skip(1).Take(8).Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            var sum = values.Sum();
            total += sum;
            busy += sum - values[3] - (values.Length > 4 ? values[4] : 0);
        }
        var effective = (double)allowed.Count;
        double? quotaUsed = null;
        var identity = string.Join(',', allowed.Order());
        var cgroupLines = File.ReadAllLines("/proc/self/cgroup");
        var unified = cgroupLines.FirstOrDefault(line => line.StartsWith("0::", StringComparison.Ordinal));
        if (unified is not null)
        {
            var directory = ResolveCgroup(unified[3..]);
            var current = directory;
            while (current.StartsWith("/sys/fs/cgroup", StringComparison.Ordinal))
            {
                var maxPath = Path.Combine(current, "cpu.max");
                if (File.Exists(maxPath))
                {
                    var quota = File.ReadAllText(maxPath).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (quota.Length == 2 && quota[0] != "max")
                    {
                        effective = Math.Min(effective, double.Parse(quota[0], CultureInfo.InvariantCulture) / double.Parse(quota[1], CultureInfo.InvariantCulture));
                    }
                }
                if (current == "/sys/fs/cgroup")
                {
                    break;
                }
                current = Path.GetDirectoryName(current)!;
            }
            var usageLine = File.ReadLines(Path.Combine(directory, "cpu.stat"))
                .First(line => line.StartsWith("usage_usec ", StringComparison.Ordinal));
            quotaUsed = double.Parse(usageLine.AsSpan(11), CultureInfo.InvariantCulture) / 1000000;
            identity += ":" + directory;
        }
        else if (cgroupLines.Any(line => line.Split(':', 3) is [_, var controllers, _]
                     && controllers.Split(',').Any(value => value is "cpu" or "cpuacct")))
        {
            throw new InvalidOperationException("cgroup v1 CPU quota usage cannot be measured reliably. Automatic CPU fallback is disabled");
        }
        return new(busy, total, effective, quotaUsed, identity);
    }

    internal static HashSet<int> ParseCpuList(string value)
    {
        var result = new HashSet<int>();
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = item.Split('-');
            if (!int.TryParse(bounds[0], out var first) || first < 0 || bounds.Length > 2
                || (bounds.Length == 2 && (!int.TryParse(bounds[1], out var parsedEnd) || parsedEnd < first)))
            {
                throw new InvalidDataException("Invalid CPU set");
            }
            var last = bounds.Length == 1 ? first : int.Parse(bounds[1], CultureInfo.InvariantCulture);
            if (last > 65535)
            {
                throw new InvalidDataException("The CPU set exceeds the supported range");
            }
            for (var cpu = first; cpu <= last; cpu++)
            {
                result.Add(cpu);
            }
        }
        return result;
    }

    private static string ResolveCgroup(string relative)
    {
        var candidate = Path.GetFullPath(Path.Combine("/sys/fs/cgroup", relative.TrimStart('/')));
        if (candidate.StartsWith("/sys/fs/cgroup/", StringComparison.Ordinal) && File.Exists(Path.Combine(candidate, "cpu.stat")))
        {
            return candidate;
        }
        if (File.Exists("/sys/fs/cgroup/cpu.stat"))
        {
            return "/sys/fs/cgroup";
        }
        throw new InvalidOperationException("Unable to locate the current CPU cgroup");
    }
}
