namespace Alife.PluginMarket;

public enum VersionConstraintType
{
    Minimum,
    Maximum
}

public class VersionConstraint(VersionConstraintType type, string version)
{
    public VersionConstraintType Type { get; set; } = type;
    public string Version { get; set; } = version;

    public override string ToString() => Type switch {
        VersionConstraintType.Minimum => $">={Version}",
        VersionConstraintType.Maximum => $"<={Version}",
        _ => Version
    };
}

public partial class VersionResolver
{
    public static int CompareVersions(string v1, string v2)
    {
        string[] parts1 = v1.Split('.');
        string[] parts2 = v2.Split('.');
        int max = Math.Max(parts1.Length, parts2.Length);

        for (int i = 0; i < max; i++)
        {
            int p1 = i < parts1.Length && int.TryParse(parts1[i], out int a) ? a : 0;
            int p2 = i < parts2.Length && int.TryParse(parts2[i], out int b) ? b : 0;
            if (p1 != p2)
                return p1.CompareTo(p2);
        }
        return 0;
    }
    public static int GetMajorVersion(string version)
    {
        string[] parts = version.Split('.');
        return parts.Length > 0 && int.TryParse(parts[0], out int major) ? major : 0;
    }
}

public partial class VersionResolver
{
    readonly Dictionary<string, (string Min, string Max)> dependencies = new();

    public IEnumerable<(string Name, string Min, string Max)> GetAllRanges()
    {
        foreach (var kvp in dependencies)
            yield return (kvp.Key, kvp.Value.Min, kvp.Value.Max);
    }
    public void AddRange(IEnumerable<KeyValuePair<string, string>> dependencies)
    {
        foreach (var dep in dependencies)
            Add(dep.Key, dep.Value);
    }
    public bool IsSatisfied(string dependency, string version)
    {
        if (!dependencies.TryGetValue(dependency, out var range))
            return true;

        return CompareVersions(version, range.Min) >= 0 && CompareVersions(version, range.Max) <= 0;
    }
    public string Resolve(string dependency, IEnumerable<string> availableVersions)
    {
        if (!dependencies.TryGetValue(dependency, out var range))
            throw new KeyNotFoundException($"未找到依赖 {dependency}");

        string? best = null;
        foreach (string version in availableVersions)
        {
            if (CompareVersions(version, range.Min) < 0)
                continue;
            if (CompareVersions(version, range.Max) > 0)
                continue;
            if (best == null || CompareVersions(version, best) > 0)
                best = version;
        }

        if (best == null)
            throw new InvalidOperationException($"依赖 {dependency} 在范围内 ({range.Min}~{range.Max}) 无可用版本");

        return best;
    }

    void Add(string dependency, string versionSpec)
    {
        if (string.IsNullOrWhiteSpace(versionSpec))
        {
            Add(dependency, new VersionConstraint(VersionConstraintType.Minimum, "0.0.0"));
            return;
        }

        foreach (string part in versionSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith(">="))
                Add(dependency, new VersionConstraint(VersionConstraintType.Minimum, part[2..]));
            else if (part.StartsWith("<="))
                Add(dependency, new VersionConstraint(VersionConstraintType.Maximum, part[2..]));
            else if (part.StartsWith("=="))
            {
                string version = part[2..];
                Add(dependency, new VersionConstraint(VersionConstraintType.Minimum, version));
                Add(dependency, new VersionConstraint(VersionConstraintType.Maximum, version));
            }
            else
                throw new ArgumentException($"Invalid version spec: {part}");
        }
    }
    void Add(string dependency, VersionConstraint constraint)
    {
        if (!dependencies.TryGetValue(dependency, out var current))
            current = ("0.0.0", "99999.0.0");

        string min = current.Min;
        string max = current.Max;

        if (constraint.Type == VersionConstraintType.Minimum && CompareVersions(constraint.Version, min) > 0)
            min = constraint.Version;
        else if (constraint.Type == VersionConstraintType.Maximum && CompareVersions(constraint.Version, max) < 0)
            max = constraint.Version;

        if (CompareVersions(min, max) > 0)
            throw new InvalidOperationException($"依赖 {dependency} 版本矛盾: {min} > {max}");

        dependencies[dependency] = (min, max);
    }
}
