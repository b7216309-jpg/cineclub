using System.Collections.ObjectModel;
using System.IO;

namespace Cineclub.App.Desktop;

/// <summary>
/// Stable UI-facing facade. The SignalR/Kestrel implementation only needs to
/// implement <see cref="IRoomTransport"/>; MainWindow never depends on server types.
/// </summary>
public sealed class ClientSession : IDisposable
{
    private IRoomTransport _transport;

    public ClientSession(IRoomTransport? transport = null)
    {
        _transport = transport ?? new DisconnectedRoomTransport();
        Participants = [];
        Messages = [];
        WireTransport(_transport);
    }

    public ObservableCollection<SessionParticipant> Participants { get; }

    public ObservableCollection<SessionChatMessage> Messages { get; }

    public bool IsHost { get; private set; }

    public bool IsConnected => _transport.IsConnected;

    public string DisplayName { get; private set; } = string.Empty;

    public string? InvitationUrl { get; private set; }

    public Uri? MediaSource { get; private set; }

    public SessionSnapshot? LatestSnapshot { get; private set; }

    public event EventHandler<SessionStatus>? StatusChanged;

    public event EventHandler<Uri>? MediaSourceAvailable;

    public event EventHandler<PlaybackCommand>? PlaybackCommandReceived;

    public event EventHandler? ParticipantsChanged;

    public event EventHandler<SessionChatMessage>? ChatReceived;

    public event EventHandler<SessionReaction>? ReactionReceived;

    public event EventHandler<string>? InvitationAvailable;

    /// <summary>
    /// Allows composition root/network code to install the real SignalR adapter
    /// without replacing the desktop shell.
    /// </summary>
    public void UseTransport(IRoomTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        UnwireTransport(_transport);
        _transport.Dispose();
        _transport = transport;
        WireTransport(_transport);
    }

    public async Task HostAsync(string displayName, string moviePath, CancellationToken cancellationToken = default)
    {
        DisplayName = ValidateDisplayName(displayName);
        if (string.IsNullOrWhiteSpace(moviePath) || !File.Exists(moviePath))
        {
            throw new FileNotFoundException("Le film sélectionné est introuvable.", moviePath);
        }

        ResetPresentationState();
        IsHost = true;
        StatusChanged?.Invoke(this, new SessionStatus(SessionConnectionState.Connecting, "Création de la salle…"));
        var result = await _transport.HostAsync(DisplayName, moviePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(InvitationUrl, result.InvitationUrl, StringComparison.Ordinal))
        {
            InvitationUrl = result.InvitationUrl;
            InvitationAvailable?.Invoke(this, result.InvitationUrl);
        }
    }

