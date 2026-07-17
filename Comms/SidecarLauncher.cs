using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace VoiceChatPlugin.VoiceChat;

internal readonly record struct SidecarDeviceEnumerationResult(
    bool IsAuthoritative,
    IReadOnlyList<VoiceDeviceInfo> Input,
    IReadOnlyList<VoiceDeviceInfo> Output)
{
    internal static SidecarDeviceEnumerationResult Failure =>
        new(false, Array.Empty<VoiceDeviceInfo>(), Array.Empty<VoiceDeviceInfo>());

    internal static SidecarDeviceEnumerationResult Success(
        IReadOnlyList<VoiceDeviceInfo> input,
        IReadOnlyList<VoiceDeviceInfo> output)
        => new(true, input, output);
}

internal readonly record struct SidecarTemporaryPaths(
    string HandshakePath,
    string? PrivateDirectory,
    string? PrivateRoot);

internal readonly record struct WineHelperCandidates(
    string PrimaryPath,
    string? FallbackPath,
    string? StagedHelperPath,
    string? StagedDspPath);

internal readonly record struct WineLaunchControlPaths(
    string Nonce,
    string OwnershipPath,
    string StartedPath,
    string FailurePath,
    string ExitPath,
    string CancellationPath);

internal static class SidecarLauncher
{
    private const string WineTemporaryDirectoryPrefix = "perfect-comms-";
    internal const int WineHelperExitWaitMs = 4_500;
    internal const string WinePrivateDirectoryScript = """
        set -eu
        umask 077
        private_dir=$1
        pending_token=$2
        receipt=$3
        expected_receipt=$4
        launch_owned=$5
        /bin/mkdir -m 700 "$private_dir"
        /bin/chmod 700 "$private_dir"
        : > "$pending_token"
        /bin/chmod 600 "$pending_token"
        receipt_tmp="${receipt}.tmp.$$"
        printf '%s' "$expected_receipt" > "$receipt_tmp"
        /bin/chmod 600 "$receipt_tmp"
        /bin/mv -f "$receipt_tmp" "$receipt"
        (
          bootstrap_seconds=0
          while [ "$bootstrap_seconds" -lt 120 ]; do
            if [ ! -d "$private_dir" ]; then exit 0; fi
            if [ -e "$launch_owned" ]; then exit 0; fi
            /bin/sleep 1
            bootstrap_seconds=$((bootstrap_seconds + 1))
          done
          /bin/rm -f "$pending_token" "$receipt"
          /bin/rmdir "$private_dir" 2>/dev/null || true
        ) >/dev/null 2>&1 &
        """;
    internal const string WineHelperLaunchScript = """
        set -u
        umask 077
        private_dir=$1
        token_file=$2
        primary_helper=$3
        fallback_helper=$4
        staged_helper=$5
        staged_dsp=$6
        quarantine_target=$7
        handshake_file=$8
        launch_owned=$9
        launch_started=${10}
        launch_failed=${11}
        helper_exited=${12}
        launch_cancelled=${13}
        launch_nonce=${14}
        expected_protocol=${15}
        shift 15

        write_receipt() {
          receipt_file=$1
          receipt_value=$2
          receipt_tmp="${receipt_file}.tmp.$$"
          printf '%s' "$receipt_value" > "$receipt_tmp" || return 1
          /bin/chmod 600 "$receipt_tmp" || { /bin/rm -f "$receipt_tmp"; return 1; }
          /bin/mv -f "$receipt_tmp" "$receipt_file" || {
            /bin/rm -f "$receipt_tmp"
            return 1
          }
        }

        cleanup_later() {
          (
            /bin/sleep 10
            if [ -n "$token_file" ]; then /bin/rm -f "$token_file"; fi
            /bin/rm -f "$private_dir/.token.pending" "$private_dir/.bootstrap-ready"
            /bin/rm -f "$handshake_file" "$launch_owned" "$launch_started" \
              "$launch_failed" "$helper_exited" "$launch_cancelled"
            if [ -n "$staged_helper" ]; then /bin/rm -f "$staged_helper"; fi
            if [ -n "$staged_dsp" ]; then /bin/rm -f "$staged_dsp"; fi
            /bin/rmdir "$private_dir" 2>/dev/null || true
          ) >/dev/null 2>&1 &
        }

        fail_launch() {
          failure_reason=$1
          failure_code=$2
          write_receipt "$launch_failed" \
            "perfect-comms-launch-failed-v1:${launch_nonce}:${failure_reason}:${failure_code}" || true
          cleanup_later
          exit "$failure_code"
        }

        is_cancelled() {
          [ -f "$launch_cancelled" ] || return 1
          cancellation_value=$(/bin/cat "$launch_cancelled" 2>/dev/null) || return 1
          [ "$cancellation_value" = "perfect-comms-launch-cancel-v1:${launch_nonce}" ]
        }

        terminate_bounded_child() {
          /bin/kill -TERM "$bounded_pid" 2>/dev/null || true
          /bin/sleep 1
          /bin/kill -KILL "$bounded_pid" 2>/dev/null || true
          wait "$bounded_pid" 2>/dev/null || true
        }

        run_bounded() {
          bounded_output=$1
          bounded_limit=$2
          shift 2
          : > "$bounded_output" || { bounded_status=125; return 1; }
          "$@" >"$bounded_output" 2>/dev/null &
          bounded_pid=$!
          bounded_elapsed=0
          while /bin/kill -0 "$bounded_pid" 2>/dev/null; do
            if is_cancelled; then
              terminate_bounded_child
              bounded_status=125
              return 2
            fi
            if [ "$bounded_elapsed" -ge "$bounded_limit" ]; then
              terminate_bounded_child
              bounded_status=124
              return 1
            fi
            /bin/sleep 1
            bounded_elapsed=$((bounded_elapsed + 1))
          done
          wait "$bounded_pid"
          bounded_status=$?
          return 0
        }

        probe_status=127
        probe_helper() {
          probe_output_file="$private_dir/.probe-output.$$"
          run_bounded "$probe_output_file" 2 "$1" --protocol-version
          bounded_result=$?
          probe_status=$bounded_status
          probe_output=$(/bin/cat "$probe_output_file" 2>/dev/null) || probe_output=
          /bin/rm -f "$probe_output_file"
          if [ "$bounded_result" -eq 2 ]; then fail_launch cancelled 125; fi
          if [ "$probe_status" -ne 0 ]; then return 1; fi
          if [ "$probe_output" != "$expected_protocol" ]; then probe_status=65; return 1; fi
          return 0
        }

        /bin/chmod 700 "$private_dir" || exit 125
        write_receipt "$launch_owned" \
          "perfect-comms-launch-owned-v1:${launch_nonce}" || exit 125
        if is_cancelled; then fail_launch cancelled 125; fi
        if [ -n "$token_file" ]; then
          /bin/chmod 600 "$token_file" || fail_launch token-permission 126
        fi
        if [ -n "$quarantine_target" ] && [ -x /usr/bin/xattr ]; then
          xattr_output_file="$private_dir/.xattr-output.$$"
          run_bounded "$xattr_output_file" 2 \
            /usr/bin/xattr -dr com.apple.quarantine "$quarantine_target"
          bounded_result=$?
          /bin/rm -f "$xattr_output_file"
          if [ "$bounded_result" -eq 2 ]; then fail_launch cancelled 125; fi
        fi
        /bin/chmod u+x "$primary_helper" >/dev/null 2>&1 || true
        if [ -n "$fallback_helper" ] && [ "$fallback_helper" != "$primary_helper" ]; then
          /bin/chmod u+x "$fallback_helper" >/dev/null 2>&1 || true
        fi

        selected_helper=
        primary_status=127
        fallback_status=127
        if [ -z "$fallback_helper" ] || [ "$fallback_helper" = "$primary_helper" ]; then
          # The signed universal macOS app has no alternate location and is already validated by
          # release signing/smoke gates. Linux always supplies a staged+original pair and takes
          # the portable bounded execution probe path below.
          selected_helper=$primary_helper
          primary_status=0
        else
          probe_helper "$primary_helper"
          primary_status=$probe_status
          if [ "$primary_status" -eq 0 ]; then
            selected_helper=$primary_helper
          else
            if is_cancelled; then fail_launch cancelled 125; fi
            probe_helper "$fallback_helper"
            fallback_status=$probe_status
            if [ "$fallback_status" -eq 0 ]; then selected_helper=$fallback_helper; fi
          fi
        fi
        if [ -z "$selected_helper" ]; then
          printf '%s\n' \
            "pc-capture: no executable helper location (primary=${primary_status}, fallback=${fallback_status})" >&2
          fail_launch no-executable-candidate 126
        fi
        if is_cancelled; then fail_launch cancelled 125; fi
        if [ "$selected_helper" = "$primary_helper" ]; then
          printf '%s\n' 'pc-capture: helper candidate selected=primary' >&2
        else
          printf '%s\n' 'pc-capture: helper candidate selected=fallback' >&2
        fi

        "$selected_helper" "$@" &
        helper_pid=$!
        if ! write_receipt "$launch_started" \
          "perfect-comms-launch-started-v1:${launch_nonce}:${helper_pid}"; then
          /bin/kill -TERM "$helper_pid" 2>/dev/null || true
          wait "$helper_pid" 2>/dev/null || true
          fail_launch start-receipt 125
        fi
        if is_cancelled; then /bin/kill -TERM "$helper_pid" 2>/dev/null || true; fi
        wait "$helper_pid"
        helper_status=$?
        write_receipt "$helper_exited" \
          "perfect-comms-helper-exited-v1:${launch_nonce}:${helper_pid}:${helper_status}" || true
        cleanup_later
        exit "$helper_status"
        """;
    private static readonly object BundleVersionGate = new();
    private static readonly Dictionary<string, string> BundleVersions = new(StringComparer.Ordinal);

