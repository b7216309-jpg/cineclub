using System.IO;
using System.Security.Cryptography;
using System.Text;
using Cineclub.App.Models;
using Microsoft.AspNetCore.SignalR;

namespace Cineclub.App.Server;

/// <summary>
/// Small, in-memory, thread-safe room state. Cineclub intentionally hosts one
/// private room per process; no database or transcoding pipeline is involved.
/// </summary>
public sealed class RoomCoordinator
{
    public const string SignalRGroupName = "cineclub-room";
    public const int MaximumParticipants = 6;

    private const long MaximumPositionMs = 7L * 24 * 60 * 60 * 1_000;

    private static readonly string[] DefaultColors =
    [
        "#FFB454", "#70D6FF", "#FF70A6", "#A8E6A3",
        "#C9A7EB", "#FFE66D", "#7FDBDA", "#FF9F80"
    ];

    private readonly object _gate = new();
    private readonly Dictionary<string, ParticipantState> _participants = [];
    private readonly Dictionary<string, string> _participantByConnection = [];
    private readonly List<ChatEntry> _recentChat = [];
    private readonly byte[] _accessTokenBytes;
    private readonly byte[] _hostKeyBytes;
    private readonly TimeSpan _reconnectRetention = TimeSpan.FromMinutes(5);

    private long _roomRevision;
    private long _playbackRevision;
    private bool _isPlaying;
    private long _positionMs;
    private DateTimeOffset _anchoredAtUtc = DateTimeOffset.UtcNow;
    private ScheduledPlaybackCommand? _pendingCommand;
    private bool _pendingRequiresAllReady;
    private bool _hasPlaybackStarted;

    public RoomCoordinator(string mediaPath, string? movieTitle = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);

