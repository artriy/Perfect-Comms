using System.Diagnostics;
using VoiceChatPlugin;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class SidecarLauncherCacheTests
{
    private const string Triple = "x86_64-pc-windows-msvc";
    private const string CacheFixtureResource = "PerfectComms.Tests.cache-test-payload";

    [Fact]
    public void NativeLaunchArgumentsIncludeManagedOwnerPid()
    {
        var arguments = SidecarLauncher.BuildArguments(@"C:\Temp Folder\handshake.json", 4242, wine: false);

        Assert.Equal("--handshake \"C:\\Temp Folder\\handshake.json\" --owner-pid 4242", arguments);
    }

    [Fact]
    public void WineLaunchArgumentsOmitManagedOwnerPid()
    {
        var arguments = SidecarLauncher.BuildArguments("/tmp/handshake.json", 4242, wine: true);

        Assert.Equal("--handshake \"/tmp/handshake.json\"", arguments);
        Assert.DoesNotContain("owner-pid", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedNativePlatformsAndArchitecturesFailClosed()
    {
        Assert.Throws<PlatformNotSupportedException>(() => SidecarLauncher.TargetTripleFor(
            wine: false,
            WineHostOs.Unknown,
            windows: true,
            macOs: false,
            linux: false,
            System.Runtime.InteropServices.Architecture.Arm64));
        Assert.Throws<PlatformNotSupportedException>(() => SidecarLauncher.TargetTripleFor(
            wine: false,
            WineHostOs.Unknown,
            windows: false,
            macOs: false,
            linux: false,
            System.Runtime.InteropServices.Architecture.X64));
        Assert.Equal("x86_64-unknown-linux-gnu", SidecarLauncher.TargetTripleFor(
            wine: false,
            WineHostOs.Unknown,
            windows: false,
            macOs: false,
            linux: true,
            System.Runtime.InteropServices.Architecture.X64));
        Assert.Equal("i686-pc-windows-msvc", SidecarLauncher.TargetTripleFor(
            wine: false,
            WineHostOs.Unknown,
            windows: true,
            macOs: false,
            linux: false,
            System.Runtime.InteropServices.Architecture.X86));
        Assert.Equal("x86_64-pc-windows-msvc", SidecarLauncher.TargetTripleFor(
            wine: false,
            WineHostOs.Unknown,
            windows: true,
            macOs: false,
            linux: false,
            System.Runtime.InteropServices.Architecture.X64));
    }

    [Fact]
    public void WineBootstrapAcceptsVerifiedReceiptDespiteNonzeroWrapperExit()
    {
        var root = NewTemporaryDirectory();
        var actionCalls = 0;
        try
        {
            WineHostActionResult Prepare(
                string operation,
                string script,
                IReadOnlyList<string> hostArguments,
                string receiptPath,
                string expectedReceipt)
            {
                actionCalls++;
                Assert.Equal("prepare-private-directory", operation);
                Assert.Equal(SidecarLauncher.WinePrivateDirectoryScript, script);
                Assert.Equal(5, hostArguments.Count);
                Assert.Empty(Directory.EnumerateDirectories(root));
                Assert.Equal(expectedReceipt, hostArguments[3]);
                Assert.EndsWith("/.launch-owned", hostArguments[4], StringComparison.Ordinal);

                var directory = Path.GetDirectoryName(receiptPath)!;
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, ".token.pending"), string.Empty);
                File.WriteAllText(receiptPath, expectedReceipt);
                // This is the Proton-CachyOS regression: the wrapper says 1, but the
                // nonce-bound receipt proves the host action completed successfully.
                return WineHostActionResult.Verified(wrapperExitCode: 1);
            }

            var paths = SidecarLauncher.CreateTemporaryPaths(
                wine: true,
                static path => path,
                Prepare,
                root);
            Assert.NotNull(paths.PrivateDirectory);
            Assert.Equal(Path.Combine(paths.PrivateDirectory!, "handshake.json"), paths.HandshakePath);
            Assert.Equal(1, actionCalls);

            var tokenFile = SidecarLauncher.CreateWineTokenFile(
                paths,
                "test-secret");
            Assert.Equal("test-secret", File.ReadAllText(tokenFile));
            Assert.Throws<IOException>(() => SidecarLauncher.CreateWineTokenFile(
                paths,
                "replacement"));
            Assert.Equal(1, actionCalls);

            SidecarLauncher.CleanupTemporaryPaths(
                paths.HandshakePath,
                paths.PrivateDirectory,
                paths.PrivateRoot);
            Assert.Empty(Directory.EnumerateDirectories(root));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void WineCleanupRejectsPrefixNamedDirectoryOutsideOwningRoot()
    {
        var parent = NewTemporaryDirectory();
        var owningRoot = Path.Combine(parent, "owning-root");
        var outside = Path.Combine(parent, "perfect-comms-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(owningRoot);
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "handshake.json");
        File.WriteAllText(sentinel, "keep");
        try
        {
            Assert.Throws<InvalidOperationException>(() => SidecarLauncher.CleanupTemporaryPaths(
                sentinel,
                outside,
                owningRoot));
            Assert.True(File.Exists(sentinel));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void WineBootstrapRemovesDirectoryWhenPermissionSetupFails()
    {
        var root = NewTemporaryDirectory();
        try
        {
            WineHostActionResult FailAfterCreatingDirectory(
                string _,
                string __,
                IReadOnlyList<string> ___,
                string receiptPath,
                string ____)
            {
                var directory = Path.GetDirectoryName(receiptPath)!;
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, ".token.pending"), string.Empty);
                return new WineHostActionResult(
                    true, false, true, 0, "receipt-missing", 5000, string.Empty);
            }

            Assert.Throws<UnauthorizedAccessException>(() => SidecarLauncher.CreateTemporaryPaths(
                wine: true,
                static path => path,
                FailAfterCreatingDirectory,
                root));
            Assert.Empty(Directory.EnumerateDirectories(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void NonWineTemporaryPathsDoNotInvokeWinePlumbing()
    {
        var paths = SidecarLauncher.CreateTemporaryPaths(
            wine: false,
            static _ => throw new InvalidOperationException("winepath must not run"),
            static (_, _, _, _, _) => throw new InvalidOperationException("host action must not run"));

        Assert.Null(paths.PrivateDirectory);
        Assert.Null(paths.PrivateRoot);
        Assert.StartsWith(Path.GetTempPath(), paths.HandshakePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WineHelperLaunchUsesPositionalPathsAndNeverExposesTokenValue()
    {
        const string secret = "super-secret-token-value";
        var localDirectory = NewTemporaryDirectory();
        File.WriteAllText(Path.Combine(localDirectory, ".token.pending"), string.Empty);
        var localPaths = new SidecarTemporaryPaths(
            Path.Combine(localDirectory, "handshake.json"),
            localDirectory,
            Path.GetDirectoryName(localDirectory));
        var localToken = SidecarLauncher.CreateWineTokenFile(localPaths, secret);
        var oddRoot = "/tmp/Perfect Comms/odd ' $() ; &";
        var nonce = new string('A', 64);
        var control = new WineLaunchControlPaths(
            nonce,
            oddRoot + "/.launch-owned",
            oddRoot + "/.launch-started",
            oddRoot + "/.launch-failed",
            oddRoot + "/.helper-exited",
            oddRoot + "/.launch-cancelled");
        try
        {
            var psi = SidecarLauncher.BuildWineHelperStartInfo(
                oddRoot,
                new WineHelperCandidates(
                    oddRoot + "/PerfectCommsAudio",
                    oddRoot + "/Original Helper",
                    oddRoot + "/PerfectCommsAudio",
                    oddRoot + "/libwebrtc-apm.so"),
                oddRoot + "/handshake.json",
                oddRoot + "/token",
                enumerate: false,
                control,
                SidecarVoiceClient.Proto,
                hostQuarantineTarget: oddRoot + "/PerfectCommsAudio.app");

            Assert.Equal(secret, File.ReadAllText(localToken));
            Assert.Equal("start.exe", psi.FileName);
            Assert.Equal("/unix", psi.ArgumentList[0]);
            Assert.Equal("/bin/sh", psi.ArgumentList[1]);
            Assert.Equal("-c", psi.ArgumentList[2]);
            Assert.Equal(SidecarLauncher.WineHelperLaunchScript, psi.ArgumentList[3]);
            Assert.Contains("--handshake", psi.ArgumentList);
            Assert.Contains("--token-file", psi.ArgumentList);
            Assert.Contains("--cancel-file", psi.ArgumentList);
            Assert.Contains("--cancel-nonce", psi.ArgumentList);
            Assert.Contains(nonce, psi.ArgumentList);
            Assert.DoesNotContain(secret, psi.Arguments, StringComparison.Ordinal);
            Assert.All(psi.ArgumentList, argument =>
                Assert.DoesNotContain(secret, argument, StringComparison.Ordinal));
            Assert.DoesNotContain(secret, SidecarLauncher.SanitizeStderrForDiagnostics(
                "helper failed near token=" + secret,
                secret), StringComparison.Ordinal);
            Assert.DoesNotContain(oddRoot, SidecarLauncher.WineHelperLaunchScript, StringComparison.Ordinal);
            Assert.DoesNotContain("/wait", psi.ArgumentList);
            Assert.True(psi.RedirectStandardOutput);
            Assert.True(psi.RedirectStandardError);
        }
        finally
        {
            Directory.Delete(localDirectory, true);
        }
    }

    [Fact]
    public void LinuxWineLaunchStagesHelperAndDspInsidePrivateExecLocation()
    {
        var root = NewTemporaryDirectory();
        var source = Path.Combine(root, "source");
        var privateDirectory = Path.Combine(root, "perfect-comms-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(privateDirectory);
        var helper = Path.Combine(source, "PerfectCommsAudio");
        File.WriteAllText(helper, "linux-helper");
        File.WriteAllText(Path.Combine(source, "libwebrtc-apm.so"), "linux-apm");
        var paths = new SidecarTemporaryPaths(
            Path.Combine(privateDirectory, "handshake.json"),
            privateDirectory,
            root);
        try
        {
            var staged = SidecarLauncher.StageWineHelper(helper, paths, WineHostOs.Linux);

            Assert.Equal(Path.Combine(privateDirectory, "PerfectCommsAudio"), staged.PrimaryPath);
            Assert.Equal(helper, staged.FallbackPath);
            Assert.Equal(staged.PrimaryPath, staged.StagedHelperPath);
            Assert.Equal(Path.Combine(privateDirectory, "libwebrtc-apm.so"), staged.StagedDspPath);
            Assert.Equal("linux-helper", File.ReadAllText(staged.PrimaryPath));
            Assert.Equal("linux-apm", File.ReadAllText(Path.Combine(privateDirectory, "libwebrtc-apm.so")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void WineLaunchReceiptsBindOwnershipHandshakeExitAndCancellationToNonceAndPid()
    {
        var privateDirectory = NewTemporaryDirectory();
        var paths = new SidecarTemporaryPaths(
            Path.Combine(privateDirectory, "handshake.json"),
            privateDirectory,
            Path.GetDirectoryName(privateDirectory));
        var control = SidecarLauncher.CreateWineLaunchControl(paths);
        try
        {
            File.WriteAllText(
                control.OwnershipPath,
                "perfect-comms-launch-owned-v1:wrong-nonce");
            Assert.False(SidecarLauncher.IsWineLaunchOwned(control));
            File.WriteAllText(
                control.OwnershipPath,
                $"perfect-comms-launch-owned-v1:{control.Nonce}");
            File.WriteAllText(
                control.StartedPath,
                $"perfect-comms-launch-started-v1:{control.Nonce}:4242");
            File.WriteAllText(paths.HandshakePath, "{\"port\":54321,\"pid\":4242}");

            Assert.True(SidecarLauncher.PollWineHandshake(
                paths.HandshakePath,
                control,
                timeoutMs: 500,
                out var port,
                out var helperPid,
                out var supervisorOwned,
                out var failure));
            Assert.Equal(54321, port);
            Assert.Equal(4242, helperPid);
            Assert.True(supervisorOwned);
            Assert.Equal(string.Empty, failure);

            File.WriteAllText(
                control.ExitPath,
                $"perfect-comms-helper-exited-v1:{control.Nonce}:9999:0");
            Assert.False(SidecarLauncher.TryReadWineHelperExit(control, 4242, out _));
            File.WriteAllText(
                control.ExitPath,
                $"perfect-comms-helper-exited-v1:{control.Nonce}:4242:7");
            Assert.True(SidecarLauncher.WaitForWineHelperExit(control, 4242, 100, out var exitCode));
            Assert.Equal(7, exitCode);

            Assert.True(SidecarLauncher.RequestWineLaunchCancellation(control));
            Assert.Equal(
                $"perfect-comms-launch-cancel-v1:{control.Nonce}",
                File.ReadAllText(control.CancellationPath));
        }
        finally
        {
            Directory.Delete(privateDirectory, true);
        }
    }

    [Fact]
    public void WineLaunchRejectsPidMismatchAndAuthenticatesFailureReceipt()
    {
        var privateDirectory = NewTemporaryDirectory();
        var paths = new SidecarTemporaryPaths(
            Path.Combine(privateDirectory, "handshake.json"),
            privateDirectory,
            Path.GetDirectoryName(privateDirectory));
        var control = SidecarLauncher.CreateWineLaunchControl(paths);
        try
        {
            File.WriteAllText(
                control.OwnershipPath,
                $"perfect-comms-launch-owned-v1:{control.Nonce}");
            File.WriteAllText(
                control.StartedPath,
                $"perfect-comms-launch-started-v1:{control.Nonce}:4242");
            File.WriteAllText(paths.HandshakePath, "{\"port\":54321,\"pid\":4243}");

            Assert.False(SidecarLauncher.PollWineHandshake(
                paths.HandshakePath,
                control,
                timeoutMs: 500,
                out _,
                out _,
                out _,
                out var mismatch));
            Assert.Contains("PID receipt mismatch", mismatch, StringComparison.Ordinal);

            File.WriteAllText(
                control.FailurePath,
                "perfect-comms-launch-failed-v1:wrong:no-executable-candidate:126");
            Assert.False(SidecarLauncher.TryReadWineLaunchFailure(control, out _, out _));
            File.WriteAllText(
                control.FailurePath,
                $"perfect-comms-launch-failed-v1:{control.Nonce}:no-executable-candidate:126");
            Assert.True(SidecarLauncher.TryReadWineLaunchFailure(
                control,
                out var reason,
                out var exitCode));
            Assert.Equal("no-executable-candidate", reason);
            Assert.Equal(126, exitCode);
        }
        finally
        {
            Directory.Delete(privateDirectory, true);
        }
    }

    [Fact]
    public void MacDspInsideSignedAppIsNeverReplacedByStandaloneResource()
    {
        var root = NewTemporaryDirectory();
        var macDirectory = Path.Combine(root, "PerfectCommsAudio.app", "Contents", "MacOS");
        Directory.CreateDirectory(macDirectory);
        var helper = Path.Combine(macDirectory, "PerfectCommsAudio");
        var signedDsp = Path.Combine(macDirectory, "libwebrtc-apm.dylib");
        File.WriteAllText(helper, "signed-helper");
        File.WriteAllText(signedDsp, "signed-in-app-apm");
        try
        {
            SidecarLauncher.EnsureDspLibsExtracted(
                typeof(SidecarLauncher).Assembly,
                root,
                "x86_64-apple-darwin",
                helper,
                "bundle-v1-test");

            Assert.Equal("signed-in-app-apm", File.ReadAllText(signedDsp));
            Assert.Empty(SidecarLauncher.DspLibsFor("x86_64-apple-darwin"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void HostBootstrapScriptCreatesExactPrivateModesOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = NewTemporaryDirectory();
        var privateDirectory = Path.Combine(root, "perfect-comms-" + Guid.NewGuid().ToString("N"));
        var token = Path.Combine(privateDirectory, ".token.pending");
        var receipt = Path.Combine(privateDirectory, ".bootstrap-ready");
        var launchOwned = Path.Combine(privateDirectory, ".launch-owned");
        const string nonce = "perfect-comms-host-action-v1:test";
        try
        {
            var psi = new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(SidecarLauncher.WinePrivateDirectoryScript);
            psi.ArgumentList.Add("perfect-comms-bootstrap");
            psi.ArgumentList.Add(privateDirectory);
            psi.ArgumentList.Add(token);
            psi.ArgumentList.Add(receipt);
            psi.ArgumentList.Add(nonce);
            psi.ArgumentList.Add(launchOwned);
            using var process = Process.Start(psi);
            Assert.NotNull(process);
            Assert.True(process!.WaitForExit(5000));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(nonce, File.ReadAllText(receipt));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(privateDirectory));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(token));
            // Release the bootstrap watchdog instead of leaving its bounded abandonment timer
            // alive for the rest of the Unix test job.
            File.WriteAllText(launchOwned, "claimed-by-test");
            Thread.Sleep(1_100);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void WineHostShellScriptsPassPosixSyntaxCheck()
    {
        Assert.DoesNotContain("/timeout", SidecarLauncher.WineHelperLaunchScript, StringComparison.Ordinal);
        Assert.Contains(
            "if [ ! -d \"$private_dir\" ]; then exit 0; fi",
            SidecarLauncher.WinePrivateDirectoryScript,
            StringComparison.Ordinal);

        if (OperatingSystem.IsWindows()) return;
        const string shell = "/bin/sh";
        if (!File.Exists(shell)) return;

        foreach (var script in new[]
                 {
                     SidecarLauncher.WinePrivateDirectoryScript,
                     SidecarLauncher.WineHelperLaunchScript,
                 })
        {
            var psi = new ProcessStartInfo(shell)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);
            using var process = Process.Start(psi);
            Assert.NotNull(process);
            Assert.True(process!.WaitForExit(5000));
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.ExitCode == 0, stderr);
        }
    }

    [Fact]
    public void HostLaunchScriptPreservesOddPositionalPathsOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;

        var parent = NewTemporaryDirectory();
        var privateDirectory = Path.Combine(parent, "perfect comms ' $() ; &");
        Directory.CreateDirectory(privateDirectory);
        var token = Path.Combine(privateDirectory, "token odd");
        var helper = Path.Combine(privateDirectory, "helper odd ' $() ; &");
        var handshake = Path.Combine(privateDirectory, "handshake odd ' $() ; &.json");
        var launchOwned = Path.Combine(privateDirectory, ".launch-owned");
        var launchStarted = Path.Combine(privateDirectory, ".launch-started");
        var launchFailed = Path.Combine(privateDirectory, ".launch-failed");
        var helperExited = Path.Combine(privateDirectory, ".helper-exited");
        var launchCancelled = Path.Combine(privateDirectory, ".launch-cancelled");
        const string launchNonce = "ABCDEF0123456789";
        File.WriteAllText(token, "secret");
        File.WriteAllText(
            helper,
            "#!/bin/sh\n" +
            "if [ \"$1\" = \"--protocol-version\" ]; then printf '%s\\n' 13; exit 0; fi\n" +
            "while [ \"$#\" -gt 0 ]; do\n" +
            "  if [ \"$1\" = \"--handshake\" ]; then shift; printf '%s' ok > \"$1\"; exit 0; fi\n" +
            "  shift\n" +
            "done\n" +
            "exit 2\n");
        try
        {
            var psi = new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(SidecarLauncher.WineHelperLaunchScript);
            psi.ArgumentList.Add("perfect-comms-bootstrap");
            psi.ArgumentList.Add(privateDirectory);
            psi.ArgumentList.Add(token);
            psi.ArgumentList.Add(helper);
            psi.ArgumentList.Add(string.Empty);
            psi.ArgumentList.Add(string.Empty);
            psi.ArgumentList.Add(string.Empty);
            psi.ArgumentList.Add(string.Empty);
            psi.ArgumentList.Add(handshake);
            psi.ArgumentList.Add(launchOwned);
            psi.ArgumentList.Add(launchStarted);
            psi.ArgumentList.Add(launchFailed);
            psi.ArgumentList.Add(helperExited);
            psi.ArgumentList.Add(launchCancelled);
            psi.ArgumentList.Add(launchNonce);
            psi.ArgumentList.Add(SidecarVoiceClient.Proto.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--handshake");
            psi.ArgumentList.Add(handshake);
            psi.ArgumentList.Add("--token-file");
            psi.ArgumentList.Add(token);
            using var process = Process.Start(psi);
            Assert.NotNull(process);
            Assert.True(process!.WaitForExit(5000));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("ok", File.ReadAllText(handshake));
            Assert.Equal(
                $"perfect-comms-launch-owned-v1:{launchNonce}",
                File.ReadAllText(launchOwned));
            var started = File.ReadAllText(launchStarted);
            Assert.StartsWith($"perfect-comms-launch-started-v1:{launchNonce}:", started);
            var startedPid = int.Parse(started[(started.LastIndexOf(':') + 1)..]);
            Assert.Equal(
                $"perfect-comms-helper-exited-v1:{launchNonce}:{startedPid}:0",
                File.ReadAllText(helperExited));
            Assert.False(File.Exists(launchFailed));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void HostLaunchScriptFallsBackWhenPrimaryExecProbeFailsOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;

        var parent = NewTemporaryDirectory();
        var privateDirectory = Path.Combine(parent, "perfect-comms-fallback");
        Directory.CreateDirectory(privateDirectory);
        var token = Path.Combine(privateDirectory, "token");
        var primary = Path.Combine(privateDirectory, "primary-helper");
        var fallback = Path.Combine(privateDirectory, "fallback-helper");
        var handshake = Path.Combine(privateDirectory, "handshake.json");
        var selected = Path.Combine(privateDirectory, "selected.txt");
        var control = new WineLaunchControlPaths(
            "FALLBACK0123456789",
            Path.Combine(privateDirectory, ".launch-owned"),
            Path.Combine(privateDirectory, ".launch-started"),
            Path.Combine(privateDirectory, ".launch-failed"),
            Path.Combine(privateDirectory, ".helper-exited"),
            Path.Combine(privateDirectory, ".launch-cancelled"));
        File.WriteAllText(token, "secret");
        File.WriteAllText(
            primary,
            "#!/bin/sh\n" +
            "if [ \"$1\" = \"--protocol-version\" ]; then printf '%s\\n' 9; exit 0; fi\n" +
            $"printf '%s' primary > '{selected}'; exit 91\n");
        File.WriteAllText(
            fallback,
            "#!/bin/sh\n" +
            "if [ \"$1\" = \"--protocol-version\" ]; then printf '%s\\n' 13; exit 0; fi\n" +
            $"printf '%s' fallback > '{selected}'\n" +
            "while [ \"$#\" -gt 0 ]; do\n" +
            "  if [ \"$1\" = \"--handshake\" ]; then shift; printf '%s' ok > \"$1\"; exit 0; fi\n" +
            "  shift\n" +
            "done\n" +
            "exit 2\n");
        try
        {
            var psi = new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(SidecarLauncher.WineHelperLaunchScript);
            psi.ArgumentList.Add("perfect-comms-bootstrap");
            psi.ArgumentList.Add(privateDirectory);
            psi.ArgumentList.Add(token);
            psi.ArgumentList.Add(primary);
            psi.ArgumentList.Add(fallback);
            psi.ArgumentList.Add(string.Empty);
            psi.ArgumentList.Add(string.Empty);
            psi.ArgumentList.Add(string.Empty);
            psi.ArgumentList.Add(handshake);
            psi.ArgumentList.Add(control.OwnershipPath);
            psi.ArgumentList.Add(control.StartedPath);
            psi.ArgumentList.Add(control.FailurePath);
            psi.ArgumentList.Add(control.ExitPath);
            psi.ArgumentList.Add(control.CancellationPath);
            psi.ArgumentList.Add(control.Nonce);
            psi.ArgumentList.Add(SidecarVoiceClient.Proto.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--handshake");
            psi.ArgumentList.Add(handshake);
            psi.ArgumentList.Add("--token-file");
            psi.ArgumentList.Add(token);
            using var process = Process.Start(psi);
            Assert.NotNull(process);
            var completed = process!.WaitForExit(5000);
            if (!completed)
            {
                try { process.Kill(); } catch { }
                try { process.WaitForExit(1000); } catch { }
            }
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(completed, stderr);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("fallback", File.ReadAllText(selected));
            Assert.Equal("ok", File.ReadAllText(handshake));
            Assert.False(File.Exists(control.FailurePath));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void HostLaunchCancellationBoundsSlowPreflightAndCleansDirectoryOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;

        var parent = NewTemporaryDirectory();
        var privateDirectory = Path.Combine(parent, "perfect-comms-slow-preflight");
        Directory.CreateDirectory(privateDirectory);
        var token = Path.Combine(privateDirectory, "token");
        var primary = Path.Combine(privateDirectory, "slow-primary");
        var fallback = Path.Combine(privateDirectory, "fallback");
        var handshake = Path.Combine(privateDirectory, "handshake.json");
        var probeEntered = Path.Combine(parent, "probe-entered");
        var control = new WineLaunchControlPaths(
            new string('A', 64),
            Path.Combine(privateDirectory, ".launch-owned"),
            Path.Combine(privateDirectory, ".launch-started"),
            Path.Combine(privateDirectory, ".launch-failed"),
            Path.Combine(privateDirectory, ".helper-exited"),
            Path.Combine(privateDirectory, ".launch-cancelled"));
        File.WriteAllText(token, "secret");
        File.WriteAllText(
            primary,
            "#!/bin/sh\n" +
            $"if [ \"$1\" = \"--protocol-version\" ]; then printf '%s' entered > '{probeEntered}'; exec /bin/sleep 30; fi\n" +
            "exit 90\n");
        File.WriteAllText(
            fallback,
            "#!/bin/sh\n" +
            "if [ \"$1\" = \"--protocol-version\" ]; then printf '%s\\n' 13; exit 0; fi\n" +
            "exit 0\n");
        try
        {
            var psi = new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(SidecarLauncher.WineHelperLaunchScript);
            psi.ArgumentList.Add("perfect-comms-bootstrap");
            psi.ArgumentList.Add(privateDirectory);
            psi.ArgumentList.Add(token);
            psi.ArgumentList.Add(primary);
            psi.ArgumentList.Add(fallback);
            // The test files are passed as the two staged paths so the supervisor's fixed-file
            // cleanup can remove everything and prove the directory is eventually reclaimed.
            psi.ArgumentList.Add(primary);
            psi.ArgumentList.Add(fallback);
            psi.ArgumentList.Add(string.Empty);
            psi.ArgumentList.Add(handshake);
            psi.ArgumentList.Add(control.OwnershipPath);
            psi.ArgumentList.Add(control.StartedPath);
            psi.ArgumentList.Add(control.FailurePath);
            psi.ArgumentList.Add(control.ExitPath);
            psi.ArgumentList.Add(control.CancellationPath);
            psi.ArgumentList.Add(control.Nonce);
            psi.ArgumentList.Add(SidecarVoiceClient.Proto.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--handshake");
            psi.ArgumentList.Add(handshake);
            using var process = Process.Start(psi);
            Assert.NotNull(process);

            Assert.True(SpinWait.SpinUntil(
                () => SidecarLauncher.IsWineLaunchOwned(control),
                TimeSpan.FromSeconds(2)));
            Assert.True(SpinWait.SpinUntil(
                () => File.Exists(probeEntered),
                TimeSpan.FromSeconds(2)));
            Assert.True(SidecarLauncher.RequestWineLaunchCancellation(control));
            var completed = process!.WaitForExit(6_000);
            if (!completed)
            {
                try { process.Kill(); } catch { }
                try { process.WaitForExit(1000); } catch { }
            }
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(completed, stderr);
            Assert.Equal(125, process.ExitCode);
            Assert.True(SidecarLauncher.TryReadWineLaunchFailure(
                control,
                out var reason,
                out var failureCode));
            Assert.Equal("cancelled", reason);
            Assert.Equal(125, failureCode);
            Assert.False(File.Exists(control.StartedPath));
            Assert.True(SpinWait.SpinUntil(
                () => !Directory.Exists(privateDirectory),
                TimeSpan.FromSeconds(12)),
                "Wine supervisor did not reclaim the cancelled private directory");
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    [Fact]
    public void DeviceEnumerationFileRequiresStructuredInputAndOutputLists()
    {
        var directory = NewTemporaryDirectory();
        var path = Path.Combine(directory, "devices.json");
        try
        {
            File.WriteAllText(path,
                "{\"op\":\"devices\",\"devices\":[{\"id\":\"mic-1\",\"name\":\"Mic\"}]," +
                "\"outputDevices\":[{\"id\":\"spk-1\",\"name\":\"Speaker\"}]}");
            Assert.True(SidecarLauncher.TryReadDevicesFile(path, out var input, out var output));
            Assert.Equal("mic-1", Assert.Single(input).Id);
            Assert.Equal("spk-1", Assert.Single(output).Id);

            File.WriteAllText(path,
                "{\"op\":\"devices\",\"devices\":[],\"outputDevices\":[]}");
            Assert.True(SidecarLauncher.TryReadDevicesFile(path, out input, out output));
            Assert.Empty(input);
            Assert.Empty(output);

            File.WriteAllText(path,
                "{\"op\":\"devices\",\"devices\":[{\"id\":\"mic-1\",\"name\":\"Mic\"}]," +
                "\"outputDevices\":[{\"name\":\"Missing stable ID\"}]}");
            Assert.False(SidecarLauncher.TryReadDevicesFile(path, out _, out _));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

#if WINDOWS
    [Fact]
    public void FailedEnumerationIsDistinctFromAuthoritativeEmptyEnumeration()
    {
        var directory = NewTemporaryDirectory();
        try
        {
            var failure = SidecarLauncher.EnumerateDevices(
                Path.Combine(directory, "missing-helper.exe"),
                wine: false,
                static path => path);
            var empty = SidecarDeviceEnumerationResult.Success(
                Array.Empty<VoiceDeviceInfo>(), Array.Empty<VoiceDeviceInfo>());

            Assert.False(failure.IsAuthoritative);
            Assert.True(empty.IsAuthoritative);
            Assert.Empty(empty.Input);
            Assert.Empty(empty.Output);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
#endif

    [Fact]
    public void HelperAndDspMapToFixedNamesInsideSameVersionedBundle()
    {
        var assembly = typeof(SidecarLauncherCacheTests).Assembly;
        var version = NativeLibraryCache.BuildContentVersion(assembly, new[] { CacheFixtureResource });
        var baseDirectory = Path.Combine(Path.GetTempPath(), "Perfect Comms cache mapping");
        var expectedDirectory = Path.Combine(
            baseDirectory,
            "cache",
            "PerfectComms",
            "native",
            Triple,
            version);

        var helper = NativeLibraryCache.ResolveExtractionPath(
            baseDirectory,
            Triple,
            "PerfectCommsAudio.exe",
            version);
        var dsp = NativeLibraryCache.ResolveExtractionPath(
            baseDirectory,
            Triple,
            "webrtc-apm.x64.dll",
            version);

        Assert.StartsWith("bundle-v1-", version, StringComparison.Ordinal);
        Assert.Equal(expectedDirectory, NativeLibraryCache.BundleDirectory(baseDirectory, Triple, version));
        Assert.Equal(expectedDirectory, Path.GetDirectoryName(helper));
        Assert.Equal(expectedDirectory, Path.GetDirectoryName(dsp));
        Assert.Equal("PerfectCommsAudio.exe", Path.GetFileName(helper));
        Assert.Equal("webrtc-apm.x64.dll", Path.GetFileName(dsp));
    }

    [Fact]
    public void BundleVersionIsStableRegardlessOfResourceOrder()
    {
        var assembly = typeof(SidecarLauncherCacheTests).Assembly;

        var first = NativeLibraryCache.BuildContentVersion(
            assembly,
            new[] { CacheFixtureResource, "missing.optional.resource" });
        var second = NativeLibraryCache.BuildContentVersion(
            assembly,
            new[] { "missing.optional.resource", CacheFixtureResource, CacheFixtureResource });

        Assert.Equal(first, second);
    }

    #if WINDOWS
    [Fact]
    public void LockedLegacyTargetDoesNotBlockVersionedExtraction()
    {
        var assembly = typeof(SidecarLauncherCacheTests).Assembly;
        var baseDirectory = NewTemporaryDirectory();
        var legacyTarget = NativeLibraryCache.ResolveExtractionPath(
            baseDirectory,
            Triple,
            "PerfectCommsAudio.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyTarget)!);
        File.WriteAllText(legacyTarget, "old locked helper");

        try
        {
            using (var legacyLock = new FileStream(
                       legacyTarget,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.Read))
            {
                var version = NativeLibraryCache.BuildContentVersion(assembly, new[] { CacheFixtureResource });
                var extracted = NativeLibraryCache.Extract(
                    assembly,
                    CacheFixtureResource,
                    "PerfectCommsAudio.exe",
                    Triple,
                    baseDirectory,
                    version);

                Assert.NotEqual(legacyTarget, extracted);
                Assert.Equal(
                    NativeLibraryCache.BundleDirectory(baseDirectory, Triple, version),
                    Path.GetDirectoryName(extracted));
                Assert.True(File.Exists(extracted));
            }
        }
        finally
        {
            Directory.Delete(baseDirectory, true);
        }
    }
    #endif

    [Fact]
    public void ExtractionFailureReportsExactStageAndTargetPath()
    {
        var assembly = typeof(SidecarLauncher).Assembly;
        var baseDirectory = NewTemporaryDirectory();
        const string version = "bundle-v1-missing-resource-test";
        var expectedTarget = NativeLibraryCache.ResolveExtractionPath(
            baseDirectory,
            Triple,
            "PerfectCommsAudio.exe",
            version);

        try
        {
            var error = Assert.Throws<NativeCacheExtractionException>(() =>
                NativeLibraryCache.Extract(
                    assembly,
                    "missing.helper.resource",
                    "PerfectCommsAudio.exe",
                    Triple,
                    baseDirectory,
                    version));

            Assert.Equal("open-resource", error.Stage);
            Assert.Equal(expectedTarget, error.TargetPath);
            Assert.Contains("stage=open-resource", error.Message, StringComparison.Ordinal);
            Assert.Contains(NativeLibraryCache.DiagnosticValue(expectedTarget), error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(baseDirectory, true);
        }
    }

    [Fact]
    public void StalePruningNeverDeletesCurrentBundle()
    {
        var baseDirectory = NewTemporaryDirectory();
        const string currentVersion = "bundle-v1-current-test";
        const string staleVersion = "bundle-v1-stale-test";
        var current = NativeLibraryCache.BundleDirectory(baseDirectory, Triple, currentVersion);
        var stale = NativeLibraryCache.BundleDirectory(baseDirectory, Triple, staleVersion);
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "PerfectCommsAudio.exe"), "stale");

        try
        {
            var deleted = NativeLibraryCache.PruneStaleBundles(baseDirectory, Triple, currentVersion);

            Assert.True(Directory.Exists(current));
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(1, deleted);
                Assert.False(Directory.Exists(stale));
            }
            else
            {
                Assert.Equal(0, deleted);
                Assert.True(Directory.Exists(stale));
            }
        }
        finally
        {
            Directory.Delete(baseDirectory, true);
        }
    }

    [Fact]
    public void StalePruningSkipsBundleWithActiveLease()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var baseDirectory = NewTemporaryDirectory();
        const string currentVersion = "bundle-v1-current-lease-test";
        const string activeVersion = "bundle-v1-active-lease-test";
        var current = NativeLibraryCache.BundleDirectory(baseDirectory, Triple, currentVersion);
        var active = NativeLibraryCache.BundleDirectory(baseDirectory, Triple, activeVersion);
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(active);
        var leasePath = Path.Combine(active, ".in-use");

        try
        {
            using (var activeLease = new FileStream(
                       leasePath,
                       FileMode.OpenOrCreate,
                       FileAccess.ReadWrite,
                       FileShare.ReadWrite))
            {
                var deleted = NativeLibraryCache.PruneStaleBundles(baseDirectory, Triple, currentVersion);

                Assert.Equal(0, deleted);
                Assert.True(Directory.Exists(active));
            }
        }
        finally
        {
            Directory.Delete(baseDirectory, true);
        }
    }

    [Fact]
    public void MacContentAddressedBundleRepairsCorruptionWithoutTimestampHeuristics()
    {
        var baseDirectory = NewTemporaryDirectory();
        const string triple = "aarch64-apple-darwin";
        const string version = "bundle-v1-mac-cache-test";
        var zip = Path.Combine(baseDirectory, "helper.zip");
        CreateMacHelperZip(zip);

        try
        {
            var inner = SidecarLauncher.ExtractMacApp(zip, triple, baseDirectory, version);
            File.WriteAllText(inner, "cached-sentinel");
            File.SetLastWriteTimeUtc(zip, DateTime.UtcNow.AddMinutes(5));

            var reused = SidecarLauncher.ExtractMacApp(zip, triple, baseDirectory, version);

            Assert.Equal(inner, reused);
            Assert.Equal("helper-bytes", File.ReadAllText(reused));
        }
        finally
        {
            Directory.Delete(baseDirectory, true);
        }
    }

    [Fact]
    public async Task ConcurrentMacExtractionPublishesOneCompleteApp()
    {
        var baseDirectory = NewTemporaryDirectory();
        const string triple = "x86_64-apple-darwin";
        const string version = "bundle-v1-mac-concurrency-test";
        var zip = Path.Combine(baseDirectory, "helper.zip");
        CreateMacHelperZip(zip);

        try
        {
            var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(
                () => SidecarLauncher.ExtractMacApp(zip, triple, baseDirectory, version))));

            Assert.Single(paths.Distinct(StringComparer.Ordinal));
            Assert.All(paths, path => Assert.Equal("helper-bytes", File.ReadAllText(path)));
        }
        finally
        {
            Directory.Delete(baseDirectory, true);
        }
    }

    private static void CreateMacHelperZip(string path)
    {
        using var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        var entry = archive.CreateEntry("PerfectCommsAudio.app/Contents/MacOS/PerfectCommsAudio");
        entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var writer = new StreamWriter(entry.Open());
        writer.Write("helper-bytes");
    }

    private static string NewTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "perfect-comms-cache-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