    public static string TargetTriple()
        => TargetTripleFor(
            WineEnvironment.IsWine,
            WineEnvironment.HostOs,
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
            // The helper must match the game process, not the physical CPU. This keeps 32-bit
            // Among Us correct on x64 Windows and lets the x64 build run under Windows-on-ARM
            // emulation instead of incorrectly requesting a nonexistent ARM64 helper.
            RuntimeInformation.ProcessArchitecture);

    internal static string TargetTripleFor(
        bool wine,
        WineHostOs wineHostOs,
        bool windows,
        bool macOs,
        bool linux,
        Architecture processArchitecture)
    {
        if (wine)
        {
            // The macOS resource is a universal app even though its resource key uses x86_64.
            if (wineHostOs == WineHostOs.MacOS) return "x86_64-apple-darwin";
            if (wineHostOs == WineHostOs.Linux) return "x86_64-unknown-linux-gnu";
            throw new PlatformNotSupportedException(
                "pc-capture: running under Wine but host OS is undetectable (Z: not mapped to host root); cannot select a native helper");
        }
        if (windows)
            return processArchitecture switch
            {
                Architecture.X86 => "i686-pc-windows-msvc",
                Architecture.X64 => "x86_64-pc-windows-msvc",
                _ => throw new PlatformNotSupportedException(
                    $"pc-capture: unsupported Windows process architecture {processArchitecture}"),
            };
        if (macOs)
            return processArchitecture switch
            {
                Architecture.X64 => "x86_64-apple-darwin",
                Architecture.Arm64 => "aarch64-apple-darwin",
                _ => throw new PlatformNotSupportedException(
                    $"pc-capture: unsupported macOS process architecture {processArchitecture}"),
            };
        if (linux)
            return processArchitecture == Architecture.X64
                ? "x86_64-unknown-linux-gnu"
                : throw new PlatformNotSupportedException(
                    $"pc-capture: unsupported Linux process architecture {processArchitecture}");
        throw new PlatformNotSupportedException("pc-capture: unsupported operating system");
    }

    public static string HelperFileName(string triple)
        => triple.Contains("windows") ? "PerfectCommsAudio.exe" : "PerfectCommsAudio";

    public static bool IsHelperAvailable()
        => IsHelperAvailable(Assembly.GetExecutingAssembly());

    public static bool IsHelperAvailable(Assembly assembly)
    {
        try
        {
            using var stream = assembly.GetManifestResourceStream(ResourceName(TargetTriple()));
            return stream != null;
        }
        catch
        {
            return false;
        }
    }

    public static string ResourceName(string triple)
        => triple switch
        {
            "x86_64-pc-windows-msvc" => "Lib.pc-capture.pc-capture-win-x64.exe",
            "i686-pc-windows-msvc" => "Lib.pc-capture.pc-capture-win-x86.exe",
            "x86_64-unknown-linux-gnu" => "Lib.pc-capture.pc-capture-linux-x64",
            "x86_64-apple-darwin" => "Lib.pc-capture.pc-capture-mac.zip",
            "aarch64-apple-darwin" => "Lib.pc-capture.pc-capture-mac.zip",
            _ => throw new PlatformNotSupportedException($"No pc-capture helper for target {triple}"),
        };

    public static string EnsureHelperExtracted(Assembly assembly, string baseDirectory, bool force)
    {
        var triple = TargetTriple();
        var resourceName = ResourceName(triple);
        var isMac = resourceName.EndsWith(".zip", StringComparison.Ordinal);
        var cacheFileName = isMac ? "pc-capture.zip" : HelperFileName(triple);
        var stage = "bundle-version";
        var diagnosticPath = Path.Combine(baseDirectory, "cache", "PerfectComms", "native", triple);

        try
        {
            var bundleVersion = BundleVersionFor(assembly, triple, resourceName);
            if (force)
                bundleVersion += $"-refresh-{Environment.ProcessId}-{Guid.NewGuid():N}";

            var bundleDirectory = NativeLibraryCache.BundleDirectory(baseDirectory, triple, bundleVersion);
            diagnosticPath = bundleDirectory;
            VoiceDiagnostics.Log(
                "sidecar.extract",
                $"event=begin stage=acquire-bundle-lease triple={triple} bundle={bundleVersion} path={NativeLibraryCache.DiagnosticValue(bundleDirectory)} force={force.ToString().ToLowerInvariant()}");

            stage = "acquire-bundle-lease";
            NativeLibraryCache.HoldBundleLease(bundleDirectory);

            stage = "extract-helper";
            var extracted = NativeLibraryCache.Extract(
                assembly,
                resourceName,
                cacheFileName,
                triple,
                baseDirectory,
                bundleVersion);
            diagnosticPath = extracted;

            string helperPath;
            if (isMac)
            {
                stage = "extract-mac-app";
                helperPath = ExtractMacApp(extracted, triple, baseDirectory, bundleVersion);
                diagnosticPath = helperPath;
                if (!WineEnvironment.IsWine)
                {
                    stage = "make-executable";
                    MakeExecutable(helperPath);
                    stage = "strip-quarantine";
                    StripQuarantine(helperPath);
                }
            }
            else
            {
                helperPath = extracted;
                if (!WineEnvironment.IsWine && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    stage = "make-executable";
                    MakeExecutable(helperPath);
                }
            }

            stage = "extract-dsp";
            EnsureDspLibsExtracted(assembly, baseDirectory, triple, helperPath, bundleVersion);

            stage = "prune-stale-bundles";
            var pruned = NativeLibraryCache.PruneStaleBundles(baseDirectory, triple, bundleVersion);
            VoiceDiagnostics.Log(
                "sidecar.extract",
                $"event=complete triple={triple} bundle={bundleVersion} helper={NativeLibraryCache.DiagnosticValue(helperPath)} pruned={pruned}");
            return helperPath;
        }
        catch (Exception ex)
        {
            var detail = ex is NativeCacheExtractionException
                ? ex.Message
                : $"Sidecar extraction failed stage={stage} path={NativeLibraryCache.DiagnosticValue(diagnosticPath)} " +
                  $"error={ExceptionDiagnostic(ex)}";
            VoiceDiagnostics.Log("sidecar.extract", $"event=failed {detail}");
            throw new InvalidOperationException(detail, ex);
        }
    }

    private static string BundleVersionFor(Assembly assembly, string triple, string helperResourceName)
    {
        var key = $"{assembly.ManifestModule.ModuleVersionId:N}:{triple}";
        lock (BundleVersionGate)
        {
            if (BundleVersions.TryGetValue(key, out var existing))
                return existing;

            var resources = new List<string> { helperResourceName };
            foreach (var (resource, _) in DspLibsFor(triple))
                resources.Add(resource);
            var created = NativeLibraryCache.BuildContentVersion(assembly, resources);
            BundleVersions[key] = created;
            return created;
        }
    }

