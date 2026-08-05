namespace LogReader.Core;

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LogReader.Core.Models;

public static class ConfiguredLogCatalogRevision
{
    private const string RevisionPrefix = "sha256:";

    public static string Calculate(
        int sourceFormatVersion,
        ImmutableArray<ConfiguredLogGroup> groups,
        ImmutableArray<ConfiguredLogFile> files,
        ImmutableArray<ConfiguredDatePathPattern> datePathPatterns)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "weeztail-configured-log-catalog-v1");
        Append(hash, sourceFormatVersion.ToString(CultureInfo.InvariantCulture));

        Append(hash, "groups");
        Append(hash, groups.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var group in groups)
        {
            Append(hash, group.Id);
            Append(hash, group.Name);
            Append(hash, group.SortOrder.ToString(CultureInfo.InvariantCulture));
            Append(hash, group.ParentGroupId);
            Append(hash, ((int)group.Kind).ToString(CultureInfo.InvariantCulture));
            if (group.FileIds.IsDefault)
            {
                Append(hash, null);
                continue;
            }

            Append(hash, group.FileIds.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var fileId in group.FileIds)
                Append(hash, fileId);
        }

        Append(hash, "files");
        Append(hash, files.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var file in files)
        {
            Append(hash, file.Id);
            Append(hash, file.PhysicalPath);
        }

        Append(hash, "date-path-patterns");
        Append(hash, datePathPatterns.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var pattern in datePathPatterns)
        {
            Append(hash, pattern.Id);
            Append(hash, pattern.Name);
            Append(hash, pattern.FindPattern);
            Append(hash, pattern.ReplacePattern);
        }

        return RevisionPrefix + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string? value)
    {
        if (value == null)
        {
            hash.AppendData([0]);
            return;
        }

        hash.AppendData([1]);
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
