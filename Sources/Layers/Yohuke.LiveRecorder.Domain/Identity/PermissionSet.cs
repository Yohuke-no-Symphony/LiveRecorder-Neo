using System.Collections;
using System.Collections.Immutable;

namespace Yohuke.LiveRecorder.Domain;

/// <summary>不共享可变状态、按稳定标识比较的权限集合。</summary>
public sealed class PermissionSet : IReadOnlyCollection<string>, IEquatable<PermissionSet>
{
    private readonly ImmutableSortedSet<string> values;

    public static PermissionSet Empty { get; } = new([]);

    public static PermissionSet MemberDefaults { get; } = new([
        PermissionIds.VIEW_RECORDINGS,
        PermissionIds.DOWNLOAD_RECORDINGS,
        PermissionIds.CREATE_CLIPS,
        PermissionIds.UPLOAD_LIVE_MARKERS
    ]);

    public int Count => values.Count;

    /// <summary>复制并规范化权限标识，之后不再保留调用方的可变集合。</summary>
    public PermissionSet(IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        values = permissions.ToImmutableSortedSet(StringComparer.Ordinal);
        if (values.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("权限标识不能为空。", nameof(permissions));
        }
    }

    /// <summary>判断集合是否包含指定的稳定权限标识。</summary>
    public bool Contains(string permission) => values.Contains(permission);

    /// <inheritdoc />
    public bool Equals(PermissionSet? other) => other is not null && values.SetEquals(other.values);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PermissionSet other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in values)
        {
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public IEnumerator<string> GetEnumerator() => values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