    public static (string Resource, string File)[] DspLibsFor(string triple)
        => triple switch
        {
            "x86_64-pc-windows-msvc" => new[] { ("Lib.dsp.webrtc-apm.x64.dll", "webrtc-apm.x64.dll") },
            "i686-pc-windows-msvc" => new[] { ("Lib.dsp.webrtc-apm.x86.dll", "webrtc-apm.x86.dll") },
            "x86_64-unknown-linux-gnu" => new[] { ("Lib.dsp.libwebrtc-apm.so", "libwebrtc-apm.so") },
            _ => Array.Empty<(string, string)>(),
        };

    public static void EnsureDspLibsExtracted(
        Assembly assembly,
        string baseDirectory,
        string triple,
        string helperPath,
        string bundleVersion)
    {
        var helperDir = Path.GetDirectoryName(helperPath);
        if (string.IsNullOrEmpty(helperDir)) return;
        if (triple.EndsWith("-apple-darwin", StringComparison.Ordinal))
        {
            // The universal macOS app is deep-signed after its APM dylib is inserted. Replacing
            // that sealed file with the separately staged pre-sign artifact invalidates the app's
            // code signature, especially on Apple Silicon. The archive integrity check above
            // already validates every in-app file, so preserve and require the signed copy.
            var signedDsp = Path.Combine(helperDir, "libwebrtc-apm.dylib");
            if (!File.Exists(signedDsp))
                throw new FileNotFoundException(
                    "The signed macOS helper app is missing its bundled WebRTC APM library",
                    signedDsp);
            VoiceDiagnostics.Log(
                "sidecar.dsp",
                $"event=preserve-signed-app-library path={NativeLibraryCache.DiagnosticValue(signedDsp)}");
            return;
        }
        foreach (var (resource, file) in DspLibsFor(triple))
        {
            try
            {
                using (var probe = assembly.GetManifestResourceStream(resource))
                    if (probe == null) continue;
                var extracted = NativeLibraryCache.Extract(
                    assembly,
                    resource,
                    file,
                    triple,
                    baseDirectory,
                    bundleVersion);
                var beside = Path.Combine(helperDir, file);
                if (!string.Equals(Path.GetFullPath(extracted), Path.GetFullPath(beside), StringComparison.OrdinalIgnoreCase))
                    PublishFileAtomically(extracted, beside);
            }
            catch (Exception ex)
            {
                var target = Path.Combine(helperDir, file);
                VoiceDiagnostics.Log(
                    "sidecar.dsp",
                    $"event=extract-failed stage=extract-or-place path={NativeLibraryCache.DiagnosticValue(target)} " +
                    $"resource={NativeLibraryCache.DiagnosticValue(resource)} error={ExceptionDiagnostic(ex)}");
                throw;
            }
        }
    }

    public static string ExtractMacApp(string zipPath, string triple, string baseDirectory, string bundleVersion)
    {
        var bundleDirectory = NativeLibraryCache.BundleDirectory(baseDirectory, triple, bundleVersion);
        var appDir = Path.Combine(bundleDirectory, "PerfectCommsAudio.app");
        var inner = Path.Combine(appDir, "Contents", "MacOS", "PerfectCommsAudio");
        Directory.CreateDirectory(bundleDirectory);
        var lockPath = Path.Combine(bundleDirectory, ".mac-extract.lock");
        using var extractionLock = AcquireExclusiveFileLock(lockPath, TimeSpan.FromSeconds(15));
        if (MacAppMatchesArchive(zipPath, appDir))
            return inner;

        var stagingRoot = Path.Combine(
            bundleDirectory,
            $".mac-stage-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var stagedApp = Path.Combine(stagingRoot, "PerfectCommsAudio.app");
        var backupApp = Path.Combine(
            bundleDirectory,
            $".mac-old-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var previousMoved = false;
        var published = false;
        try
        {
            Directory.CreateDirectory(stagingRoot);
            ZipFile.ExtractToDirectory(zipPath, stagingRoot, false);
            if (!MacAppMatchesArchive(zipPath, stagedApp))
                throw new InvalidDataException(
                    $"Mac helper archive is missing or failed integrity validation: {zipPath}");

            if (Directory.Exists(appDir))
            {
                Directory.Move(appDir, backupApp);
                previousMoved = true;
            }

            Directory.Move(stagedApp, appDir);
            published = true;
            TryDeleteDirectory(backupApp);
            return inner;
        }
        catch
        {
            if (!published && previousMoved && !Directory.Exists(appDir) && Directory.Exists(backupApp))
            {
                try { Directory.Move(backupApp, appDir); } catch { }
            }
            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
            if (published)
                TryDeleteDirectory(backupApp);
        }
    }

    private static bool MacAppMatchesArchive(string zipPath, string appDirectory)
    {
        if (!Directory.Exists(appDirectory))
            return false;

        const string appPrefix = "PerfectCommsAudio.app/";
        const string helperEntry = "PerfectCommsAudio.app/Contents/MacOS/PerfectCommsAudio";
        var appRoot = Path.GetFullPath(appDirectory);
        var appRootPrefix = Path.TrimEndingDirectorySeparator(appRoot) + Path.DirectorySeparatorChar;
        var foundHelper = false;
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.StartsWith(appPrefix, StringComparison.Ordinal) ||
                    entry.FullName.EndsWith("/", StringComparison.Ordinal))
                    continue;

                var relative = entry.FullName.Substring(appPrefix.Length)
                    .Replace('/', Path.DirectorySeparatorChar);
                var target = Path.GetFullPath(Path.Combine(appRoot, relative));
                if (!target.StartsWith(appRootPrefix, StringComparison.Ordinal) ||
                    !File.Exists(target) ||
                    new FileInfo(target).Length != entry.Length)
                    return false;

                using var expectedStream = entry.Open();
                using var actualStream = new FileStream(
                    target,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (!StreamHashesEqual(expectedStream, actualStream))
                    return false;
                if (string.Equals(entry.FullName, helperEntry, StringComparison.Ordinal))
                    foundHelper = true;
            }
            return foundHelper;
        }
        catch
        {
            return false;
        }
    }

