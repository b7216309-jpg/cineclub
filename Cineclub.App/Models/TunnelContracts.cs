namespace Cineclub.App.Models;

public enum TunnelState
{
    Stopped,
    Starting,
    Ready,
    Error
}

public sealed record TunnelStatus(
    TunnelState State,
    Uri? PublicBaseUri,
    string? ErrorMessage);
