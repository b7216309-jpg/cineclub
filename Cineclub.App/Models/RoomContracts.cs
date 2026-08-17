namespace Cineclub.App.Models;

public enum PlaybackCommandKind
{
    Play,
    Pause,
    Seek
}

public sealed record JoinRoomRequest(
    string Token,
    string DisplayName,
    string? ResumeToken = null,
    string? HostKey = null,
    int? PreferredSeat = null,
    string? AvatarColor = null);

public sealed record JoinRoomResult(
    string ParticipantId,
    string ResumeToken,
    bool IsHost,
    RoomSnapshot Snapshot);

public sealed record RoomInvitation(Uri ServerBaseUri, string Token);

public sealed record ParticipantSnapshot(
    string ParticipantId,
    string DisplayName,
    int Seat,
    string AvatarColor,
    bool IsHost,
    bool IsConnected,
    bool IsReady,
    bool IsBuffering,
    double BufferPercent,
    long ReportedPositionMs,
    bool ReportedIsPlaying);

public sealed record PlayerStatusUpdate(
    bool IsReady,
    bool IsBuffering,
    double BufferPercent,
    long PositionMs,
    bool IsPlaying);

public sealed record ScheduledPlaybackCommand(
    PlaybackCommandKind Kind,
    long PositionMs,
    bool ResultingIsPlaying,
    DateTimeOffset ExecuteAtServerUtc,
    long Revision);

public sealed record SchedulePlaybackRequest(
    PlaybackCommandKind Kind,
    long PositionMs,
    int DelayMs = 400);

public sealed record StartWhenReadyRequest(
    long PositionMs,
    int CountdownMs = 2_000);

public sealed record PlaybackSnapshot(
    bool IsPlaying,
    long PositionMs,
    DateTimeOffset AnchoredAtServerUtc,
    long Revision,
    ScheduledPlaybackCommand? PendingCommand);

public sealed record ChatEntry(
    string MessageId,
    string ParticipantId,
    string DisplayName,
    string Text,
    DateTimeOffset SentAtServerUtc);

public sealed record ChatRequest(string Text);

public sealed record ReactionEvent(
    string ReactionId,
    string ParticipantId,
    string DisplayName,
    string Emoji,
    DateTimeOffset SentAtServerUtc);

public sealed record ReactionRequest(string Emoji);

public sealed record RoomNotice(
    string Code,
    string Message,
    DateTimeOffset SentAtServerUtc);

public sealed record RoomSnapshot(
    string RoomId,
    string MovieTitle,
    long MediaSizeBytes,
    string MediaEndpoint,
    DateTimeOffset ServerTimeUtc,
    long Revision,
    bool AllConnectedParticipantsReady,
    PlaybackSnapshot Playback,
    IReadOnlyList<ParticipantSnapshot> Participants,
    IReadOnlyList<ChatEntry> RecentChat);

/// <summary>
/// Names the SignalR messages emitted by <c>RoomHub</c>. Native clients may use
/// these exact method names with HubConnection.On(...).
/// </summary>
public interface IRoomClient
{
    Task RoomSnapshot(RoomSnapshot snapshot);

    Task ChatMessage(ChatEntry message);

    Task Reaction(ReactionEvent reaction);

    Task PlaybackCommand(ScheduledPlaybackCommand command);

    Task Notice(RoomNotice notice);
}