    private static void PublishFileAtomically(string source, string target)
    {
        using (var sourceProbe = new FileStream(
                   source,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            if (File.Exists(target))
            {
                using var targetProbe = new FileStream(
                    target,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (sourceProbe.Length == targetProbe.Length && StreamHashesEqual(sourceProbe, targetProbe))
                    return;
            }
        }

        var temp = $"{target}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var input = new FileStream(
                       source,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            File.Move(temp, target, true);
            temp = string.Empty;
        }
        finally
        {
            if (!string.IsNullOrEmpty(temp))
            {
                try { File.Delete(temp); } catch { }
            }
        }
    }

    private static bool StreamHashesEqual(Stream left, Stream right)
    {
        using var leftSha = SHA256.Create();
        using var rightSha = SHA256.Create();
        return CryptographicOperations.FixedTimeEquals(
            leftSha.ComputeHash(left),
            rightSha.ComputeHash(right));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch { }
    }

    private static FileStream AcquireExclusiveFileLock(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    public static void MakeExecutable(string path)
    {
        if (WineEnvironment.IsWine)
        {
            WineEnvironment.HostExec("/bin/chmod", "u+x", WineEnvironment.ResolveHostPath(path));
            return;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;
        try
        {
            var psi = new ProcessStartInfo("chmod")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("u+x");
            psi.ArgumentList.Add(path);
            using var p = Process.Start(psi);
            if (p == null)
                throw new InvalidOperationException("chmod process did not start");
            if (!p.WaitForExit(2000))
            {
                KillQuietly(p);
                throw new TimeoutException("chmod timed out");
            }
            if (p.ExitCode != 0)
                throw new UnauthorizedAccessException($"chmod exited with code {p.ExitCode}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Could not mark the native audio helper executable", ex);
        }
    }

    public static void StripQuarantine(string innerPath)
    {
        var appDir = MacAppDirectory(innerPath);
        var target = appDir ?? innerPath;
        if (WineEnvironment.IsWine)
        {
            WineEnvironment.HostExec(
                "/usr/bin/xattr",
                "-dr",
                "com.apple.quarantine",
                WineEnvironment.ResolveHostPath(target));
            return;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;
        try
        {
            var psi = new ProcessStartInfo("xattr")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add("-r");
            psi.ArgumentList.Add("com.apple.quarantine");
            psi.ArgumentList.Add(target);
            using var p = Process.Start(psi);
            if (p != null && !p.WaitForExit(2000))
                KillQuietly(p);
        }
        catch { }
    }

    private static string? MacAppDirectory(string helperPath)
    {
        var appDirectory = Path.GetDirectoryName(
            Path.GetDirectoryName(
                Path.GetDirectoryName(helperPath)));
        return !string.IsNullOrEmpty(appDirectory) &&
               appDirectory.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? appDirectory
            : null;
    }

    public static string BuildArguments(string handshakePath, int ownerPid, bool wine)
    {
        var arguments = $"--handshake \"{handshakePath}\"";
        if (wine)
            return arguments;
        if (ownerPid <= 0)
            throw new ArgumentOutOfRangeException(nameof(ownerPid), "A native helper owner PID must be positive");
        return $"{arguments} --owner-pid {ownerPid}";
    }

    internal static ProcessStartInfo BuildWineHelperStartInfo(
        string hostPrivateDirectory,
        WineHelperCandidates hostCandidates,
        string hostHandshake,
        string? hostToken,
        bool enumerate,
        WineLaunchControlPaths hostControl,
        int expectedProtocol,
        string? hostQuarantineTarget = null)
    {
        ThrowIfNullOrWhiteSpace(hostPrivateDirectory, nameof(hostPrivateDirectory));
        ThrowIfNullOrWhiteSpace(hostCandidates.PrimaryPath, nameof(hostCandidates));
        ThrowIfNullOrWhiteSpace(hostHandshake, nameof(hostHandshake));
        ThrowIfNullOrWhiteSpace(hostControl.Nonce, nameof(hostControl));
        ThrowIfNullOrWhiteSpace(hostControl.OwnershipPath, nameof(hostControl));
        ThrowIfNullOrWhiteSpace(hostControl.StartedPath, nameof(hostControl));
        ThrowIfNullOrWhiteSpace(hostControl.FailurePath, nameof(hostControl));
        ThrowIfNullOrWhiteSpace(hostControl.ExitPath, nameof(hostControl));
        ThrowIfNullOrWhiteSpace(hostControl.CancellationPath, nameof(hostControl));
        if (expectedProtocol <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedProtocol));

        var arguments = new List<string>
        {
            hostPrivateDirectory,
            hostToken ?? string.Empty,
            hostCandidates.PrimaryPath,
            hostCandidates.FallbackPath ?? string.Empty,
            hostCandidates.StagedHelperPath ?? string.Empty,
            hostCandidates.StagedDspPath ?? string.Empty,
            hostQuarantineTarget ?? string.Empty,
            hostHandshake,
            hostControl.OwnershipPath,
            hostControl.StartedPath,
            hostControl.FailurePath,
            hostControl.ExitPath,
            hostControl.CancellationPath,
            hostControl.Nonce,
            expectedProtocol.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (enumerate)
            arguments.Add("--enumerate");
        arguments.Add("--handshake");
        arguments.Add(hostHandshake);
        // A nonce-bound native guard makes managed cancellation identity-safe even before the
        // TCP authentication handshake. It replaces best-effort /bin/kill against a PID that
        // could have exited and been reused.
        arguments.Add("--cancel-file");
        arguments.Add(hostControl.CancellationPath);
        arguments.Add("--cancel-nonce");
        arguments.Add(hostControl.Nonce);
        if (!string.IsNullOrEmpty(hostToken))
        {
            arguments.Add("--token-file");
            arguments.Add(hostToken);
        }
        return WineEnvironment.BuildWineShellStartInfo(WineHelperLaunchScript, arguments);
    }

    public static bool TryReadHandshake(string handshakePath, out int port, out int pid)
    {
        port = 0;
        pid = 0;
        try
        {
            if (!File.Exists(handshakePath))
                return false;
            var text = File.ReadAllText(handshakePath);
            if (string.IsNullOrWhiteSpace(text))
                return false;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("port", out var p) || !root.TryGetProperty("pid", out var i))
                return false;
            port = p.GetInt32();
            pid = i.GetInt32();
            return port > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool PollHandshake(string handshakePath, int timeoutMs, Func<bool> childAlive, out int port, out int pid)
    {
        port = 0;
        pid = 0;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (!childAlive())
                return false;
            if (TryReadHandshake(handshakePath, out port, out pid))
                return true;
            Thread.Sleep(25);
        }
        return false;
    }

    internal static bool PollWineHandshake(
        string handshakePath,
        WineLaunchControlPaths control,
        int timeoutMs,
        out int port,
        out int helperPid,
        out bool supervisorOwned,
        out string failureReason)
    {
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        port = 0;
        helperPid = 0;
        supervisorOwned = false;
        failureReason = string.Empty;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            supervisorOwned |= IsWineLaunchOwned(control);
            if (TryReadWineLaunchFailure(control, out var launchFailure, out var launchExitCode))
            {
                failureReason =
                    $"Wine host launch failed before handshake ({launchFailure}, exit={launchExitCode})";
                return false;
            }
            if (helperPid == 0)
                TryReadWineLaunchStarted(control, out helperPid);
            if (helperPid > 0 && TryReadWineHelperExit(control, helperPid, out var helperExitCode))
            {
                failureReason = $"native helper exited before handshake (exit={helperExitCode})";
                return false;
            }
            if (TryReadHandshake(handshakePath, out var candidatePort, out var handshakePid))
            {
                if (helperPid == 0)
                {
                    Thread.Sleep(25);
                    continue;
                }
                if (handshakePid != helperPid)
                {
                    failureReason =
                        $"Wine helper PID receipt mismatch (launch={helperPid}, handshake={handshakePid})";
                    return false;
                }
                port = candidatePort;
                return true;
            }
            Thread.Sleep(25);
        }

        supervisorOwned |= IsWineLaunchOwned(control);
        if (helperPid == 0)
            TryReadWineLaunchStarted(control, out helperPid);
        failureReason = !supervisorOwned
            ? "Wine host shell was not dispatched before the launch deadline"
            : helperPid <= 0
                ? "Wine host launch preflight did not start an executable helper"
                : "native helper started but did not publish a handshake before the deadline";
        return false;
    }

    public static string NewHandshakePath()
        => Path.Combine(Path.GetTempPath(), "pc-capture-" + Guid.NewGuid().ToString("N") + ".json");

    internal static WineHelperCandidates StageWineHelper(
        string helperPath,
        SidecarTemporaryPaths paths,
        WineHostOs hostOs)
    {
        if (hostOs != WineHostOs.Linux)
            return new WineHelperCandidates(helperPath, null, null, null);
        if (string.IsNullOrEmpty(paths.PrivateDirectory))
            throw new InvalidOperationException("A staged Linux helper requires a private Wine directory");

        var helperDirectory = Path.GetDirectoryName(helperPath)
            ?? throw new InvalidOperationException("The Linux helper has no containing directory");
        var sourceDsp = Path.Combine(helperDirectory, "libwebrtc-apm.so");
        if (!File.Exists(sourceDsp))
            throw new FileNotFoundException("The Linux helper is missing its WebRTC APM library", sourceDsp);

        var stagedHelper = Path.Combine(paths.PrivateDirectory, "PerfectCommsAudio");
        var stagedDsp = Path.Combine(paths.PrivateDirectory, "libwebrtc-apm.so");
        PublishFileAtomically(helperPath, stagedHelper);
        PublishFileAtomically(sourceDsp, stagedDsp);
        // Hardened Linux hosts commonly mark either the Steam library or /tmp noexec. The host
        // script performs a real --protocol-version execution probe and falls back between both
        // locations; chmod's executable bit alone cannot detect a noexec mount.
        return new WineHelperCandidates(stagedHelper, helperPath, stagedHelper, stagedDsp);
    }

    internal static SidecarTemporaryPaths CreateTemporaryPaths(
        bool wine,
        Func<string, string> resolveWineHostPath,
        WineHostActionExecutor hostAction,
        string? wineTemporaryRoot = null)
    {
        if (!wine)
            return new SidecarTemporaryPaths(NewHandshakePath(), null, null);
        ArgumentNullException.ThrowIfNull(resolveWineHostPath);
        ArgumentNullException.ThrowIfNull(hostAction);

        var root = Path.GetFullPath(wineTemporaryRoot ?? @"Z:\tmp");
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Wine host temporary directory is unavailable: {root}");
        var hostRoot = resolveWineHostPath(root);
        if (string.IsNullOrWhiteSpace(hostRoot))
            throw new DirectoryNotFoundException("Could not resolve the Wine host temporary root");

        string? privateDirectory = null;
        try
        {
            for (var attempt = 0; attempt < 16; attempt++)
            {
                var candidate = Path.Combine(
                    root,
                    WineTemporaryDirectoryPrefix + Guid.NewGuid().ToString("N"));
                if (Directory.Exists(candidate))
                    continue;
                privateDirectory = candidate;
                break;
            }
            if (privateDirectory == null)
                throw new IOException("Could not allocate a private Wine helper directory");

            var leaf = Path.GetFileName(privateDirectory);
            var hostDirectory = AppendHostPath(hostRoot, leaf);
            var pendingToken = Path.Combine(privateDirectory, ".token.pending");
            var receiptPath = Path.Combine(privateDirectory, ".bootstrap-ready");
            var launchOwnershipPath = Path.Combine(privateDirectory, ".launch-owned");
            var expectedReceipt = "perfect-comms-host-action-v1:" + NewSecureNonce();
            var result = hostAction(
                "prepare-private-directory",
                WinePrivateDirectoryScript,
                new[]
                {
                    hostDirectory,
                    AppendHostPath(hostDirectory, Path.GetFileName(pendingToken)),
                    AppendHostPath(hostDirectory, Path.GetFileName(receiptPath)),
                    expectedReceipt,
                    AppendHostPath(hostDirectory, Path.GetFileName(launchOwnershipPath)),
                },
                receiptPath,
                expectedReceipt);
            TryDeleteFile(receiptPath);
            if (!result.Succeeded || !Directory.Exists(privateDirectory) || !File.Exists(pendingToken))
                throw new UnauthorizedAccessException(
                    "Could not securely prepare the Wine helper directory and token slot " +
                    $"({result.DiagnosticSummary})");

            return new SidecarTemporaryPaths(
                Path.Combine(privateDirectory, "handshake.json"),
                privateDirectory,
                root);
        }
        catch
        {
            if (privateDirectory != null)
                TryDeleteDirectory(privateDirectory);
            throw;
        }
    }

    internal static string CreateWineTokenFile(
        SidecarTemporaryPaths paths,
        string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (string.IsNullOrEmpty(paths.PrivateDirectory))
            throw new InvalidOperationException("A Wine token requires a private temporary directory");

        var pendingToken = Path.Combine(paths.PrivateDirectory, ".token.pending");
        var tokenFile = Path.Combine(paths.PrivateDirectory, "token");
        var tokenBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(token);
        if (File.Exists(tokenFile))
            throw new IOException("The Wine helper token has already been published");
        using (var stream = new FileStream(pendingToken, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(0);
            stream.Write(tokenBytes, 0, tokenBytes.Length);
            stream.Flush(true);
        }
        try
        {
            // The host bootstrap created .token.pending as mode 0600 before any secret bytes were
            // written. A same-directory atomic rename preserves that mode and prevents a second
            // publication from silently replacing the live token.
            File.Move(pendingToken, tokenFile);
            return tokenFile;
        }
        catch
        {
            TryDeleteFile(pendingToken);
            TryDeleteFile(tokenFile);
            throw;
        }
    }

    internal static void CleanupTemporaryPaths(
        string? handshakePath,
        string? privateDirectory,
        string? privateRoot)
    {
        if (string.IsNullOrEmpty(privateDirectory))
        {
            if (!string.IsNullOrEmpty(handshakePath) && File.Exists(handshakePath))
                File.Delete(handshakePath);
            return;
        }
        if (string.IsNullOrEmpty(privateRoot))
            throw new InvalidOperationException("Refusing to remove a Wine helper directory without its owning root");

        var fullDirectory = Path.GetFullPath(privateDirectory);
        var fullRoot = Path.GetFullPath(privateRoot);
        var parent = Path.GetDirectoryName(fullDirectory);
        var directoryName = Path.GetFileName(fullDirectory);
        var directorySuffix = directoryName.StartsWith(WineTemporaryDirectoryPrefix, StringComparison.Ordinal)
            ? directoryName.Substring(WineTemporaryDirectoryPrefix.Length)
            : string.Empty;
        if (!Guid.TryParseExact(directorySuffix, "N", out _))
            throw new InvalidOperationException("Refusing to remove an unrecognized Wine helper directory");
        if (string.IsNullOrEmpty(parent) || !PathsEqual(parent, fullRoot))
            throw new InvalidOperationException("Refusing to remove a Wine helper directory outside its owning root");
        if (!string.IsNullOrEmpty(handshakePath))
        {
            var handshakeParent = Path.GetDirectoryName(Path.GetFullPath(handshakePath));
            if (string.IsNullOrEmpty(handshakeParent) || !PathsEqual(handshakeParent, fullDirectory))
                throw new InvalidOperationException("Refusing to clean a handshake outside its Wine helper directory");
        }
        if (Directory.Exists(fullDirectory))
            Directory.Delete(fullDirectory, true);
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static WineLaunchControlPaths CreateWineLaunchControl(SidecarTemporaryPaths paths)
    {
        if (string.IsNullOrEmpty(paths.PrivateDirectory))
            throw new InvalidOperationException("Wine launch control requires a private directory");
        return new WineLaunchControlPaths(
            NewSecureNonce(),
            Path.Combine(paths.PrivateDirectory, ".launch-owned"),
            Path.Combine(paths.PrivateDirectory, ".launch-started"),
            Path.Combine(paths.PrivateDirectory, ".launch-failed"),
            Path.Combine(paths.PrivateDirectory, ".helper-exited"),
            Path.Combine(paths.PrivateDirectory, ".launch-cancelled"));
    }

    internal static WineLaunchControlPaths ResolveHostLaunchControl(
        WineLaunchControlPaths local,
        string hostPrivateDirectory)
        => new(
            local.Nonce,
            AppendHostPath(hostPrivateDirectory, Path.GetFileName(local.OwnershipPath)),
            AppendHostPath(hostPrivateDirectory, Path.GetFileName(local.StartedPath)),
            AppendHostPath(hostPrivateDirectory, Path.GetFileName(local.FailurePath)),
            AppendHostPath(hostPrivateDirectory, Path.GetFileName(local.ExitPath)),
            AppendHostPath(hostPrivateDirectory, Path.GetFileName(local.CancellationPath)));

    internal static bool IsWineLaunchOwned(WineLaunchControlPaths control)
        => TryReadExactFile(
            control.OwnershipPath,
            "perfect-comms-launch-owned-v1:" + control.Nonce);

    internal static bool TryReadWineLaunchStarted(
        WineLaunchControlPaths control,
        out int helperPid)
    {
        helperPid = 0;
        if (!TryReadFile(control.StartedPath, out var value)) return false;
        var prefix = "perfect-comms-launch-started-v1:" + control.Nonce + ":";
        return value.StartsWith(prefix, StringComparison.Ordinal) &&
               int.TryParse(
                   value.Substring(prefix.Length),
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out helperPid) &&
               helperPid > 0;
    }

    internal static bool TryReadWineLaunchFailure(
        WineLaunchControlPaths control,
        out string reason,
        out int exitCode)
    {
        reason = string.Empty;
        exitCode = 0;
        if (!TryReadFile(control.FailurePath, out var value)) return false;
        var prefix = "perfect-comms-launch-failed-v1:" + control.Nonce + ":";
        if (!value.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var payload = value.Substring(prefix.Length);
        var separator = payload.LastIndexOf(':');
        if (separator <= 0 || separator == payload.Length - 1) return false;
        reason = payload.Substring(0, separator);
        return reason.Length <= 80 &&
               !ContainsControlCharacters(reason) &&
               int.TryParse(
                   payload.Substring(separator + 1),
                   System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out exitCode);
    }

    internal static bool TryReadWineHelperExit(
        WineLaunchControlPaths control,
        int expectedPid,
        out int exitCode)
    {
        exitCode = 0;
        if (expectedPid <= 0 || !TryReadFile(control.ExitPath, out var value)) return false;
        var prefix = "perfect-comms-helper-exited-v1:" + control.Nonce + ":" +
                     expectedPid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":";
        return value.StartsWith(prefix, StringComparison.Ordinal) &&
               int.TryParse(
                   value.Substring(prefix.Length),
                   System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out exitCode);
    }

    internal static bool WaitForWineHelperExit(
        WineLaunchControlPaths control,
        int expectedPid,
        int timeoutMs,
        out int exitCode)
    {
        if (timeoutMs < 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        var stopwatch = Stopwatch.StartNew();
        do
        {
            if (TryReadWineHelperExit(control, expectedPid, out exitCode)) return true;
            if (stopwatch.ElapsedMilliseconds >= timeoutMs) break;
            Thread.Sleep(25);
        }
        while (true);
        exitCode = 0;
        return false;
    }

    internal static bool RequestWineLaunchCancellation(WineLaunchControlPaths control)
    {
        var value = "perfect-comms-launch-cancel-v1:" + control.Nonce;
        var temporary = control.CancellationPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(
                temporary,
                value,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, control.CancellationPath);
            return true;
        }
        catch
        {
            TryDeleteFile(temporary);
            return TryReadExactFile(control.CancellationPath, value);
        }
    }

    private static bool TryReadExactFile(string path, string expected)
        => TryReadFile(path, out var value) && string.Equals(value, expected, StringComparison.Ordinal);

    private static bool TryReadFile(string path, out string value)
    {
        value = string.Empty;
        try
        {
            if (!File.Exists(path)) return false;
            value = File.ReadAllText(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string AppendHostPath(string hostDirectory, string leaf)
    {
        ThrowIfNullOrWhiteSpace(hostDirectory, nameof(hostDirectory));
        ThrowIfNullOrWhiteSpace(leaf, nameof(leaf));
        if (leaf.IndexOfAny(new[] { '/', '\\' }) >= 0 || leaf == "." || leaf == "..")
            throw new InvalidDataException("A Wine host path leaf must be a single filename");
        if (ContainsControlCharacters(hostDirectory) || ContainsControlCharacters(leaf))
            throw new InvalidDataException("Wine host paths cannot contain control characters");

        var normalized = hostDirectory.Replace('\\', '/').TrimEnd('/');
        return (normalized.Length == 0 ? string.Empty : normalized) + "/" + leaf;
    }

    private static bool ContainsControlCharacters(string value)
    {
        foreach (var c in value)
            if (char.IsControl(c)) return true;
        return false;
    }

    private static string NewSecureNonce()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void ThrowIfNullOrWhiteSpace(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be null or whitespace.", parameterName);
    }

    public static SidecarDeviceEnumerationResult EnumerateDevices()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            if (!IsHelperAvailable(assembly))
                return SidecarDeviceEnumerationResult.Failure;
            var helperPath = EnsureHelperExtracted(assembly, AppContext.BaseDirectory, force: false);
            return EnumerateDevices(helperPath, WineEnvironment.IsWine, WineEnvironment.ResolveHostPath);
        }
        catch
        {
            return SidecarDeviceEnumerationResult.Failure;
        }
    }

    public static SidecarDeviceEnumerationResult EnumerateDevices(
        string helperPath,
        bool wine,
        Func<string, string> resolveWineHostPath,
        WineHostActionExecutor? hostAction = null,
        WineHostOs? wineHostOs = null)
    {
        var paths = default(SidecarTemporaryPaths);
        var outPath = string.Empty;
        Process? process = null;
        SidecarProcessDiagnostics? diagnostics = null;
        WineLaunchControlPaths? wineControl = null;
        var wineSupervisorOwned = false;
        var wineHelperPid = 0;
        var enumerationSucceeded = false;
        var sw = Stopwatch.StartNew();
        try
        {
            VoiceDiagnostics.Log(
                "sidecar.launch",
                $"event=enumerate-prepare wine={wine} helper=\"{SafeDiagnosticField(helperPath, 512)}\"");
            hostAction ??= WineEnvironment.RunVerifiedHostAction;
            paths = CreateTemporaryPaths(wine, resolveWineHostPath, hostAction);
            outPath = paths.HandshakePath;
            VoiceDiagnostics.Log(
                "sidecar.launch",
                $"event=enumerate-begin wine={wine} helper=\"{SafeDiagnosticField(helperPath, 512)}\" handshake=\"{SafeDiagnosticField(outPath, 320)}\"");
            ProcessStartInfo psi;
            if (wine)
            {
                var hostOs = wineHostOs ?? WineEnvironment.HostOs;
                var launchCandidates = StageWineHelper(helperPath, paths, hostOs);
                wineControl = CreateWineLaunchControl(paths);
                var hostPrivate = resolveWineHostPath(paths.PrivateDirectory!);
                var hostCandidates = new WineHelperCandidates(
                    resolveWineHostPath(launchCandidates.PrimaryPath),
                    launchCandidates.FallbackPath == null
                        ? null
                        : resolveWineHostPath(launchCandidates.FallbackPath),
                    launchCandidates.StagedHelperPath == null
                        ? null
                        : resolveWineHostPath(launchCandidates.StagedHelperPath),
                    launchCandidates.StagedDspPath == null
                        ? null
                        : resolveWineHostPath(launchCandidates.StagedDspPath));
                var hostOut = AppendHostPath(hostPrivate, Path.GetFileName(outPath));
                var hostControl = ResolveHostLaunchControl(wineControl.Value, hostPrivate);
                var quarantineTarget = MacAppDirectory(helperPath);
                var hostQuarantineTarget = quarantineTarget == null
                    ? null
                    : resolveWineHostPath(quarantineTarget);
                psi = BuildWineHelperStartInfo(
                    hostPrivate,
                    hostCandidates,
                    hostOut,
                    hostToken: null,
                    enumerate: true,
                    hostControl,
                    SidecarVoiceClient.Proto,
                    hostQuarantineTarget: hostQuarantineTarget);
            }
            else
            {
                var args = BuildArguments(outPath, Environment.ProcessId, wine: false);
                psi = new ProcessStartInfo(helperPath, $"--enumerate {args}");
            }
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;

            process = Process.Start(psi);
            if (process == null)
            {
                VoiceDiagnostics.Log("sidecar.launch", "event=enumerate-start-failed reason=process-start-null");
                return SidecarDeviceEnumerationResult.Failure;
            }
            diagnostics = AttachProcessDiagnostics(process, token: string.Empty, purpose: "enumerate");
            VoiceDiagnostics.Log("sidecar.launch", $"event=enumerate-process-start pid={SafeProcessId(process)}");
            var pollSw = Stopwatch.StartNew();
            while (pollSw.ElapsedMilliseconds < 3000)
            {
                if (TryReadDevicesFile(outPath, out var input, out var output))
                {
                    enumerationSucceeded = true;
                    VoiceDiagnostics.Log(
                        "sidecar.launch",
                        $"event=enumerate-complete pid={SafeProcessId(process)} inputs={input.Count} outputs={output.Count} elapsedMs={pollSw.ElapsedMilliseconds}");
                    return SidecarDeviceEnumerationResult.Success(input, output);
                }
                if (wineControl is { } control)
                {
                    wineSupervisorOwned |= IsWineLaunchOwned(control);
                    if (wineHelperPid == 0)
                        TryReadWineLaunchStarted(control, out wineHelperPid);
                    if (TryReadWineLaunchFailure(control, out var reason, out var launchExitCode))
                    {
                        VoiceDiagnostics.Log(
                            "sidecar.launch",
                            $"event=enumerate-host-failed reason={reason} exitCode={launchExitCode} elapsedMs={pollSw.ElapsedMilliseconds}");
                        return SidecarDeviceEnumerationResult.Failure;
                    }
                    if (wineHelperPid > 0 &&
                        TryReadWineHelperExit(control, wineHelperPid, out var helperExitCode))
                    {
                        VoiceDiagnostics.Log(
                            "sidecar.launch",
                            $"event=enumerate-helper-exited-before-result helperPid={wineHelperPid} exitCode={helperExitCode} elapsedMs={pollSw.ElapsedMilliseconds}");
                        return SidecarDeviceEnumerationResult.Failure;
                    }
                }
                Thread.Sleep(25);
            }
            VoiceDiagnostics.Log(
                "sidecar.launch",
                $"event=enumerate-timeout pid={SafeProcessId(process)} elapsedMs={pollSw.ElapsedMilliseconds}");
            return SidecarDeviceEnumerationResult.Failure;
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log(
                "sidecar.launch",
                $"event=enumerate-failed elapsedMs={sw.ElapsedMilliseconds} error={ExceptionDiagnostic(ex)}");
            return SidecarDeviceEnumerationResult.Failure;
        }
        finally
        {
            if (process != null && !wine)
                KillQuietly(process);
            diagnostics?.Complete("enumerate-finished");
            var wineHelperExited = false;
            if (wineControl is { } control)
            {
                wineSupervisorOwned |= IsWineLaunchOwned(control);
                if (wineHelperPid == 0)
                    TryReadWineLaunchStarted(control, out wineHelperPid);
                if (!enumerationSucceeded)
                    RequestWineLaunchCancellation(control);
                if (wineHelperPid > 0)
                    wineHelperExited = WaitForWineHelperExit(
                        control,
                        wineHelperPid,
                        timeoutMs: 1_000,
                        out _);
            }
            if (!wineSupervisorOwned || wineHelperExited)
            {
                try { CleanupTemporaryPaths(outPath, paths.PrivateDirectory, paths.PrivateRoot); } catch { }
            }
            if (process != null)
            {
                if (wine && !ProcessExited(process))
                    KillQuietly(process);
                try { process.Dispose(); } catch { }
            }
        }
    }

    internal static bool TryReadDevicesFile(string path, out List<VoiceDeviceInfo> input, out List<VoiceDeviceInfo> output)
    {
        input = new List<VoiceDeviceInfo>();
        output = new List<VoiceDeviceInfo>();
        try
        {
            if (!File.Exists(path))
                return false;
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
                return false;
            return SidecarProtocol.TryReadDevices(text, out input) &&
                   SidecarProtocol.TryReadOutputDevices(text, out output);
        }
        catch
        {
            return false;
        }
    }

    public static SidecarLaunchResult Launch(
        string helperPath,
        string token,
        int handshakeTimeoutMs,
        bool wine,
        Func<string, string> resolveWineHostPath,
        WineHostActionExecutor? hostAction = null,
        WineHostOs? wineHostOs = null)
    {
        var result = new SidecarLaunchResult { Wine = wine };
        var paths = default(SidecarTemporaryPaths);
        Process? process = null;
        SidecarProcessDiagnostics? diagnostics = null;
        string? tokenFile = null;
        var sw = Stopwatch.StartNew();
        try
        {
            VoiceDiagnostics.Log(
                "sidecar.launch",
                $"event=prepare wine={wine} timeoutMs={handshakeTimeoutMs} helper=\"{SafeDiagnosticField(helperPath, 512)}\"");
            hostAction ??= WineEnvironment.RunVerifiedHostAction;
            paths = CreateTemporaryPaths(wine, resolveWineHostPath, hostAction);
            result.HandshakePath = paths.HandshakePath;
            result.TemporaryDirectory = paths.PrivateDirectory;
            result.TemporaryRoot = paths.PrivateRoot;
            VoiceDiagnostics.Log(
                "sidecar.launch",
                $"event=begin wine={wine} timeoutMs={handshakeTimeoutMs} helper=\"{SafeDiagnosticField(helperPath, 512)}\" handshake=\"{SafeDiagnosticField(result.HandshakePath, 320)}\"");
            if (wine)
            {
                tokenFile = CreateWineTokenFile(paths, token);
                var hostOs = wineHostOs ?? WineEnvironment.HostOs;
                var launchCandidates = StageWineHelper(helperPath, paths, hostOs);
                var control = CreateWineLaunchControl(paths);
                result.WineControl = control;
                var hostPrivate = resolveWineHostPath(paths.PrivateDirectory!);
                var hostToken = AppendHostPath(hostPrivate, Path.GetFileName(tokenFile));
                var hostCandidates = new WineHelperCandidates(
                    resolveWineHostPath(launchCandidates.PrimaryPath),
                    launchCandidates.FallbackPath == null
                        ? null
                        : resolveWineHostPath(launchCandidates.FallbackPath),
                    launchCandidates.StagedHelperPath == null
                        ? null
                        : resolveWineHostPath(launchCandidates.StagedHelperPath),
                    launchCandidates.StagedDspPath == null
                        ? null
                        : resolveWineHostPath(launchCandidates.StagedDspPath));
                var hostHandshake = AppendHostPath(hostPrivate, Path.GetFileName(result.HandshakePath));
                var hostControl = ResolveHostLaunchControl(control, hostPrivate);
                var quarantineTarget = MacAppDirectory(helperPath);
                var hostQuarantineTarget = quarantineTarget == null
                    ? null
                    : resolveWineHostPath(quarantineTarget);
                var wpsi = BuildWineHelperStartInfo(
                    hostPrivate,
                    hostCandidates,
                    hostHandshake,
                    hostToken,
                    enumerate: false,
                    hostControl,
                    SidecarVoiceClient.Proto,
                    hostQuarantineTarget: hostQuarantineTarget);

                process = Process.Start(wpsi);
                if (process == null)
                {
                    result.FailureReason = "Process.Start returned null (start.exe missing)";
                    return result;
                }
                result.Process = process;
                diagnostics = AttachProcessDiagnostics(process, token, "voice-wine");
                result.Diagnostics = diagnostics;
                VoiceDiagnostics.Log("sidecar.launch", $"event=process-start pid={SafeProcessId(process)} wine=true");

                if (!PollWineHandshake(
                        result.HandshakePath,
                        control,
                        handshakeTimeoutMs,
                        out var wport,
                        out var wpid,
                        out var supervisorOwned,
                        out var wineFailure))
                {
                    result.WineSupervisorOwned = supervisorOwned;
                    result.Pid = wpid;
                    RequestWineLaunchCancellation(control);
                    result.FailureReason = wineFailure;
                    return result;
                }

                result.WineSupervisorOwned = true;
                result.Port = wport;
                result.Pid = wpid;
                result.Success = true;
                VoiceDiagnostics.Log(
                    "sidecar.launch",
                    $"event=handshake-ready launcherPid={SafeProcessId(process)} helperPid={wpid} port={wport} wine=true elapsedMs={sw.ElapsedMilliseconds}");
                return result;
            }

            var nativeArguments = BuildArguments(result.HandshakePath, Environment.ProcessId, wine: false);
            var psi = new ProcessStartInfo(helperPath, nativeArguments)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            process = Process.Start(psi);
            if (process == null)
            {
                result.FailureReason = "Process.Start returned null";
                return result;
            }
            result.Process = process;
            diagnostics = AttachProcessDiagnostics(process, token, "voice");
            result.Diagnostics = diagnostics;
            VoiceDiagnostics.Log("sidecar.launch", $"event=process-start pid={SafeProcessId(process)} wine=false");

            try
            {
                process.StandardInput.WriteLine(token);
                process.StandardInput.Flush();
                process.StandardInput.Close();
            }
            catch (Exception ex)
            {
                result.FailureReason = "token write failed: " + ex.Message;
                KillQuietly(process);
                return result;
            }

            if (!PollHandshake(result.HandshakePath, handshakeTimeoutMs, () => !ProcessExited(process), out var port, out var pid))
            {
                result.FailureReason = ProcessExited(process)
                    ? "helper exited before handshake (host-exec blocked or crash)"
                    : "handshake timeout";
                KillQuietly(process);
                return result;
            }

            result.Port = port;
            result.Pid = pid;
            result.Success = true;
            VoiceDiagnostics.Log(
                "sidecar.launch",
                $"event=handshake-ready launcherPid={SafeProcessId(process)} helperPid={pid} port={port} wine=false elapsedMs={sw.ElapsedMilliseconds}");
            return result;
        }
        catch (Exception ex)
        {
            result.FailureReason = "launch failed: " + ex.Message;
            if (process != null)
                KillQuietly(process);
            return result;
        }
        finally
        {
            if (!result.Success)
            {
                VoiceDiagnostics.Log(
                    "sidecar.launch",
                    $"event=failed elapsedMs={sw.ElapsedMilliseconds} reason=\"{SafeDiagnosticField(result.FailureReason, 320)}\"");
                diagnostics?.Complete("launch-failed");
            }
            if (tokenFile != null)
            {
                try { File.Delete(tokenFile); } catch { }
            }
            if (!result.Success)
            {
                if (result.WineControl is { } control)
                {
                    RequestWineLaunchCancellation(control);
                    result.WineSupervisorOwned |= IsWineLaunchOwned(control);
                    if (result.Pid <= 0 && TryReadWineLaunchStarted(control, out var startedPid))
                        result.Pid = startedPid;
                }
                // Once the host supervisor has claimed the directory, it owns the helper child
                // and fixed-file cleanup. Removing the directory here races a detached host
                // process and was the source of orphaned retries on Wine/CrossOver.
                if (!result.WineSupervisorOwned)
                {
                    try { CleanupTemporaryPaths(result.HandshakePath, result.TemporaryDirectory, result.TemporaryRoot); } catch { }
                }
                if (process != null)
                {
                    if (!ProcessExited(process))
                        KillQuietly(process);
                    try { process.Dispose(); } catch { }
                    result.Process = null;
                }
            }
        }
    }

    private static SidecarProcessDiagnostics AttachProcessDiagnostics(Process process, string token, string purpose)
    {
        var diagnostics = new SidecarProcessDiagnostics(process, token, purpose);
        diagnostics.Attach();
        return diagnostics;
    }

    internal static string SanitizeStderrForDiagnostics(string? line, string token)
    {
        if (string.IsNullOrEmpty(line)) return string.Empty;

        var normalized = new StringBuilder(Math.Min(line.Length, SidecarProcessDiagnostics.MaxLineChars));
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            normalized.Append(char.IsControl(c) || c == '"' ? ' ' : c);
        }
        var safe = normalized.ToString();
        if (!string.IsNullOrEmpty(token))
            safe = safe.Replace(token, "[redacted-token]", StringComparison.Ordinal);

        safe = RedactNamedValue(safe, "candidate");
        safe = RedactNamedValue(safe, "sdp");
        safe = RedactNamedValue(safe, "credential");
        safe = RedactNamedValue(safe, "authorization");
        safe = RedactNamedValue(safe, "token");

        if (safe.Length > SidecarProcessDiagnostics.MaxLineChars)
            safe = safe.Substring(0, SidecarProcessDiagnostics.MaxLineChars) + "...";
        return safe;
    }

    private static string RedactNamedValue(string value, string name)
    {
        var searchFrom = 0;
        while (searchFrom < value.Length)
        {
            var nameIndex = value.IndexOf(name, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (nameIndex < 0) return value;
            var suffixStart = nameIndex + name.Length;
            if (suffixStart + 6 <= value.Length &&
                value.AsSpan(suffixStart, 6).Equals("_bytes".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                searchFrom = suffixStart + 6;
                continue;
            }
            var separatorLimit = Math.Min(value.Length, nameIndex + name.Length + 8);
            for (var i = nameIndex + name.Length; i < separatorLimit; i++)
            {
                if (value[i] != '=' && value[i] != ':') continue;
                return value.Substring(0, i + 1) + "[redacted]";
            }
            searchFrom = nameIndex + name.Length;
        }
        return value;
    }

    private static string SafeDiagnosticField(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var length = Math.Min(value.Length, maxChars);
        var builder = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            var c = value[i];
            builder.Append(char.IsControl(c) || c == '"' ? ' ' : c);
        }
        if (value.Length > maxChars) builder.Append("...");
        return builder.ToString();
    }

    private static int SafeProcessId(Process process)
    {
        try { return process.Id; }
        catch { return -1; }
    }

    private static string ExceptionDiagnostic(Exception ex)
        => $"{SafeDiagnosticField(ex.GetType().Name, 80)}:\"{SafeDiagnosticField(ex.Message, 240)}\"";

    private static bool ProcessExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private static void KillQuietly(Process process)
    {
        try { if (!process.HasExited) process.Kill(); } catch { }
        try { process.WaitForExit(500); } catch { }
    }
}

internal sealed class SidecarLaunchResult
{
    public bool Success;
    public bool Wine;
    public bool WineSupervisorOwned;
    public int Port;
    public int Pid;
    public Process? Process;
    public SidecarProcessDiagnostics? Diagnostics;
    public string HandshakePath = "";
    public string? TemporaryDirectory;
    public string? TemporaryRoot;
    public WineLaunchControlPaths? WineControl;
    public string FailureReason = "";
}

internal sealed class SidecarProcessDiagnostics
{
    internal const int MaxLineChars = 512;
    private const int MaxLoggedLines = 200;
    private const int MaxLinesPerWindow = 20;
    private const int WindowMs = 1000;

    private readonly object _gate = new();
    private readonly Process _process;
    private readonly string _token;
    private readonly string _purpose;
    private long _windowStartTick;
    private int _windowLines;
    private int _logged;
    private int _dropped;
    private int _summaryLogged;

    public SidecarProcessDiagnostics(Process process, string token, string purpose)
    {
        _process = process;
        _token = token ?? string.Empty;
        _purpose = purpose;
        _windowStartTick = Environment.TickCount64;
    }

    public void Attach()
    {
        var captureStdout = false;
        var captureStderr = false;
        try
        {
            captureStdout = _process.StartInfo.RedirectStandardOutput;
            captureStderr = _process.StartInfo.RedirectStandardError;
            if (captureStdout)
            {
                _process.OutputDataReceived += (_, args) =>
                {
                    if (args.Data != null)
                        LogLine("stdout", args.Data);
                };
            }
            if (captureStderr)
            {
                _process.ErrorDataReceived += (_, args) =>
                {
                    if (args.Data != null)
                        LogLine("stderr", args.Data);
                };
            }
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) =>
            {
                var exitCode = -1;
                try { exitCode = _process.ExitCode; } catch { }
                VoiceDiagnostics.Log(
                    "sidecar.launch",
                    $"event=process-exit purpose={_purpose} pid={SafePid()} exitCode={exitCode}");
            };
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log(
                "sidecar.stderr",
                $"event=capture-failed purpose={_purpose} pid={SafePid()} error={ex.GetType().Name}:\"{SidecarLauncher.SanitizeStderrForDiagnostics(ex.Message, _token)}\"");
            return;
        }

        // Wine start.exe writes some fatal /unix dispatch errors to stdout. Both redirected
        // streams must be drained for helper launches: otherwise a verbose wrapper can fill the
        // stdout pipe and turn a useful launch error into a generic handshake timeout. Start the
        // streams independently so one late/closed pipe cannot prevent the other from draining.
        if (captureStdout) BeginRead("stdout", _process.BeginOutputReadLine);
        if (captureStderr) BeginRead("stderr", _process.BeginErrorReadLine);
        VoiceDiagnostics.Log(
            "sidecar.stderr",
            $"event=capture-start purpose={_purpose} pid={SafePid()} stdout={captureStdout.ToString().ToLowerInvariant()} stderr={captureStderr.ToString().ToLowerInvariant()} perSecondLimit={MaxLinesPerWindow} totalLimit={MaxLoggedLines} maxLineChars={MaxLineChars}");
    }

    private void BeginRead(string source, Action begin)
    {
        try
        {
            begin();
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log(
                "sidecar.stderr",
                $"event=stream-capture-failed purpose={_purpose} pid={SafePid()} source={source} error={ex.GetType().Name}:\"{SidecarLauncher.SanitizeStderrForDiagnostics(ex.Message, _token)}\"");
        }
    }

    private void LogLine(string source, string line)
    {
        string? safe = null;
        var sequence = 0;
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (now - _windowStartTick >= WindowMs)
            {
                _windowStartTick = now;
                _windowLines = 0;
            }
            if (_logged >= MaxLoggedLines || _windowLines >= MaxLinesPerWindow)
            {
                _dropped++;
                return;
            }
            _windowLines++;
            sequence = ++_logged;
            safe = SidecarLauncher.SanitizeStderrForDiagnostics(line, _token);
        }

        VoiceDiagnostics.Log(
            "sidecar.stderr",
            $"purpose={_purpose} pid={SafePid()} source={source} seq={sequence} text=\"{safe}\"");
    }

    public void Complete(string reason)
    {
        if (Interlocked.Exchange(ref _summaryLogged, 1) != 0) return;
        int logged;
        int dropped;
        lock (_gate)
        {
            logged = _logged;
            dropped = _dropped;
        }
        VoiceDiagnostics.Log(
            "sidecar.stderr",
            $"event=capture-stop purpose={_purpose} pid={SafePid()} reason={reason} logged={logged} dropped={dropped}");
    }

    private int SafePid()
    {
        try { return _process.Id; }
        catch { return -1; }
    }
}
