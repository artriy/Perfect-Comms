using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class SoftDependencyTypeResolverTests
{
    [Fact]
    public void ResolvesOnlyTheRequestedNamespaceQualifiedType()
    {
        string fullName = typeof(VoiceGamePhase).FullName!;

        Assert.Same(typeof(VoiceGamePhase), SoftDependencyTypeResolver.ResolveExact(fullName));
        Assert.Same(typeof(VoiceGamePhase), SoftDependencyTypeResolver.ResolveExact(fullName));
        Assert.Null(SoftDependencyTypeResolver.ResolveExact(nameof(VoiceGamePhase)));
    }

    [Fact]
    public void CachedMissIsRetriedAfterOptionalAssemblyLoads()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string fullName = $"PerfectComms.Tests.Dynamic.OptionalType_{suffix}";
        Assert.Null(SoftDependencyTypeResolver.ResolveExact(fullName));

        int beforeGeneration = SoftDependencyTypeResolver.AssemblyGeneration;
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"PerfectComms.DynamicOptional_{suffix}"),
            AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("main");
        Type loadedType = module.DefineType(fullName, TypeAttributes.Public).CreateType()!;

        Assert.True(SoftDependencyTypeResolver.AssemblyGeneration > beforeGeneration);
        Assert.Same(loadedType, SoftDependencyTypeResolver.ResolveExact(fullName));
    }

    [Fact]
    public void ConcurrentExactLookupsShareTheCacheSafely()
    {
        string known = typeof(VoiceGamePhase).FullName!;
        string missing = $"PerfectComms.Tests.Missing_{Guid.NewGuid():N}";

        Parallel.For(0, 2_000, i =>
        {
            if ((i & 1) == 0)
                Assert.Same(typeof(VoiceGamePhase), SoftDependencyTypeResolver.ResolveExact(known));
            else
                Assert.Null(SoftDependencyTypeResolver.ResolveExact(missing));
        });
    }

    [Theory]
    [InlineData(false, false, false, 4, 4, true)]
    [InlineData(true, true, false, 4, 4, true)]
    [InlineData(true, false, true, 4, 4, true)]
    [InlineData(true, false, false, 4, 5, true)]
    [InlineData(true, false, false, 4, 4, false)]
    public void RoleTypeConsumerReprobesForLifecycleOrLateAssemblyChanges(
        bool alreadyResolved,
        bool phaseChanged,
        bool joinedNewLobby,
        int resolvedAssemblyGeneration,
        int currentAssemblyGeneration,
        bool expected)
    {
        Assert.Equal(expected, VoiceRoleMuteState.ShouldRefreshSupportedModTypes(
            alreadyResolved,
            phaseChanged,
            joinedNewLobby,
            resolvedAssemblyGeneration,
            currentAssemblyGeneration));
    }
}
