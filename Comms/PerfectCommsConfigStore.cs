using BepInEx;
using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace VoiceChatPlugin.VoiceChat;

internal readonly record struct PerfectCommsConfigKey(string Section, string Key);

internal enum PerfectCommsConfigMigrationResult
{
    ExistingGlobal,
    MigratedLocal,
    NewGlobal,
    InstanceFallback,
}

internal static class PerfectCommsConfigPath
{
    internal const string DirectoryName = "PerfectComms";
    internal const string FileName = "com.edgetel.perfectcomms.cfg";

    internal static string Resolve(string persistentDataPath)
    {
        if (string.IsNullOrWhiteSpace(persistentDataPath))
            throw new InvalidOperationException("Unity did not provide a persistent data path.");

        return Path.GetFullPath(Path.Combine(persistentDataPath, DirectoryName, FileName));
    }
}

/// <summary>
/// Owns Perfect Comms' user-global BepInEx configuration. BepInEx ConfigFile serializes access only
/// inside one process and rewrites its target directly, so sharing one ConfigFile path between two
/// live Among Us clients would otherwise permit stale whole-file overwrites. This store disables
/// direct ConfigFile saves, merges only locally changed definitions into the latest disk snapshot
/// under a cross-process lock, and promotes a flushed same-directory temporary file atomically.
/// </summary>
internal sealed class PerfectCommsConfigStore : IDisposable
{
    private const int LockTimeoutMilliseconds = 5000;
    private const int LockRetryMilliseconds = 20;

    private static PerfectCommsConfigStore? _current;

    private readonly object _gate = new();
    private readonly ConfigFile _config;
    private readonly BepInPlugin _ownerMetadata;
    private readonly string _path;
    private readonly string _lockPath;
    private readonly Action<string> _logInfo;
    private readonly Action<string> _logWarning;
    private readonly HashSet<PerfectCommsConfigKey> _dirty = new();
    private readonly HashSet<PerfectCommsConfigKey> _removed = new();

    private bool _initializing = true;
    private int _batchDepth;
    private bool _disposed;

    private PerfectCommsConfigStore(
        ConfigFile config,
        BepInPlugin ownerMetadata,
        string path,
        PerfectCommsConfigMigrationResult migrationResult,
        Action<string> logInfo,
        Action<string> logWarning)
    {
        _config = config;
        _ownerMetadata = ownerMetadata;
        _path = path;
        _lockPath = path + ".lock";
        _logInfo = logInfo;
        _logWarning = logWarning;
        MigrationResult = migrationResult;

        // ConfigFile.OnSettingChanged calls Save() before notifying subscribers. It must remain off
        // for the entire lifetime of this shared file so every write goes through the merge below.
        _config.SaveOnConfigSet = false;
        _config.SettingChanged += OnSettingChanged;
    }

    internal ConfigFile Config => _config;
    internal string Path => _path;
    internal PerfectCommsConfigMigrationResult MigrationResult { get; }
    internal bool IsGlobal => MigrationResult != PerfectCommsConfigMigrationResult.InstanceFallback;