    public async Task JoinAsync(string displayName, string invitationUrl, CancellationToken cancellationToken = default)
    {
        DisplayName = ValidateDisplayName(displayName);
        if (!Uri.TryCreate(invitationUrl.Trim(), UriKind.Absolute, out var invitation) ||
            (invitation.Scheme != Uri.UriSchemeHttp && invitation.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Colle un lien d’invitation Cineclub HTTP ou HTTPS valide.", nameof(invitationUrl));
        }

        ResetPresentationState();
        IsHost = false;
        StatusChanged?.Invoke(this, new SessionStatus(SessionConnectionState.Connecting, "Connexion à la salle…"));
        await _transport.JoinAsync(DisplayName, invitation, cancellationToken).ConfigureAwait(false);
    }

    public async Task LeaveAsync(CancellationToken cancellationToken = default)
    {
        await _transport.DisconnectAsync(cancellationToken);
        ResetPresentationState();
    }

    public Task SetReadyAsync(bool ready, CancellationToken cancellationToken = default) =>
        _transport.SetReadyAsync(ready, cancellationToken);

    public Task ChooseSeatAsync(int seat, CancellationToken cancellationToken = default) =>
        _transport.SelectSeatAsync(seat, cancellationToken);

    public Task SendChatAsync(string text, CancellationToken cancellationToken = default)
    {
        var cleanText = text.Trim();
        return cleanText.Length == 0
            ? Task.CompletedTask
            : _transport.SendChatAsync(cleanText, cancellationToken);
    }

    public Task SendReactionAsync(string reaction, CancellationToken cancellationToken = default) =>
        _transport.SendReactionAsync(reaction, cancellationToken);

    public Task RequestPlaybackAsync(bool playing, long positionMs, CancellationToken cancellationToken = default) =>
        _transport.RequestPlaybackAsync(playing, Math.Max(0, positionMs), cancellationToken);

    public Task RequestSeekAsync(long positionMs, CancellationToken cancellationToken = default) =>
        _transport.RequestSeekAsync(Math.Max(0, positionMs), cancellationToken);

    public Task ReportPlayerStatusAsync(
        bool ready,
        bool buffering,
        double bufferPercent,
        long positionMs,
        bool isPlaying,
        CancellationToken cancellationToken = default) =>
        _transport.ReportPlayerStatusAsync(
            ready,
            buffering,
            Math.Clamp(bufferPercent, 0, 100),
            Math.Max(0, positionMs),
            isPlaying,
            cancellationToken);

    private static string ValidateDisplayName(string name)
    {
        var cleanName = name.Trim();
        if (cleanName.Length is < 2 or > 24)
        {
            throw new ArgumentException("Le prénom doit contenir entre 2 et 24 caractères.", nameof(name));
        }

        return cleanName;
    }

    private void WireTransport(IRoomTransport transport)
    {
        transport.StatusChanged += OnStatusChanged;
        transport.SnapshotReceived += OnSnapshotReceived;
        transport.MediaSourceReceived += OnMediaSourceReceived;
        transport.PlaybackReceived += OnPlaybackReceived;
        transport.ChatReceived += OnChatReceived;
        transport.ReactionReceived += OnReactionReceived;
        transport.InvitationReceived += OnInvitationReceived;
    }

    private void UnwireTransport(IRoomTransport transport)
    {
        transport.StatusChanged -= OnStatusChanged;
        transport.SnapshotReceived -= OnSnapshotReceived;
        transport.MediaSourceReceived -= OnMediaSourceReceived;
        transport.PlaybackReceived -= OnPlaybackReceived;
        transport.ChatReceived -= OnChatReceived;
        transport.ReactionReceived -= OnReactionReceived;
        transport.InvitationReceived -= OnInvitationReceived;
    }

    private void OnStatusChanged(object? sender, SessionStatus status) => StatusChanged?.Invoke(this, status);

    private void OnSnapshotReceived(object? sender, SessionSnapshot snapshot)
    {
        LatestSnapshot = snapshot;
        Participants.Clear();
        foreach (var participant in snapshot.Participants.OrderBy(participant => participant.Seat))
        {
            Participants.Add(participant);
        }

        ParticipantsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnMediaSourceReceived(object? sender, Uri source)
    {
        MediaSource = source;
        MediaSourceAvailable?.Invoke(this, source);
    }

    private void OnPlaybackReceived(object? sender, PlaybackCommand command) =>
        PlaybackCommandReceived?.Invoke(this, command);

    private void OnChatReceived(object? sender, SessionChatMessage message)
    {
        Messages.Add(message);
        while (Messages.Count > 200)
        {
            Messages.RemoveAt(0);
        }

        ChatReceived?.Invoke(this, message);
    }

    private void OnReactionReceived(object? sender, SessionReaction reaction) =>
        ReactionReceived?.Invoke(this, reaction);

    private void OnInvitationReceived(object? sender, string invitation)
    {
        if (string.Equals(InvitationUrl, invitation, StringComparison.Ordinal))
        {
            return;
        }

        InvitationUrl = invitation;
        InvitationAvailable?.Invoke(this, invitation);
    }

    private void ResetPresentationState()
    {
        IsHost = false;
        InvitationUrl = null;
        MediaSource = null;
        LatestSnapshot = null;
        Participants.Clear();
        Messages.Clear();
        ParticipantsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        UnwireTransport(_transport);
        _transport.Dispose();
    }
}

public interface IRoomTransport : IDisposable
{
    bool IsConnected { get; }

    event EventHandler<SessionStatus>? StatusChanged;

    event EventHandler<SessionSnapshot>? SnapshotReceived;

    event EventHandler<Uri>? MediaSourceReceived;

    event EventHandler<PlaybackCommand>? PlaybackReceived;

    event EventHandler<SessionChatMessage>? ChatReceived;

    event EventHandler<SessionReaction>? ReactionReceived;

    event EventHandler<string>? InvitationReceived;

    Task<HostSessionResult> HostAsync(string displayName, string moviePath, CancellationToken cancellationToken);

    Task JoinAsync(string displayName, Uri invitationUrl, CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);

    Task SetReadyAsync(bool ready, CancellationToken cancellationToken);

    Task SelectSeatAsync(int seat, CancellationToken cancellationToken);

    Task SendChatAsync(string text, CancellationToken cancellationToken);

    Task SendReactionAsync(string reaction, CancellationToken cancellationToken);

    Task RequestPlaybackAsync(bool playing, long positionMs, CancellationToken cancellationToken);

    Task RequestSeekAsync(long positionMs, CancellationToken cancellationToken);

    Task ReportPlayerStatusAsync(
        bool ready,
        bool buffering,
        double bufferPercent,
        long positionMs,
        bool isPlaying,
        CancellationToken cancellationToken);
}

public sealed class DisconnectedRoomTransport : IRoomTransport
{
    private const string Message = "Le transport de salle n’est pas encore configuré.";

    public bool IsConnected => false;

    public event EventHandler<SessionStatus>? StatusChanged;
    public event EventHandler<SessionSnapshot>? SnapshotReceived;
    public event EventHandler<Uri>? MediaSourceReceived;
    public event EventHandler<PlaybackCommand>? PlaybackReceived;
    public event EventHandler<SessionChatMessage>? ChatReceived;
    public event EventHandler<SessionReaction>? ReactionReceived;
    public event EventHandler<string>? InvitationReceived;

    public Task<HostSessionResult> HostAsync(string displayName, string moviePath, CancellationToken cancellationToken) =>
        Task.FromException<HostSessionResult>(new InvalidOperationException(Message));

    public Task JoinAsync(string displayName, Uri invitationUrl, CancellationToken cancellationToken) =>
        Task.FromException(new InvalidOperationException(Message));

    public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task SetReadyAsync(bool ready, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task SelectSeatAsync(int seat, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task SendChatAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task SendReactionAsync(string reaction, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RequestPlaybackAsync(bool playing, long positionMs, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RequestSeekAsync(long positionMs, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ReportPlayerStatusAsync(bool ready, bool buffering, double bufferPercent, long positionMs, bool isPlaying, CancellationToken cancellationToken) => Task.CompletedTask;
    public void Dispose() { }

    // Keep the compiler aware that these are intentional adapter hooks.
    private void PreserveEvents() =>
        _ = (StatusChanged, SnapshotReceived, MediaSourceReceived, PlaybackReceived, ChatReceived, ReactionReceived, InvitationReceived);
}

public sealed record HostSessionResult(string InvitationUrl);

public sealed record SessionParticipant(
    string Id,
    string DisplayName,
    int Seat,
    bool IsReady,
    bool IsHost,
    string AccentHex)
{
    public string Initials => string.Concat(DisplayName
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Take(2)
        .Select(part => char.ToUpperInvariant(part[0])));

    public string ReadyLabel => IsReady ? "Prêt" : "Chargement";

    public string RoleLabel => IsHost ? "Hôte" : "Invité";

    public string SeatLabel => $"Fauteuil {Seat + 1}";
}

public sealed record SessionChatMessage(
    string Id,
    string Author,
    string Text,
    DateTimeOffset SentAt,
    bool IsSystem = false)
{
    public string TimeLabel => SentAt.ToLocalTime().ToString("HH:mm");
}

public sealed record SessionSnapshot(
    IReadOnlyList<SessionParticipant> Participants,
    string MovieTitle = "",
    bool AllReady = false);

public sealed record SessionReaction(
    string Id,
    string Author,
    string Emoji,
    DateTimeOffset SentAt);

public sealed record PlaybackCommand(
    bool IsPlaying,
    long PositionMs,
    bool HardSeek,
    float CorrectionRate = 1f,
    long Revision = 0,
    DateTimeOffset? ExecuteAt = null,
    bool IsCorrection = false);

public sealed record SessionStatus(SessionConnectionState State, string Message);

public enum SessionConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Error
}
