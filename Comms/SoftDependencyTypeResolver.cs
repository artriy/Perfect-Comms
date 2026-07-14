using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Exact-name resolver for optional mod types. Harmony's TypeByName falls back to enumerating every
/// type in every loaded assembly, which made the first lobby snapshot and privacy projection pause
/// the Unity main thread for hundreds of milliseconds. Every caller here already knows the complete
/// namespace-qualified name, so Assembly.GetType is both sufficient and substantially cheaper.
/// </summary>
internal static class SoftDependencyTypeResolver
{
    private readonly record struct CacheEntry(Type? Type, int AssemblyGeneration);

    private static readonly object Sync = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);
    private static Assembly[] _assemblies = Array.Empty<Assembly>();
    private static int _assembliesGeneration = -1;
    private static int _assemblyGeneration;

    static SoftDependencyTypeResolver()
    {
        // A cached miss must be retried if a soft dependency loads later. The event only bumps a
        // counter; the next main-thread lookup refreshes the assembly snapshot and cache entry.
        AppDomain.CurrentDomain.AssemblyLoad += (_, _) => Interlocked.Increment(ref _assemblyGeneration);
    }

    internal static int AssemblyGeneration => Volatile.Read(ref _assemblyGeneration);

    internal static Type? ResolveExact(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName) || fullName.IndexOf('.') < 0)
            return null;

        lock (Sync)
        {
            int generation = AssemblyGeneration;
            if (Cache.TryGetValue(fullName, out var cached)
                && cached.AssemblyGeneration == generation)
            {
                return cached.Type;
            }

            if (_assembliesGeneration != generation)
            {
                _assemblies = AppDomain.CurrentDomain.GetAssemblies();
                _assembliesGeneration = generation;
            }

            Type? resolved = null;
            for (int i = 0; i < _assemblies.Length; i++)
            {
                try
                {
                    resolved = _assemblies[i].GetType(fullName, throwOnError: false, ignoreCase: false);
                }
                catch
                {
                    // One unusual/dynamic assembly must not prevent other optional assemblies from
                    // satisfying an exact lookup.
                }

                if (resolved != null)
                    break;
            }

            Cache[fullName] = new CacheEntry(resolved, generation);
            return resolved;
        }
    }
}