        var fullPath = Path.GetFullPath(mediaPath);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Le film sélectionné est introuvable.", fullPath);
        }

        MediaPath = fullPath;
        MovieTitle = CleanMovieTitle(movieTitle, file.Name);
        MediaSizeBytes = file.Length;
        MediaLastWriteUtc = file.LastWriteTimeUtc;
        MediaContentType = GetMediaContentType(file.Extension);
        RoomId = Convert.ToHexString(RandomNumberGenerator.GetBytes(3));
        AccessToken = CreateOpaqueToken();
        HostKey = CreateOpaqueToken();
        _accessTokenBytes = Encoding.UTF8.GetBytes(AccessToken);
        _hostKeyBytes = Encoding.UTF8.GetBytes(HostKey);
    }

    internal string MediaPath { get; }

    internal SemaphoreSlim EventGate { get; } = new(1, 1);

    public string MovieTitle { get; }

    public long MediaSizeBytes { get; }

    public DateTimeOffset MediaLastWriteUtc { get; }

    public string MediaContentType { get; }

    public string RoomId { get; }

    public string AccessToken { get; }

    /// <summary>
    /// Kept only in the host process. Never put this value in an invitation.
    /// </summary>
    public string HostKey { get; }

    public string MediaEndpoint => $"/media/{AccessToken}";

    public bool IsValidAccessToken(string? candidate) =>
        FixedTimeEquals(_accessTokenBytes, candidate);

    public JoinRoomResult Join(string connectionId, JoinRoomRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(request);

        if (!IsValidAccessToken(request.Token))
        {
            throw new HubException("Invitation invalide ou expirée.");
        }

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            CleanupDisconnected_NoLock(now);
            AdvancePlayback_NoLock(now);

            var displayName = CleanDisplayName(request.DisplayName);
            var isHostClaim = FixedTimeEquals(_hostKeyBytes, request.HostKey);

            if (_participantByConnection.TryGetValue(connectionId, out var existingParticipantId) &&
                _participants.TryGetValue(existingParticipantId, out var existingParticipant))
            {
                if (!string.IsNullOrWhiteSpace(request.ResumeToken) &&
                    !FixedTimeEquals(existingParticipant.ResumeTokenBytes, request.ResumeToken))
                {
                    throw new HubException("Cette connexion a déjà rejoint une autre session.");
                }

                if (isHostClaim && !existingParticipant.IsHost)
                {
                    throw new HubException("Cette connexion a déjà rejoint la salle comme invité.");
                }

                existingParticipant.DisplayName = displayName;
                if (!string.IsNullOrWhiteSpace(request.AvatarColor))
                {
                    existingParticipant.AvatarColor = CleanColor(request.AvatarColor, existingParticipant.Seat);
                }

                existingParticipant.IsConnected = true;
                existingParticipant.DisconnectedAtUtc = null;
                _roomRevision++;
                return new JoinRoomResult(
                    existingParticipant.ParticipantId,
                    existingParticipant.ResumeToken,
                    existingParticipant.IsHost,
                    CreateSnapshot_NoLock(now));
            }

            var participant = FindResumingParticipant_NoLock(request.ResumeToken);

            if (participant is null && isHostClaim)
            {
                participant = _participants.Values.FirstOrDefault(value => value.IsHost);
            }

            if (participant is null)
            {
                if (_participants.Count >= MaximumParticipants)
                {
                    throw new HubException("La salle est complète.");
                }

                var isHost = isHostClaim && _participants.Values.All(value => !value.IsHost);
                participant = new ParticipantState
                {
                    ParticipantId = Guid.NewGuid().ToString("N"),
                    ResumeToken = CreateOpaqueToken(),
                    DisplayName = displayName,
                    AvatarColor = CleanColor(request.AvatarColor, _participants.Count),
                    Seat = PickSeat_NoLock(request.PreferredSeat),
                    IsHost = isHost
                };
                participant.ResumeTokenBytes = Encoding.UTF8.GetBytes(participant.ResumeToken);
                _participants.Add(participant.ParticipantId, participant);
            }
            else
            {
                if (participant.ConnectionId is not null)
                {
                    _participantByConnection.Remove(participant.ConnectionId);
                }

                participant.DisplayName = displayName;
                if (!string.IsNullOrWhiteSpace(request.AvatarColor))
                {
                    participant.AvatarColor = CleanColor(request.AvatarColor, participant.Seat);
                }
            }

            participant.ConnectionId = connectionId;
            participant.IsConnected = true;
            participant.DisconnectedAtUtc = null;
            _participantByConnection[connectionId] = participant.ParticipantId;
            _roomRevision++;

            var snapshot = CreateSnapshot_NoLock(now);
            return new JoinRoomResult(
                participant.ParticipantId,
                participant.ResumeToken,
                participant.IsHost,
                snapshot);
        }
    }

    public RoomSnapshot MarkDisconnected(string connectionId)
    {
        return MarkDisconnected(connectionId, out _);
    }

    internal RoomSnapshot MarkDisconnected(string connectionId, out bool participantDisconnected)
    {
        lock (_gate)
        {
            participantDisconnected = false;
            var now = DateTimeOffset.UtcNow;
            AdvancePlayback_NoLock(now);

            if (_participantByConnection.Remove(connectionId, out var participantId) &&
                _participants.TryGetValue(participantId, out var participant) &&
                string.Equals(participant.ConnectionId, connectionId, StringComparison.Ordinal))
            {
                participant.ConnectionId = null;
                participant.IsConnected = false;
                participant.IsReady = false;
                participant.IsBuffering = false;
                participant.BufferPercent = 0;
                participant.DisconnectedAtUtc = now;
                _roomRevision++;
                participantDisconnected = true;
            }

            CleanupDisconnected_NoLock(now);
            return CreateSnapshot_NoLock(now);
        }
    }

    public RoomSnapshot SetSeat(string connectionId, int seat)
    {
        if (seat is < 0 or >= MaximumParticipants)
        {
            throw new HubException($"Le siège doit être compris entre 0 et {MaximumParticipants - 1}.");
        }

        lock (_gate)
        {
            var participant = RequireParticipant_NoLock(connectionId);
            if (_participants.Values.Any(value =>
                    value.ParticipantId != participant.ParticipantId && value.Seat == seat))
            {
                throw new HubException("Ce siège est déjà occupé.");
            }

            participant.Seat = seat;
            _roomRevision++;
            return CreateSnapshot_NoLock(DateTimeOffset.UtcNow);
        }
    }

    public RoomSnapshot UpdatePlayerStatus(string connectionId, PlayerStatusUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_gate)
        {
            var participant = RequireParticipant_NoLock(connectionId);
            participant.IsReady = update.IsReady;
            participant.IsBuffering = update.IsBuffering;
            participant.BufferPercent = double.IsFinite(update.BufferPercent)
                ? Math.Clamp(update.BufferPercent, 0, 100)
                : 0;
            participant.ReportedPositionMs = NormalizePosition(update.PositionMs);
            participant.ReportedIsPlaying = update.IsPlaying;
            _roomRevision++;
            return CreateSnapshot_NoLock(DateTimeOffset.UtcNow);
        }
    }

    public ChatEntry AddChatMessage(string connectionId, ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = (request.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw new HubException("Le message est vide.");
        }

        if (text.Length > 500)
        {
            text = TruncateWithoutSplittingSurrogate(text, 500);
        }

        lock (_gate)
        {
            var participant = RequireParticipant_NoLock(connectionId);
            var message = new ChatEntry(
                Guid.NewGuid().ToString("N"),
                participant.ParticipantId,
                participant.DisplayName,
                text,
                DateTimeOffset.UtcNow);

            _recentChat.Add(message);
            if (_recentChat.Count > 50)
            {
                _recentChat.RemoveRange(0, _recentChat.Count - 50);
            }

            _roomRevision++;
            return message;
        }
    }

    public ReactionEvent CreateReaction(string connectionId, ReactionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var emoji = (request.Emoji ?? string.Empty).Trim();
        if (emoji.Length is 0 or > 16)
        {
            throw new HubException("Réaction invalide.");
        }

        lock (_gate)
        {
            var participant = RequireParticipant_NoLock(connectionId);
            return new ReactionEvent(
                Guid.NewGuid().ToString("N"),
                participant.ParticipantId,
                participant.DisplayName,
                emoji,
                DateTimeOffset.UtcNow);
        }
    }

    public ScheduledPlaybackCommand SchedulePlayback(
        string connectionId,
        SchedulePlaybackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            RequireHost_NoLock(connectionId);
            var now = DateTimeOffset.UtcNow;
            CleanupDisconnected_NoLock(now);
            AdvancePlayback_NoLock(now);

            var delay = Math.Clamp(request.DelayMs, 150, 5_000);
            var requiresAllReady = request.Kind == PlaybackCommandKind.Play && !_hasPlaybackStarted;
            if (requiresAllReady && !AllConnectedReady_NoLock())
            {
                throw new HubException("Tous les participants ne sont pas encore prêts.");
            }

            return SchedulePlayback_NoLock(
                request.Kind,
                NormalizePosition(request.PositionMs),
                now.AddMilliseconds(delay),
                requiresAllReady);
        }
    }

    public ScheduledPlaybackCommand StartWhenReady(
        string connectionId,
        StartWhenReadyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            RequireHost_NoLock(connectionId);
            var now = DateTimeOffset.UtcNow;
            CleanupDisconnected_NoLock(now);
            AdvancePlayback_NoLock(now);

            if (!AllConnectedReady_NoLock())
            {
                throw new HubException("Tous les participants ne sont pas encore prêts.");
            }

            var countdown = Math.Clamp(request.CountdownMs, 1_000, 10_000);
            return SchedulePlayback_NoLock(
                PlaybackCommandKind.Play,
                NormalizePosition(request.PositionMs),
                now.AddMilliseconds(countdown),
                requiresAllReady: true);
        }
    }

    public RoomSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            CleanupDisconnected_NoLock(now);
            AdvancePlayback_NoLock(now);
            return CreateSnapshot_NoLock(now);
        }
    }

    internal RoomSnapshot GetSnapshotForConnection(string connectionId)
    {
        lock (_gate)
        {
            RequireParticipant_NoLock(connectionId);
            var now = DateTimeOffset.UtcNow;
            CleanupDisconnected_NoLock(now);
            AdvancePlayback_NoLock(now);
            return CreateSnapshot_NoLock(now);
        }
    }

    internal ScheduledPlaybackCommand? CancelReadyCountdownIfNeeded(bool force = false)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            CleanupDisconnected_NoLock(now);
            AdvancePlayback_NoLock(now);
            if (!_pendingRequiresAllReady || (!force && AllConnectedReady_NoLock()))
            {
                return null;
            }

            var positionMs = GetCurrentPosition_NoLock(now);
            return SchedulePlayback_NoLock(
                PlaybackCommandKind.Pause,
                positionMs,
                now,
                requiresAllReady: false);
        }
    }

    internal RoomSnapshot Leave(string connectionId, out bool participantRemoved)
    {
        lock (_gate)
        {
            participantRemoved = false;
            if (_participantByConnection.Remove(connectionId, out var participantId))
            {
                participantRemoved = _participants.Remove(participantId);
                if (participantRemoved)
                {
                    _roomRevision++;
                }
            }

            return CreateSnapshot_NoLock(DateTimeOffset.UtcNow);
        }
    }

    public bool IsHostConnection(string connectionId)
    {
        lock (_gate)
        {
            return _participantByConnection.TryGetValue(connectionId, out var participantId) &&
                   _participants.TryGetValue(participantId, out var participant) &&
                   participant.IsConnected &&
                   participant.IsHost;
        }
    }

    private ScheduledPlaybackCommand SchedulePlayback_NoLock(
        PlaybackCommandKind kind,
        long positionMs,
        DateTimeOffset executeAtUtc,
        bool requiresAllReady = false)
    {
        var resultingIsPlaying = kind switch
        {
            PlaybackCommandKind.Play => true,
            PlaybackCommandKind.Pause => false,
            PlaybackCommandKind.Seek => _isPlaying,
            _ => throw new HubException("Commande de lecture inconnue.")
        };

        _playbackRevision++;
        _roomRevision++;
        _pendingCommand = new ScheduledPlaybackCommand(
            kind,
            positionMs,
            resultingIsPlaying,
            executeAtUtc,
            _playbackRevision);
        _pendingRequiresAllReady = requiresAllReady;
        return _pendingCommand;
    }

    private void AdvancePlayback_NoLock(DateTimeOffset now)
    {
        if (_pendingCommand is not null && _pendingCommand.ExecuteAtServerUtc <= now)
        {
            _isPlaying = _pendingCommand.ResultingIsPlaying;
            _positionMs = _pendingCommand.PositionMs;
            _anchoredAtUtc = _pendingCommand.ExecuteAtServerUtc;
            if (_pendingCommand.Kind == PlaybackCommandKind.Play && _pendingCommand.ResultingIsPlaying)
            {
                _hasPlaybackStarted = true;
            }

            _pendingCommand = null;
            _pendingRequiresAllReady = false;
        }
    }

    private RoomSnapshot CreateSnapshot_NoLock(DateTimeOffset now)
    {
        CleanupDisconnected_NoLock(now);
        AdvancePlayback_NoLock(now);
        return new RoomSnapshot(
            RoomId,
            MovieTitle,
            MediaSizeBytes,
            MediaEndpoint,
            now,
            _roomRevision,
            AllConnectedReady_NoLock(),
            new PlaybackSnapshot(
                _isPlaying,
                _positionMs,
                _anchoredAtUtc,
                _playbackRevision,
                _pendingCommand),
            _participants.Values
                .OrderBy(value => value.Seat)
                .Select(value => value.ToSnapshot())
                .ToArray(),
            _recentChat.ToArray());
    }

    private bool AllConnectedReady_NoLock()
    {
        return _participants.Count > 0 && _participants.Values.All(value =>
            value.IsConnected && value.IsReady && !value.IsBuffering);
    }

    private ParticipantState RequireParticipant_NoLock(string connectionId)
    {
        if (!_participantByConnection.TryGetValue(connectionId, out var participantId) ||
            !_participants.TryGetValue(participantId, out var participant) ||
            !participant.IsConnected)
        {
            throw new HubException("Rejoignez d'abord la salle.");
        }

        return participant;
    }

    private ParticipantState RequireHost_NoLock(string connectionId)
    {
        var participant = RequireParticipant_NoLock(connectionId);
        if (!participant.IsHost)
        {
            throw new HubException("Seul l'hôte peut contrôler le film.");
        }

        return participant;
    }

    private ParticipantState? FindResumingParticipant_NoLock(string? resumeToken)
    {
        if (string.IsNullOrWhiteSpace(resumeToken) || resumeToken.Length != 32)
        {
            return null;
        }

        var candidate = Encoding.UTF8.GetBytes(resumeToken);
        return _participants.Values.FirstOrDefault(value =>
            value.ResumeTokenBytes.Length == candidate.Length &&
            CryptographicOperations.FixedTimeEquals(value.ResumeTokenBytes, candidate));
    }

    private int PickSeat_NoLock(int? preferredSeat)
    {
        var occupied = _participants.Values.Select(value => value.Seat).ToHashSet();
        if (preferredSeat is >= 0 and < MaximumParticipants && !occupied.Contains(preferredSeat.Value))
        {
            return preferredSeat.Value;
        }

        for (var seat = 0; seat < MaximumParticipants; seat++)
        {
            if (!occupied.Contains(seat))
            {
                return seat;
            }
        }

        throw new HubException("Aucun siège n'est disponible.");
    }

    private void CleanupDisconnected_NoLock(DateTimeOffset now)
    {
        var expired = _participants.Values
            .Where(value =>
                !value.IsConnected &&
                !value.IsHost &&
                value.DisconnectedAtUtc is not null &&
                now - value.DisconnectedAtUtc.Value >= _reconnectRetention)
            .Select(value => value.ParticipantId)
            .ToArray();

        foreach (var participantId in expired)
        {
            _participants.Remove(participantId);
            _roomRevision++;
        }
    }

    private static bool FixedTimeEquals(byte[] expected, string? candidate)
    {
        if (candidate is null)
        {
            return false;
        }

        if (candidate.Length != expected.Length)
        {
            return false;
        }

        var candidateBytes = Encoding.UTF8.GetBytes(candidate);
        return expected.Length == candidateBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expected, candidateBytes);
    }

    private static string CreateOpaqueToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string CleanDisplayName(string? value)
    {
        var result = string.IsNullOrWhiteSpace(value) ? "Invité" : value.Trim();
        result = new string(result.Where(character => !char.IsControl(character)).ToArray());
        if (result.Length == 0)
        {
            result = "Invité";
        }

        return TruncateWithoutSplittingSurrogate(result, 24);
    }

    private static string CleanMovieTitle(string? requestedTitle, string fileName)
    {
        var title = string.IsNullOrWhiteSpace(requestedTitle)
            ? Path.GetFileNameWithoutExtension(fileName)
            : requestedTitle.Trim();
        return TruncateWithoutSplittingSurrogate(title, 120);
    }

    private static string CleanColor(string? value, int fallbackIndex)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            var candidate = value.Trim();
            if (candidate.Length == 7 && candidate[0] == '#' &&
                candidate.Skip(1).All(Uri.IsHexDigit))
            {
                return candidate.ToUpperInvariant();
            }
        }

        return DefaultColors[Math.Abs(fallbackIndex) % DefaultColors.Length];
    }

    private long GetCurrentPosition_NoLock(DateTimeOffset now)
    {
        if (!_isPlaying || now <= _anchoredAtUtc)
        {
            return _positionMs;
        }

        var elapsedMs = (long)Math.Min(
            MaximumPositionMs,
            (now - _anchoredAtUtc).TotalMilliseconds);
        return Math.Min(MaximumPositionMs, _positionMs + elapsedMs);
    }

    private static long NormalizePosition(long positionMs) =>
        Math.Clamp(positionMs, 0, MaximumPositionMs);

    private static string TruncateWithoutSplittingSurrogate(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
        {
            return value;
        }

        var length = maximumLength;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length];
    }

    private static string GetMediaContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".mkv" => "video/x-matroska",
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".avi" => "video/x-msvideo",
        ".mov" => "video/quicktime",
        ".ts" or ".m2ts" => "video/mp2t",
        _ => "application/octet-stream"
    };

    private sealed class ParticipantState
    {
        public required string ParticipantId { get; init; }

        public required string ResumeToken { get; init; }

        public byte[] ResumeTokenBytes { get; set; } = [];

        public required string DisplayName { get; set; }

        public required string AvatarColor { get; set; }

        public int Seat { get; set; }

        public bool IsHost { get; init; }

        public string? ConnectionId { get; set; }

        public bool IsConnected { get; set; }

        public bool IsReady { get; set; }

        public bool IsBuffering { get; set; }

        public double BufferPercent { get; set; }

        public long ReportedPositionMs { get; set; }

        public bool ReportedIsPlaying { get; set; }

        public DateTimeOffset? DisconnectedAtUtc { get; set; }

        public ParticipantSnapshot ToSnapshot() => new(
            ParticipantId,
            DisplayName,
            Seat,
            AvatarColor,
            IsHost,
            IsConnected,
            IsReady,
            IsBuffering,
            BufferPercent,
            ReportedPositionMs,
            ReportedIsPlaying);
    }
}
