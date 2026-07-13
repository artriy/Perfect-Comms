using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace VoiceChatPlugin;

internal static class NativeLibraryCache
{
    private const string BundlePrefix = "bundle-v1-";
    private const string BundleLeaseFileName = ".in-use";
    private static readonly object BundleLeaseGate = new();
    private static readonly Dictionary<string, FileStream> BundleLeases = new(StringComparer.OrdinalIgnoreCase);

    public static string Extract(
        Assembly assembly,
        string resourceName,
        string fileName,
        string archLabel,
        string baseDirectory,
        string? bundleVersion = null)
    {
        var target = ResolveExtractionPath(baseDirectory, archLabel, fileName, bundleVersion);
        var dir = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException($"Native cache target has no parent: {DiagnosticValue(target)}");
        var stage = "create-directory";
        string? temp = null;

        try
        {
            Directory.CreateDirectory(dir);

            stage = "open-resource";
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new FileNotFoundException($"Missing embedded resource {resourceName}");

            stage = "hash-resource";
            using var sha = SHA256.Create();
            var expected = sha.ComputeHash(stream);

            stage = "verify-existing";
            if (File.Exists(target) && FileHashMatches(target, expected))
                return target;

            stream.Position = 0;
            temp = $"{target}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            stage = "write-temp";
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.CopyTo(output);
                output.Flush(true);
            }

            stage = "publish";
            try
            {
                File.Move(temp, target, true);
                temp = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A concurrent process may have published the same immutable bundle first.
                if (!(File.Exists(target) && FileHashMatches(target, expected)))
                    throw;
                if (temp != null)
                    TryDelete(temp);
                temp = null;
            }

            return target;
        }
        catch (NativeCacheExtractionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (temp != null)
                TryDelete(temp);
            throw new NativeCacheExtractionException(stage, target, resourceName, ex);
        }
    }

    /// <summary>
    /// Returns an immutable bundle identity tied to both the managed build and the selected
    /// embedded resources. The module MVID changes for managed-only rebuilds, while hashing
    /// the resource bytes also protects non-standard packaging pipelines that preserve it.
    /// </summary>
    public static string BuildContentVersion(Assembly assembly, IEnumerable<string> resourceNames)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(resourceNames);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashValue(hash, "PerfectComms native bundle v1");
        AppendHashValue(hash, assembly.ManifestModule.ModuleVersionId.ToString("N"));

        foreach (var resourceName in resourceNames
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            AppendHashValue(hash, resourceName);
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                hash.AppendData(new byte[] { 0 });
                continue;
            }

            hash.AppendData(new byte[] { 1 });
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                hash.AppendData(buffer, 0, read);
        }

        var digest = hash.GetHashAndReset();
        return BundlePrefix + Convert.ToHexString(digest, 0, 16).ToLowerInvariant();
    }

    public static string BundleDirectory(string baseDirectory, string archLabel, string bundleVersion)
    {
        ValidatePathSegment(archLabel, nameof(archLabel));
        ValidateBundleVersion(bundleVersion);
        return Path.Combine(baseDirectory, "cache", "PerfectComms", "native", archLabel, bundleVersion);
    }

    public static string ResolveExtractionPath(
        string baseDirectory,
        string archLabel,
        string fileName,
        string? bundleVersion = null)
    {
        ValidatePathSegment(archLabel, nameof(archLabel));
        ValidatePathSegment(fileName, nameof(fileName));

        var dir = bundleVersion == null
            ? Path.Combine(baseDirectory, "cache", "PerfectComms", "native", archLabel)
            : BundleDirectory(baseDirectory, archLabel, bundleVersion);
        return Path.Combine(dir, fileName);
    }

    /// <summary>
    /// Holds a shareable, process-lifetime lease. Stale cleanup asks for exclusive access to
    /// the same file, so it cannot remove a bundle another current plugin process is using.
    /// </summary>
    public static void HoldBundleLease(string bundleDirectory)
    {
        var fullDirectory = Path.GetFullPath(bundleDirectory);
        var leasePath = Path.Combine(fullDirectory, BundleLeaseFileName);
        lock (BundleLeaseGate)
        {
            if (BundleLeases.ContainsKey(fullDirectory))
                return;

            try
            {
                Directory.CreateDirectory(fullDirectory);
                var lease = new FileStream(
                    leasePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite);
                BundleLeases.Add(fullDirectory, lease);
            }
            catch (Exception ex)
            {
                throw new NativeCacheExtractionException("acquire-bundle-lease", leasePath, "<bundle-lease>", ex);
            }
        }
    }

    /// <summary>
    /// Deletes inactive versioned bundles only. Every failure is deliberately ignored: a
    /// locked executable, loaded DSP library, active lease, or permissions issue must never
    /// prevent the current helper from launching.
    /// </summary>
    public static int PruneStaleBundles(string baseDirectory, string archLabel, string currentBundleVersion)
    {
        // Unix permits unlinking running executables and loaded libraries. Until cleanup has
        // a host-native liveness probe, retaining stale bundles is safer than disrupting a
        // concurrently starting helper. The original lock-denial failure is Windows-specific.
        if (!OperatingSystem.IsWindows())
            return 0;

        var currentDirectory = Path.GetFullPath(BundleDirectory(baseDirectory, archLabel, currentBundleVersion));
        var archDirectory = Path.GetDirectoryName(currentDirectory);
        if (string.IsNullOrEmpty(archDirectory) || !Directory.Exists(archDirectory))
            return 0;

        var deleted = 0;
        try
        {
            foreach (var candidate in Directory.EnumerateDirectories(archDirectory, BundlePrefix + "*", SearchOption.TopDirectoryOnly))
            {
                var fullCandidate = Path.GetFullPath(candidate);
                if (PathsEqual(fullCandidate, currentDirectory))
                    continue;

                try
                {
                    var leasePath = Path.Combine(fullCandidate, BundleLeaseFileName);
                    using var exclusiveLease = new FileStream(
                        leasePath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.Delete);
                    Directory.Delete(fullCandidate, true);
                    deleted++;
                }
                catch
                {
                    // Active/current helpers and permission failures make a bundle non-prunable.
                }
            }
        }
        catch
        {
            // Cache enumeration itself is best effort.
        }
        return deleted;
    }

    internal static string DiagnosticValue(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\r': builder.Append("\\r"); break;
                case '\n': builder.Append("\\n"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                        builder.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
        return builder.ToString();
    }

    private static void AppendHashValue(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static void ValidateBundleVersion(string bundleVersion)
    {
        ValidatePathSegment(bundleVersion, nameof(bundleVersion));
        if (!bundleVersion.StartsWith(BundlePrefix, StringComparison.Ordinal))
            throw new ArgumentException($"Bundle version must start with {BundlePrefix}", nameof(bundleVersion));
    }

    private static void ValidatePathSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value is "." or ".." ||
            value.IndexOf('/') >= 0 ||
            value.IndexOf('\\') >= 0 ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Path.IsPathRooted(value))
        {
            throw new ArgumentException("Native cache path components must be single safe path segments", parameterName);
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool FileHashMatches(string path, byte[] expected)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return sha.ComputeHash(fs).AsSpan().SequenceEqual(expected);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}

internal sealed class NativeCacheExtractionException : IOException
{
    public NativeCacheExtractionException(string stage, string targetPath, string resourceName, Exception innerException)
        : base(
            $"Native extraction failed stage={stage} path={NativeLibraryCache.DiagnosticValue(targetPath)} " +
            $"resource={NativeLibraryCache.DiagnosticValue(resourceName)} error={innerException.GetType().Name}:" +
            NativeLibraryCache.DiagnosticValue(innerException.Message),
            innerException)
    {
        Stage = stage;
        TargetPath = targetPath;
        ResourceName = resourceName;
    }

    public string Stage { get; }
    public string TargetPath { get; }
    public string ResourceName { get; }
}
