namespace MusicSpin.Core.Output.Wasapi;

/// <summary>
/// What an <c>IMMNotificationClient</c> callback means for a stream open on one endpoint: whether it follows the system
/// default, whether the endpoint it holds disappeared, was disabled or unplugged. Pure decisions, so they are tested on
/// every OS; the COM plumbing that feeds them is in <see cref="WasapiExclusiveStream"/>.
/// </summary>
internal static class EndpointEvents
{
    /// <summary>DEVICE_STATE_ACTIVE: the only state in which an endpoint can render.</summary>
    internal const int StateActive = 0x1;

    private const int FlowRender = 0;
    private const int RoleMultimedia = 1;

    /// <summary>
    /// True when Windows made another endpoint the default render device (for the multimedia role the stream opened
    /// with) under a stream that follows the default. Windows also reports the eConsole and eCommunications roles of
    /// the same change, which are skipped; with no new default at all the stream's own loss says so.
    /// </summary>
    internal static bool DefaultMovedAway(bool followsDefault, string? openedId, int flow, int role, string? newDefaultId)
        => followsDefault
           && flow == FlowRender
           && role == RoleMultimedia
           && newDefaultId is not null
           && !SameEndpoint(newDefaultId, openedId);

    /// <summary>Why the stream's endpoint can no longer render after a state change, or null when the change is not about it or leaves it active.</summary>
    internal static string? LostByState(string? openedId, string? deviceId, int newState)
        => SameEndpoint(deviceId, openedId) && newState != StateActive
            ? "The audio device was disabled or unplugged."
            : null;

    /// <summary>Why the stream's endpoint can no longer render after an endpoint was removed, or null when it was another one.</summary>
    internal static string? LostByRemoval(string? openedId, string? deviceId)
        => SameEndpoint(deviceId, openedId) ? "The audio device was removed." : null;

    private static bool SameEndpoint(string? a, string? b)
        => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
