using BepInEx;
using BepInEx.Configuration;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class PerfectCommsConfigStoreTests
{
    private static readonly BepInPlugin Metadata = new(
        "com.edgetel.perfectcomms",
        "Perfect Comms",
        "4.1.2");

    [Fact]
    public void GlobalPathLivesUnderUnityPersistentDataDirectory()
    {
        string persistent = Path.Combine("root", "Innersloth", "Among Us");

        string actual = PerfectCommsConfigPath.Resolve(persistent);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(
                persistent,
                PerfectCommsConfigPath.DirectoryName,
                PerfectCommsConfigPath.FileName)),
            actual);
    }

    [Fact]
    public void FirstGlobalLaunchCopiesInstanceConfigWithoutDeletingIt()
    {
        string root = NewTemporaryDirectory();
        PerfectCommsConfigStore? store = null;
        try
        {
            string instancePath = Path.Combine(root, "instance", "com.edgetel.perfectcomms.cfg");
            Directory.CreateDirectory(Path.GetDirectoryName(instancePath)!);
            const string original = """
                ## existing instance config

                [Audio]

                MicVolume = 1.37
                """;
            File.WriteAllText(instancePath, original);
            var instanceConfig = new ConfigFile(instancePath, saveOnInit: false, Metadata);

            store = Open(instanceConfig, Path.Combine(root, "persistent"));
            store.CompleteInitialization();

            string globalPath = PerfectCommsConfigPath.Resolve(Path.Combine(root, "persistent"));
            Assert.Equal(PerfectCommsConfigMigrationResult.MigratedLocal, store.MigrationResult);
            Assert.Equal(original, File.ReadAllText(instancePath));
            Assert.Equal(original, File.ReadAllText(globalPath));
        }
        finally
        {
            store?.Dispose();
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void ExistingGlobalConfigWinsOverDifferentInstanceConfig()
    {
        string root = NewTemporaryDirectory();
        PerfectCommsConfigStore? store = null;
        try
        {
            string instancePath = Path.Combine(root, "instance", "com.edgetel.perfectcomms.cfg");
            string persistent = Path.Combine(root, "persistent");
            string globalPath = PerfectCommsConfigPath.Resolve(persistent);
            Directory.CreateDirectory(Path.GetDirectoryName(instancePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(globalPath)!);
            const string instanceText = "[Audio]\nMicVolume = 0.5\n";
            const string globalText = "[Audio]\nMicVolume = 1.5\n";
            File.WriteAllText(instancePath, instanceText);
            File.WriteAllText(globalPath, globalText);
            var instanceConfig = new ConfigFile(instancePath, saveOnInit: false, Metadata);

            store = Open(instanceConfig, persistent);
            store.CompleteInitialization();

            Assert.Equal(PerfectCommsConfigMigrationResult.ExistingGlobal, store.MigrationResult);
            Assert.Equal(globalText, File.ReadAllText(globalPath));
            Assert.Equal(instanceText, File.ReadAllText(instancePath));
        }
        finally
        {
            store?.Dispose();
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void DirtySettingMergePreservesNewerValueWrittenByAnotherProcess()
    {
        string root = NewTemporaryDirectory();
        PerfectCommsConfigStore? store = null;
        try
        {
            string instancePath = Path.Combine(root, "instance", "com.edgetel.perfectcomms.cfg");
            var instanceConfig = new ConfigFile(instancePath, saveOnInit: false, Metadata);
            string persistent = Path.Combine(root, "persistent");
            store = Open(instanceConfig, persistent);

            ConfigEntry<string> first = store.Config.Bind("Test", "First", "first-default");
            ConfigEntry<string> second = store.Config.Bind("Test", "Second", "second-default");
            store.CompleteInitialization();

            string globalPath = PerfectCommsConfigPath.Resolve(persistent);
            var external = new ConfigFile(globalPath, saveOnInit: false, Metadata);
            external.Bind("Test", "First", "first-default");
            ConfigEntry<string> externalSecond = external.Bind("Test", "Second", "second-default");
            externalSecond.Value = "second-external";

            Assert.Equal("second-default", second.Value);
            first.Value = "first-local";

            Dictionary<PerfectCommsConfigKey, string> values = PerfectCommsConfigStore.ParseValues(
                File.ReadAllText(globalPath));
            Assert.Equal("first-local", values[new PerfectCommsConfigKey("Test", "First")]);
            Assert.Equal("second-external", values[new PerfectCommsConfigKey("Test", "Second")]);
        }
        finally
        {
            store?.Dispose();
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void ExplicitBatchPersistsAllChangedSettingsTogether()
    {
        string root = NewTemporaryDirectory();
        PerfectCommsConfigStore? store = null;
        try
        {
            string instancePath = Path.Combine(root, "instance", "com.edgetel.perfectcomms.cfg");
            var instanceConfig = new ConfigFile(instancePath, saveOnInit: false, Metadata);
            string persistent = Path.Combine(root, "persistent");
            store = Open(instanceConfig, persistent);
            ConfigEntry<int> first = store.Config.Bind("Batch", "First", 1);
            ConfigEntry<int> second = store.Config.Bind("Batch", "Second", 2);
            store.CompleteInitialization();
            string globalPath = PerfectCommsConfigPath.Resolve(persistent);

            using (PerfectCommsConfigStore.BeginBatch(store.Config))
            {
                first.Value = 10;
                second.Value = 20;

                Dictionary<PerfectCommsConfigKey, string> before = PerfectCommsConfigStore.ParseValues(
                    File.ReadAllText(globalPath));
                Assert.Equal("1", before[new PerfectCommsConfigKey("Batch", "First")]);
                Assert.Equal("2", before[new PerfectCommsConfigKey("Batch", "Second")]);

                PerfectCommsConfigStore.Save(store.Config);
            }

            Dictionary<PerfectCommsConfigKey, string> after = PerfectCommsConfigStore.ParseValues(
                File.ReadAllText(globalPath));
            Assert.Equal("10", after[new PerfectCommsConfigKey("Batch", "First")]);
            Assert.Equal("20", after[new PerfectCommsConfigKey("Batch", "Second")]);
        }
        finally
        {
            store?.Dispose();
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void ParserKeepsEqualsCharactersInsideValues()
    {
        Dictionary<PerfectCommsConfigKey, string> values = PerfectCommsConfigStore.ParseValues(
            "[Voice Server]\nRegistryUrl = https://example.test/path?a=b&c=d\n");

        Assert.Equal(
            "https://example.test/path?a=b&c=d",
            values[new PerfectCommsConfigKey("Voice Server", "RegistryUrl")]);
    }

    private static PerfectCommsConfigStore Open(ConfigFile instanceConfig, string persistentPath)
        => PerfectCommsConfigStore.Open(
            instanceConfig,
            Metadata,
            persistentPath,
            _ => { },
            message => throw new Xunit.Sdk.XunitException(message));

    private static string NewTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"PerfectComms-GlobalConfig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
