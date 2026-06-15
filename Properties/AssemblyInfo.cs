using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PerfectComms.Tests")]

// Reactor reads this plain .NET attribute to discover a mod's flags WITHOUT us
// referencing Reactor at all. When Reactor is loaded it auto-registers Perfect Comms
// into its modded handshake with RequireOnAllClients, so Reactor itself gates the
// version (its Mod.Equals compares Id AND Version) and kicks mismatched/missing
// clients during the loading screen. When Reactor is absent, VoiceJoinGuard does the
// same job standalone. See VoiceJoinGuard for the absent-Reactor path.
[assembly: AssemblyMetadata("Reactor.ModFlags", "RequireOnAllClients")]