    internal static PerfectCommsConfigStore Open(
        ConfigFile instanceConfig,
        BepInPlugin ownerMetadata,
        string persistentDataPath,
        Action<string> logInfo,
        Action<string> logWarning)
    {
        if (instanceConfig == null) throw new ArgumentNullException(nameof(instanceConfig));
        if (ownerMetadata == null) throw new ArgumentNullException(nameof(ownerMetadata));
        if (logInfo == null) throw new ArgumentNullException(nameof(logInfo));
        if (logWarning == null) throw new ArgumentNullException(nameof(logWarning));

        ConfigFile selectedConfig;
        string selectedPath;
        PerfectCommsConfigMigrationResult migrationResult;

        try
        {
            selectedPath = PerfectCommsConfigPath.Resolve(persistentDataPath);
            migrationResult = PrepareGlobalConfig(instanceConfig.ConfigFilePath, selectedPath);
            selectedConfig = new ConfigFile(selectedPath, saveOnInit: false, ownerMetadata);
        }
        catch (Exception ex)
        {
            selectedConfig = instanceConfig;
            selectedPath = instanceConfig.ConfigFilePath;
            migrationResult = PerfectCommsConfigMigrationResult.InstanceFallback;
            logWarning(
                $"Global Perfect Comms config is unavailable; using the instance config at " +
                $"'{selectedPath}'. {ex.GetType().Name}: {ex.Message}");
        }

        var store = new PerfectCommsConfigStore(
            selectedConfig,
            ownerMetadata,
            selectedPath,
            migrationResult,
            logInfo,
            logWarning);
        Interlocked.Exchange(ref _current, store);

        string source = migrationResult switch
        {
            PerfectCommsConfigMigrationResult.MigratedLocal => "migrated from this Among Us instance",
            PerfectCommsConfigMigrationResult.ExistingGlobal => "existing global config",
            PerfectCommsConfigMigrationResult.NewGlobal => "new global config",
            _ => "instance fallback",
        };
        logInfo($"Perfect Comms config: {source}; path='{selectedPath}'.");
        return store;
    }

