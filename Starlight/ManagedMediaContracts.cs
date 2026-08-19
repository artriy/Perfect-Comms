namespace PerfectComms.Starlight.Media;

public readonly record struct ManagedIceServer(string Urls, string Username, string Credential);

public readonly record struct ManagedPeerRoute(
    string PeerId,
    float Gain,
    float Pan,
    int Mode,
    bool Muffled);

public readonly record struct ManagedPeerLevel(string PeerId, float Peak);

internal readonly record struct DecodedPeerFrame(
    string PeerId,
    float[] Samples,
    int SampleCount,
    bool MeasurementEligible);
