using System;
using System.Collections.Generic;
using SocketIOClient;

namespace VoiceChatPlugin.VoiceChat;

internal static class BetterCrewLinkSocketOptions
{
    internal const string UserAgentHeader = "User-Agent";

    internal static string UserAgent => $"PerfectComms/{VoiceChatPluginMain.Version}";

    internal static SocketIOOptions Create()
    {
        return new SocketIOOptions
        {
            Reconnection = true,
            ReconnectionAttempts = int.MaxValue,
            EIO = SocketIO.Core.EngineIO.V3,
            ExtraHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [UserAgentHeader] = UserAgent,
            },
        };
    }
}