    internal void CompleteInitialization()
    {
        lock (_gate)
        {
            _initializing = false;
            try
            {
                FlushCore();
            }
            catch (Exception ex)
            {
                // Voice remains usable with the already-loaded in-memory settings. Dirty definitions
                // stay queued and the next setting change or process shutdown retries the same commit.
                _logWarning(
                    $"Could not persist the initialized Perfect Comms config at '{_path}'. " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_initializing)
            {
                try { FlushCore(); }
                catch { /* shutdown/test cleanup is best-effort; explicit saves already logged */ }
            }
            _disposed = true;
            _config.SettingChanged -= OnSettingChanged;
        }
        Interlocked.CompareExchange(ref _current, null, this);
    }


    internal static IDisposable BeginBatch(ConfigFile config)
    {
        var store = Volatile.Read(ref _current);
        if (store != null && ReferenceEquals(store._config, config))
            return store.EnterBatch();
        return new DirectConfigBatch(config);
    }

    internal static void Save(ConfigFile config)
    {
        var store = Volatile.Read(ref _current);
        if (store == null || !ReferenceEquals(store._config, config))
        {
            config.Save();
            return;
        }

        lock (store._gate)
        {
            if (store._initializing)
                return;
            store.FlushCore();
        }
    }

    internal static bool Remove(ConfigFile config, ConfigDefinition definition)
    {
        var store = Volatile.Read(ref _current);
        if (store == null || !ReferenceEquals(store._config, config))
            return config.Remove(definition);

        lock (store._gate)
        {
            bool removed = config.Remove(definition);
            if (removed)
            {
                var key = ToKey(definition);
                store._dirty.Remove(key);
                store._removed.Add(key);
            }
            return removed;
        }
    }

    internal static void TryFlushPending()
    {
        var store = Volatile.Read(ref _current);
        if (store == null) return;

        lock (store._gate)
        {
            if (store._initializing) return;
            try
            {
                store.FlushCore();
            }
            catch (Exception ex)
            {
                store._logWarning(
                    $"Could not flush pending Perfect Comms config changes at '{store._path}'. " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private IDisposable EnterBatch()
    {
        lock (_gate)
        {
            checked { _batchDepth++; }
        }
        return new StoreConfigBatch(this);
    }

    private void ExitBatch()
    {
        lock (_gate)
        {
            if (_batchDepth <= 0)
                throw new InvalidOperationException("Perfect Comms config batch underflow.");
            _batchDepth--;
        }
    }

    private void OnSettingChanged(object? sender, SettingChangedEventArgs args)
    {
        if (args?.ChangedSetting == null) return;

        lock (_gate)
        {
            var key = ToKey(args.ChangedSetting.Definition);
            _removed.Remove(key);
            _dirty.Add(key);
            if (_initializing || _batchDepth > 0)
                return;

            try
            {
                FlushCore();
            }
            catch (Exception ex)
            {
                // ConfigFile isolates subscriber exceptions, but logging here preserves the target path
                // and makes it explicit that the in-memory value remains dirty for a later retry.
                _logWarning(
                    $"Could not persist Perfect Comms setting [{key.Section}] {key.Key}. " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void FlushCore()
    {
        ConfigEntryBase[] entries = _config.Values.ToArray();
        var bound = new Dictionary<PerfectCommsConfigKey, ConfigEntryBase>(entries.Length);
        foreach (ConfigEntryBase entry in entries)
            bound[ToKey(entry.Definition)] = entry;

        using FileStream configLock = AcquireFileLock(_lockPath, LockTimeoutMilliseconds);
        var values = File.Exists(_path)
            ? ParseValues(File.ReadAllText(_path, Encoding.UTF8))
            : new Dictionary<PerfectCommsConfigKey, string>();

        bool changed = !File.Exists(_path);
        foreach (PerfectCommsConfigKey key in _removed)
            changed |= values.Remove(key);

        foreach (var pair in bound)
        {
            bool mustPublish = _dirty.Contains(pair.Key) || !values.ContainsKey(pair.Key);
            if (!mustPublish) continue;

            string serialized = pair.Value.GetSerializedValue();
            if (!values.TryGetValue(pair.Key, out string? current) ||
                !string.Equals(current, serialized, StringComparison.Ordinal))
            {
                values[pair.Key] = serialized;
                changed = true;
            }
        }

        if (changed)
            WriteAtomically(_path, values, bound, _ownerMetadata);

        _dirty.Clear();
        _removed.Clear();
    }

    internal static Dictionary<PerfectCommsConfigKey, string> ParseValues(string text)
    {
        var values = new Dictionary<PerfectCommsConfigKey, string>();
        using var reader = new StringReader(text ?? string.Empty);
        string section = string.Empty;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                continue;
            if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
            {
                section = trimmed.Substring(1, trimmed.Length - 2);
                continue;
            }
            if (section.Length == 0)
                continue;

            int separator = trimmed.IndexOf('=');
            if (separator <= 0)
                continue;
            string key = trimmed.Substring(0, separator).Trim();
            if (key.Length == 0)
                continue;
            string value = trimmed.Substring(separator + 1).Trim();
            values[new PerfectCommsConfigKey(section, key)] = value;
        }
        return values;
    }

    private static void WriteAtomically(
        string path,
        IReadOnlyDictionary<PerfectCommsConfigKey, string> values,
        IReadOnlyDictionary<PerfectCommsConfigKey, ConfigEntryBase> bound,
        BepInPlugin ownerMetadata)
    {
        string directory = System.IO.Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The Perfect Comms config path has no directory.");
        Directory.CreateDirectory(directory);
        string temporary = System.IO.Path.Combine(
            directory,
            $".{System.IO.Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.SequentialScan))
            {
                using (var writer = new StreamWriter(
                           stream,
                           new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                           4096,
                           leaveOpen: true))
                {
                    writer.WriteLine($"## Settings file was created by plugin {ownerMetadata.Name} v{ownerMetadata.Version}");
                    writer.WriteLine($"## Plugin GUID: {ownerMetadata.GUID}");
                    writer.WriteLine();

                    foreach (var sectionGroup in values
                                 .OrderBy(pair => pair.Key.Section, StringComparer.Ordinal)
                                 .ThenBy(pair => pair.Key.Key, StringComparer.Ordinal)
                                 .GroupBy(pair => pair.Key.Section, StringComparer.Ordinal))
                    {
                        writer.WriteLine($"[{sectionGroup.Key}]");
                        foreach (var pair in sectionGroup)
                        {
                            writer.WriteLine();
                            if (bound.TryGetValue(pair.Key, out ConfigEntryBase? entry))
                                entry.WriteDescription(writer);
                            writer.WriteLine($"{pair.Key.Key} = {pair.Value}");
                        }
                        writer.WriteLine();
                    }
                    writer.Flush();
                }
                stream.Flush(flushToDisk: true);
            }

            PromoteTemporaryFile(temporary, path);
            temporary = string.Empty;
        }
        finally
        {
            if (temporary.Length > 0)
                TryDelete(temporary);
        }
    }

    private static PerfectCommsConfigMigrationResult PrepareGlobalConfig(
        string instancePath,
        string globalPath)
    {
        string directory = System.IO.Path.GetDirectoryName(globalPath)
            ?? throw new InvalidOperationException("The global Perfect Comms config path has no directory.");
        Directory.CreateDirectory(directory);
        string lockPath = globalPath + ".lock";

        using FileStream configLock = AcquireFileLock(lockPath, LockTimeoutMilliseconds);
        if (File.Exists(globalPath))
            return PerfectCommsConfigMigrationResult.ExistingGlobal;
        if (string.IsNullOrWhiteSpace(instancePath) || !File.Exists(instancePath))
            return PerfectCommsConfigMigrationResult.NewGlobal;

        string temporary = System.IO.Path.Combine(
            directory,
            $".{System.IO.Path.GetFileName(globalPath)}.migration.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var source = new FileStream(instancePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destination = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       81920,
                       FileOptions.SequentialScan))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }
            PromoteTemporaryFile(temporary, globalPath);
            temporary = string.Empty;
            return PerfectCommsConfigMigrationResult.MigratedLocal;
        }
        finally
        {
            if (temporary.Length > 0)
                TryDelete(temporary);
        }
    }

    private static FileStream AcquireFileLock(string lockPath, int timeoutMilliseconds)
    {
        string? directory = System.IO.Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.None);
            }
            catch (IOException) when (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
            {
                Thread.Sleep(LockRetryMilliseconds);
            }
        }
    }

    private static void PromoteTemporaryFile(string temporary, string target)
    {
        if (!File.Exists(target))
        {
            try
            {
                File.Move(temporary, target);
                return;
            }
            catch (IOException) when (File.Exists(target))
            {
                // Another process can win only when it does not yet participate in the lock protocol.
                // Treat its completed destination as authoritative and replace it below.
            }
        }

        try
        {
            File.Replace(temporary, target, destinationBackupFileName: null);
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(temporary, target, overwrite: true);
        }
        catch (IOException)
        {
            // Some Android/Linux filesystems do not expose replace-file semantics. A same-directory
            // rename with overwrite remains the strongest operation available on those platforms.
            File.Move(temporary, target, overwrite: true);
        }
    }

    private static PerfectCommsConfigKey ToKey(ConfigDefinition definition)
        => new(definition.Section, definition.Key);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A stale uniquely named temporary file is harmless and must not hide the real error.
        }
    }

    private sealed class StoreConfigBatch : IDisposable
    {
        private PerfectCommsConfigStore? _store;

        internal StoreConfigBatch(PerfectCommsConfigStore store)
        {
            _store = store;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _store, null)?.ExitBatch();
        }
    }

    private sealed class DirectConfigBatch : IDisposable
    {
        private ConfigFile? _config;
        private readonly bool _saveOnConfigSet;

        internal DirectConfigBatch(ConfigFile config)
        {
            _config = config;
            _saveOnConfigSet = config.SaveOnConfigSet;
            config.SaveOnConfigSet = false;
        }

        public void Dispose()
        {
            var config = Interlocked.Exchange(ref _config, null);
            if (config != null)
                config.SaveOnConfigSet = _saveOnConfigSet;
        }
    }
}
