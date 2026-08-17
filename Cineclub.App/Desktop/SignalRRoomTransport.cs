using System.Diagnostics;
using System.IO;
using Cineclub.App.Models;
using Cineclub.App.Server;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cineclub.App.Desktop;

/// <summary>
/// Connects the desktop facade to the embedded Cineclub server. Hosts use the
/// loopback endpoint themselves while guests use the public invitation origin.
/// The movie is always consumed through the server's HTTP Range endpoint.
/// </summary>
public sealed class SignalRRoomTransport : IRoomTransport
{
    private static readonly TimeSpan TunnelStartupTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30)
    ];

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _eventStateGate = new();
    private readonly SynchronizationContext? _eventContext;
    private readonly HashSet<string> _seenChatIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenReactionIds = new(StringComparer.Ordinal);

    private EmbeddedRoomServer? _server;
    private CloudflareTunnelService? _tunnel;
    private HubConnection? _connection;
    private CancellationTokenSource? _sessionLifetime;
    private Uri? _connectionBaseUri;
    private Uri? _lastMediaSource;
    private string? _accessToken;
    private string? _hostKey;
    private string? _resumeToken;
    private string? _displayName;
    private long _serverClockOffsetTicks;
    private long _lastPlaybackRevision = -1;
    private long _lastDriftCorrectionUtcTicks;
    private long _lastPlayerReportUtcTicks;
    private long _lastReportedPositionMs;
    private int _connected;
    private int _disconnectRequested;
    private int _lastReportedIsBuffering;
    private int _lastReportedIsPlaying;
    private int _lastReady;
    private int _firstPlayRequestInFlight;
    private int _hasStartedPlayback;
    private int _invalidMediaEndpointReported;
    private int _invitationPublished;
    private int _publicTunnelReady;
    private int _tunnelFailureReported;
    private double _lastReportedBufferPercent;
    private bool _isHost;
    private bool _disposed;

    public SignalRRoomTransport()
    {
        _eventContext = SynchronizationContext.Current;
    }

    public bool IsConnected => Volatile.Read(ref _connected) == 1;

    public event EventHandler<SessionStatus>? StatusChanged;

    public event EventHandler<SessionSnapshot>? SnapshotReceived;

    public event EventHandler<Uri>? MediaSourceReceived;

    public event EventHandler<PlaybackCommand>? PlaybackReceived;

    public event EventHandler<SessionChatMessage>? ChatReceived;

    public event EventHandler<SessionReaction>? ReactionReceived;

    public event EventHandler<string>? InvitationReceived;

    public async Task<HostSessionResult> HostAsync(
        string displayName,
        string moviePath,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _disconnectRequested, 1);
            await ResetResourcesAsync().ConfigureAwait(false);
            Volatile.Write(ref _disconnectRequested, 0);
            ResetSessionState(displayName, isHost: true);
            var sessionLifetime = new CancellationTokenSource();
            _sessionLifetime = sessionLifetime;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                sessionLifetime.Token);
            RaiseStatus(SessionConnectionState.Connecting, "Démarrage de la salle…");

            _server = new EmbeddedRoomServer(moviePath, Path.GetFileNameWithoutExtension(moviePath));
            await _server.StartAsync(operation.Token).ConfigureAwait(false);

            var localBaseUri = _server.LocalBaseUri
                ?? throw new InvalidOperationException("Le serveur local n'a fourni aucune adresse.");
            _connectionBaseUri = localBaseUri;
            _accessToken = _server.Room.AccessToken;
            _hostKey = _server.Room.HostKey;

            await ConnectAndJoinAsync(localBaseUri, sessionLifetime.Token, operation.Token).ConfigureAwait(false);

            var cloudflaredPath = FindBundledCloudflared();
            if (cloudflaredPath is null)
            {
                throw new FileNotFoundException(
                    "cloudflared est absent de cette version de Cineclub. Réinstalle l'application.");
            }

            _tunnel = new CloudflareTunnelService(cloudflaredPath);
            _tunnel.StatusChanged += OnTunnelStatusChanged;
            Uri? publicBaseUri = null;
            Exception? tunnelError = null;
            for (var attempt = 1; attempt <= 2 && publicBaseUri is null; attempt++)
            {
                try
                {
                    RaiseStatus(
                        SessionConnectionState.Connecting,
                        $"Ouverture du lien pour les amis… ({attempt}/2)");
                    publicBaseUri = await _tunnel
                        .StartAsync(localBaseUri, TunnelStartupTimeout, operation.Token)
                        .ConfigureAwait(false);
                    Volatile.Write(ref _publicTunnelReady, 1);
                    OnTunnelStatusChanged(_tunnel, _tunnel.Status);
                    if (Volatile.Read(ref _publicTunnelReady) == 0)
                    {
                        tunnelError = new InvalidOperationException(
                            _tunnel.Status.ErrorMessage ?? "Le tunnel s'est arrêté pendant son démarrage.");
                        publicBaseUri = null;
                    }
                }
                catch (OperationCanceledException) when (operation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    tunnelError = exception;
                    if (attempt < 2)
                    {
                        await Task.Delay(500, operation.Token).ConfigureAwait(false);
                    }
                }
            }

            if (publicBaseUri is null)
            {
                throw new InvalidOperationException(
                    $"Impossible de créer le lien public après deux tentatives ({CleanError(tunnelError!)}).",
                    tunnelError);
            }

            var invitation = _server.BuildInviteUri(publicBaseUri).AbsoluteUri;
            Volatile.Write(ref _invitationPublished, 1);
            OnTunnelStatusChanged(_tunnel, _tunnel.Status);
            if (!IsPublicTunnelReady(_tunnel))
            {
                throw new InvalidOperationException("Le tunnel public s'est arrêté avant la publication du lien.");
            }

            RaiseStatus(SessionConnectionState.Connected, "Salle prête — partage le lien d'invitation.");
            Raise(() => InvitationReceived?.Invoke(this, invitation));
            if (!IsPublicTunnelReady(_tunnel))
            {
                throw new InvalidOperationException("Le tunnel public s'est arrêté pendant la publication du lien.");
            }

            return new HostSessionResult(invitation);
        }
        catch
        {
            await ResetResourcesAsync().ConfigureAwait(false);
            Volatile.Write(ref _connected, 0);
            RaiseStatus(SessionConnectionState.Error, "Impossible de créer la salle.");
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task JoinAsync(
        string displayName,
        Uri invitationUrl,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var invitation = EmbeddedRoomServer.ParseInvitation(invitationUrl);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _disconnectRequested, 1);
            await ResetResourcesAsync().ConfigureAwait(false);
            Volatile.Write(ref _disconnectRequested, 0);
            ResetSessionState(displayName, isHost: false);
            var sessionLifetime = new CancellationTokenSource();
            _sessionLifetime = sessionLifetime;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                sessionLifetime.Token);
            _connectionBaseUri = invitation.ServerBaseUri;
            _accessToken = invitation.Token;

            RaiseStatus(SessionConnectionState.Connecting, "Connexion à la salle…");
            await ConnectAndJoinAsync(
                    invitation.ServerBaseUri,
                    sessionLifetime.Token,
                    operation.Token)
                .ConfigureAwait(false);
            RaiseStatus(SessionConnectionState.Connected, "Connecté à la salle.");
        }
        catch
        {
            await ResetResourcesAsync().ConfigureAwait(false);
            Volatile.Write(ref _connected, 0);
            RaiseStatus(SessionConnectionState.Error, "Impossible de rejoindre la salle.");
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _disconnectRequested, 1);
            var connection = _connection;
            if (connection is not null && connection.State == HubConnectionState.Connected)
            {
                using var leaveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                leaveTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    await connection.InvokeAsync("Leave", leaveTimeout.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Leaving is advisory. Teardown below must always stop every resource.
                }
            }

            // Once teardown has begun it must not be abandoned halfway through by
            // a caller cancellation: otherwise cloudflared or Kestrel can survive.
            await ResetResourcesAsync().ConfigureAwait(false);
            RaiseStatus(SessionConnectionState.Disconnected, "Déconnecté.");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task SetReadyAsync(bool ready, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _lastReady, ready ? 1 : 0);
        var update = new PlayerStatusUpdate(
            ready,
            Volatile.Read(ref _lastReportedIsBuffering) == 1,
            Volatile.Read(ref _lastReportedBufferPercent),
            Math.Max(0, Interlocked.Read(ref _lastReportedPositionMs)),
            Volatile.Read(ref _lastReportedIsPlaying) == 1);
        return InvokeAsync("UpdatePlayerStatus", update, cancellationToken);
    }

    public Task SelectSeatAsync(int seat, CancellationToken cancellationToken) =>
        InvokeAsync("SetSeat", seat, cancellationToken);

    public Task SendChatAsync(string text, CancellationToken cancellationToken) =>
        InvokeAsync("SendChat", new ChatRequest(text), cancellationToken);

    public Task SendReactionAsync(string reaction, CancellationToken cancellationToken) =>
        InvokeAsync("SendReaction", new ReactionRequest(reaction), cancellationToken);

    public async Task RequestPlaybackAsync(
        bool playing,
        long positionMs,
        CancellationToken cancellationToken)
    {
        if (playing && Volatile.Read(ref _hasStartedPlayback) == 0)
        {
            if (Interlocked.CompareExchange(ref _firstPlayRequestInFlight, 1, 0) != 0)
            {
                throw new InvalidOperationException("Le premier lancement est déjà en préparation.");
            }

            try
            {
                var command = await InvokeResultAsync<ScheduledPlaybackCommand>(
                    "StartWhenReady",
                    new StartWhenReadyRequest(Math.Max(0, positionMs), CountdownMs: 3_000),
                    cancellationToken).ConfigureAwait(false);
                HandlePlayback(command);
                return;
            }
            finally
            {
                Volatile.Write(ref _firstPlayRequestInFlight, 0);
            }
        }

        await InvokeAsync(
                "SchedulePlayback",
                new SchedulePlaybackRequest(
                    playing ? PlaybackCommandKind.Play : PlaybackCommandKind.Pause,
                    Math.Max(0, positionMs)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task RequestSeekAsync(long positionMs, CancellationToken cancellationToken)
    {
        return InvokeAsync(
            "SchedulePlayback",
            new SchedulePlaybackRequest(PlaybackCommandKind.Seek, Math.Max(0, positionMs)),
            cancellationToken);
    }

    public Task ReportPlayerStatusAsync(
        bool ready,
        bool buffering,
        double bufferPercent,
        long positionMs,
        bool isPlaying,
        CancellationToken cancellationToken)
    {
        Volatile.Write(ref _lastReady, ready ? 1 : 0);
        Volatile.Write(ref _lastReportedIsBuffering, buffering ? 1 : 0);
        Volatile.Write(ref _lastReportedBufferPercent, Math.Clamp(bufferPercent, 0, 100));
        Interlocked.Exchange(ref _lastReportedPositionMs, Math.Max(0, positionMs));
        Volatile.Write(ref _lastReportedIsPlaying, isPlaying ? 1 : 0);
        Interlocked.Exchange(ref _lastPlayerReportUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        return InvokeAsync(
            "UpdatePlayerStatus",
            new PlayerStatusUpdate(
                ready,
                buffering,
                Math.Clamp(bufferPercent, 0, 100),
                Math.Max(0, positionMs),
                isPlaying),
            cancellationToken);
    }

    private async Task ConnectAndJoinAsync(
        Uri baseUri,
        CancellationToken sessionToken,
        CancellationToken cancellationToken)
    {
        var hubUri = new Uri(baseUri, "room");
        var connection = CreateConnection(hubUri, sessionToken);
        _connection = connection;

        await connection.StartAsync(cancellationToken).ConfigureAwait(false);
        await EstimateClockOffsetAsync(connection, cancellationToken).ConfigureAwait(false);
        var result = await JoinCurrentIdentityAsync(connection, cancellationToken).ConfigureAwait(false);

        if (_isHost && !result.IsHost)
        {
            throw new InvalidOperationException("Le serveur n'a pas reconnu l'identité de l'hôte.");
        }

        _resumeToken = result.ResumeToken;
        Volatile.Write(ref _connected, 1);
        HandleSnapshot(result.Snapshot, forcePlayback: true);
    }

    private HubConnection CreateConnection(Uri hubUri, CancellationToken sessionToken)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUri, options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
            })
            .WithAutomaticReconnect(new RetryForeverPolicy(ReconnectDelays))
            .Build();

        connection.ServerTimeout = TimeSpan.FromSeconds(30);
        connection.KeepAliveInterval = TimeSpan.FromSeconds(10);

        connection.On<RoomSnapshot>("RoomSnapshot", snapshot =>
        {
            if (!sessionToken.IsCancellationRequested && ReferenceEquals(_connection, connection))
            {
                HandleSnapshot(snapshot);
            }
        });
        connection.On<ChatEntry>("ChatMessage", entry =>
        {
            if (!sessionToken.IsCancellationRequested && ReferenceEquals(_connection, connection))
            {
                HandleChat(entry);
            }
        });
        connection.On<ReactionEvent>("Reaction", reaction =>
        {
            if (!sessionToken.IsCancellationRequested && ReferenceEquals(_connection, connection))
            {
                HandleReaction(reaction);
            }
        });
        connection.On<ScheduledPlaybackCommand>("PlaybackCommand", command =>
        {
            if (!sessionToken.IsCancellationRequested && ReferenceEquals(_connection, connection))
            {
                HandlePlayback(command);
            }
        });
        connection.On<RoomNotice>("Notice", notice =>
        {
            if (!sessionToken.IsCancellationRequested && ReferenceEquals(_connection, connection))
            {
                HandleNotice(notice);
            }
        });

        connection.Reconnecting += exception =>
        {
            if (!sessionToken.IsCancellationRequested &&
                Volatile.Read(ref _disconnectRequested) == 0 &&
                ReferenceEquals(_connection, connection))
            {
                Volatile.Write(ref _connected, 0);
                Interlocked.Exchange(ref _lastPlayerReportUtcTicks, 0);
                RaiseStatus(
                    SessionConnectionState.Reconnecting,
                    exception is null ? "Reconnexion…" : $"Connexion perdue — reconnexion ({CleanError(exception)})…");
            }

            return Task.CompletedTask;
        };

        connection.Reconnected += _ =>
        {
            if (sessionToken.IsCancellationRequested ||
                Volatile.Read(ref _disconnectRequested) == 1 ||
                !ReferenceEquals(_connection, connection))
            {
                return Task.CompletedTask;
            }

            return ResumeAfterReconnectAsync(connection, sessionToken);
        };

        connection.Closed += exception =>
        {
            Volatile.Write(ref _connected, 0);
            if (!sessionToken.IsCancellationRequested &&
                Volatile.Read(ref _disconnectRequested) == 0 &&
                ReferenceEquals(_connection, connection))
            {
                RaiseStatus(
                    exception is null ? SessionConnectionState.Disconnected : SessionConnectionState.Error,
                    exception is null ? "Connexion fermée." : $"Connexion interrompue ({CleanError(exception)})." );
            }

            return Task.CompletedTask;
        };

        return connection;
    }

    private async Task ResumeAfterReconnectAsync(
        HubConnection connection,
        CancellationToken sessionToken)
    {
        for (var attempt = 0; !sessionToken.IsCancellationRequested; attempt++)
        {
            if (!ReferenceEquals(_connection, connection) ||
                connection.State != HubConnectionState.Connected)
            {
                return;
            }

            try
            {
                await EstimateClockOffsetAsync(connection, sessionToken).ConfigureAwait(false);
                var result = await JoinCurrentIdentityAsync(connection, sessionToken).ConfigureAwait(false);
                if (!ReferenceEquals(_connection, connection) || sessionToken.IsCancellationRequested)
                {
                    return;
                }

                _resumeToken = result.ResumeToken;
                Volatile.Write(ref _connected, 1);
                HandleSnapshot(result.Snapshot, forcePlayback: true);
                RaiseStatus(SessionConnectionState.Connected, "Reconnecté à la salle.");
                return;
            }
            catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _connected, 0);
                RaiseStatus(
                    SessionConnectionState.Reconnecting,
                    $"Connexion rétablie, reprise de la salle… ({CleanError(exception)})");
            }

            var delay = ReconnectDelays[Math.Min(attempt + 1, ReconnectDelays.Length - 1)];
            try
            {
                await Task.Delay(delay, sessionToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private Task<JoinRoomResult> JoinCurrentIdentityAsync(
        HubConnection connection,
        CancellationToken cancellationToken)
    {
        var request = new JoinRoomRequest(
            _accessToken ?? throw new InvalidOperationException("Le jeton de salle est absent."),
            _displayName ?? throw new InvalidOperationException("Le nom du participant est absent."),
            _resumeToken,
            _isHost ? _hostKey : null);
        return connection.InvokeAsync<JoinRoomResult>("Join", request, cancellationToken);
    }

    private async Task EstimateClockOffsetAsync(
        HubConnection connection,
        CancellationToken cancellationToken)
    {
        var samples = new List<ClockSample>(capacity: 5);
        for (var index = 0; index < 5; index++)
        {
            var sentAt = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var serverTime = await connection
                .InvokeAsync<DateTimeOffset>("Ping", sentAt, cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();
            var receivedAt = DateTimeOffset.UtcNow;
            var midpointTicks = sentAt.UtcTicks + ((receivedAt.UtcTicks - sentAt.UtcTicks) / 2);
            samples.Add(new ClockSample(stopwatch.ElapsedTicks, serverTime.UtcTicks - midpointTicks));
        }

        // The lowest-latency sample is the least distorted by asymmetric queueing.
        var best = samples.MinBy(sample => sample.RoundTripStopwatchTicks);
        Interlocked.Exchange(ref _serverClockOffsetTicks, best.ServerMinusLocalTicks);
    }

    private void HandleSnapshot(RoomSnapshot snapshot, bool forcePlayback = false)
    {
        var participants = snapshot.Participants
            .Where(participant => participant.IsConnected)
            .OrderBy(participant => participant.Seat)
            .Select(participant => new SessionParticipant(
                participant.ParticipantId,
                participant.DisplayName,
                participant.Seat,
                participant.IsReady && !participant.IsBuffering,
                participant.IsHost,
                participant.AvatarColor))
            .ToArray();
        Raise(() => SnapshotReceived?.Invoke(
            this,
            new SessionSnapshot(participants, snapshot.MovieTitle, snapshot.AllConnectedParticipantsReady)));

        var baseUri = _connectionBaseUri;
        if (baseUri is not null && TryBuildSameOriginMediaUri(baseUri, snapshot.MediaEndpoint, out var mediaSource))
        {
            if (!Equals(mediaSource, _lastMediaSource))
            {
                _lastMediaSource = mediaSource;
                Raise(() => MediaSourceReceived?.Invoke(this, mediaSource));
            }
        }
        else if (Interlocked.Exchange(ref _invalidMediaEndpointReported, 1) == 0)
        {
            RaiseStatus(SessionConnectionState.Error, "Le serveur a fourni une adresse média invalide.");
        }

        foreach (var message in snapshot.RecentChat.OrderBy(message => message.SentAtServerUtc))
        {
            HandleChat(message);
        }

        if (snapshot.Playback.PendingCommand is not null)
        {
            HandlePlayback(snapshot.Playback.PendingCommand, forcePlayback);
            return;
        }

        if (snapshot.Playback.IsPlaying)
        {
            Volatile.Write(ref _hasStartedPlayback, 1);
        }

        var revision = snapshot.Playback.Revision;
        if (!forcePlayback && !TryAcceptPlaybackRevision(revision))
        {
            TryEmitDriftCorrection(snapshot);
            return;
        }

        if (forcePlayback)
        {
            AcceptPlaybackRevision(revision);
        }

        var positionMs = snapshot.Playback.PositionMs;
        if (snapshot.Playback.IsPlaying)
        {
            var estimatedServerNowTicks = DateTimeOffset.UtcNow.UtcTicks +
                                          Interlocked.Read(ref _serverClockOffsetTicks);
            var elapsedTicks = Math.Max(
                0,
                estimatedServerNowTicks - snapshot.Playback.AnchoredAtServerUtc.UtcTicks);
            if (elapsedTicks > 0)
            {
                positionMs += elapsedTicks / TimeSpan.TicksPerMillisecond;
            }
        }

        Volatile.Write(ref _lastReportedIsPlaying, snapshot.Playback.IsPlaying ? 1 : 0);
        Interlocked.Exchange(ref _lastReportedPositionMs, Math.Max(0, positionMs));
        RaisePlayback(new PlaybackCommand(
            snapshot.Playback.IsPlaying,
            Math.Max(0, positionMs),
            HardSeek: true,
            CorrectionRate: 1f,
            revision,
            DateTimeOffset.UtcNow,
            IsCorrection: forcePlayback));
    }

    private void HandlePlayback(ScheduledPlaybackCommand command) =>
        HandlePlayback(command, force: false);

    private void HandlePlayback(ScheduledPlaybackCommand command, bool force)
    {
        if (command.Kind == PlaybackCommandKind.Play)
        {
            Volatile.Write(ref _hasStartedPlayback, 1);
        }

        if (!force && !TryAcceptPlaybackRevision(command.Revision))
        {
            return;
        }

        if (force)
        {
            AcceptPlaybackRevision(command.Revision);
        }

        var executeAtLocal = ToLocalDeadline(command.ExecuteAtServerUtc);
        Volatile.Write(ref _lastReportedIsPlaying, command.ResultingIsPlaying ? 1 : 0);
        Interlocked.Exchange(ref _lastReportedPositionMs, Math.Max(0, command.PositionMs));
        Interlocked.Exchange(ref _lastPlayerReportUtcTicks, executeAtLocal.UtcTicks);
        RaisePlayback(new PlaybackCommand(
            command.ResultingIsPlaying,
            Math.Max(0, command.PositionMs),
            HardSeek: true,
            CorrectionRate: 1f,
            command.Revision,
            executeAtLocal));
    }

    private void TryEmitDriftCorrection(RoomSnapshot snapshot)
    {
        if (!snapshot.Playback.IsPlaying ||
            Volatile.Read(ref _lastReady) == 0 ||
            Volatile.Read(ref _lastReportedIsBuffering) == 1 ||
            Volatile.Read(ref _lastReportedIsPlaying) == 0)
        {
            return;
        }

        var reportTicks = Interlocked.Read(ref _lastPlayerReportUtcTicks);
        if (reportTicks == 0)
        {
            return;
        }

        var localNow = DateTimeOffset.UtcNow;
        var nowTicks = localNow.UtcTicks;
        while (true)
        {
            var previousTicks = Interlocked.Read(ref _lastDriftCorrectionUtcTicks);
            if (nowTicks - previousTicks < TimeSpan.TicksPerSecond)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastDriftCorrectionUtcTicks, nowTicks, previousTicks) == previousTicks)
            {
                break;
            }
        }

        var estimatedServerNowTicks = nowTicks + Interlocked.Read(ref _serverClockOffsetTicks);
        var authoritativeElapsedTicks = Math.Max(
            0,
            estimatedServerNowTicks - snapshot.Playback.AnchoredAtServerUtc.UtcTicks);
        var authoritativePositionMs = snapshot.Playback.PositionMs +
                                      (authoritativeElapsedTicks / TimeSpan.TicksPerMillisecond);

        var localElapsedTicks = Math.Max(0, nowTicks - reportTicks);
        var estimatedLocalPositionMs = Interlocked.Read(ref _lastReportedPositionMs) +
                                       (localElapsedTicks / TimeSpan.TicksPerMillisecond);
        var driftMs = authoritativePositionMs - estimatedLocalPositionMs;
        var absoluteDriftMs = Math.Abs(driftMs);

        var hardSeek = absoluteDriftMs > 750;
        var correctionRate = absoluteDriftMs switch
        {
            < 150 => 1f,
            <= 750 when driftMs > 0 => 1.02f,
            <= 750 => 0.98f,
            _ => 1f
        };

        RaisePlayback(new PlaybackCommand(
            IsPlaying: true,
            PositionMs: Math.Max(0, authoritativePositionMs),
            HardSeek: hardSeek,
            CorrectionRate: correctionRate,
            Revision: snapshot.Playback.Revision,
            ExecuteAt: localNow,
            IsCorrection: true));
    }

    private void HandleChat(ChatEntry entry)
    {
        lock (_eventStateGate)
        {
            if (!_seenChatIds.Add(entry.MessageId))
            {
                return;
            }
        }

        var message = new SessionChatMessage(
            entry.MessageId,
            entry.DisplayName,
            entry.Text,
            ToLocalTimestamp(entry.SentAtServerUtc));
        Raise(() => ChatReceived?.Invoke(this, message));
    }

    private void HandleReaction(ReactionEvent reaction)
    {
        lock (_eventStateGate)
        {
            if (!_seenReactionIds.Add(reaction.ReactionId))
            {
                return;
            }
        }

        var message = new SessionReaction(
            reaction.ReactionId,
            reaction.DisplayName,
            reaction.Emoji,
            ToLocalTimestamp(reaction.SentAtServerUtc));
        Raise(() => ReactionReceived?.Invoke(this, message));
    }

    private void HandleNotice(RoomNotice notice)
    {
        var message = new SessionChatMessage(
            $"notice:{notice.Code}:{notice.SentAtServerUtc.UtcTicks}",
            "Cineclub",
            notice.Message,
            ToLocalTimestamp(notice.SentAtServerUtc),
            IsSystem: true);
        Raise(() => ChatReceived?.Invoke(this, message));
    }

    private Task InvokeAsync(string methodName, object argument, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connection = _connection;
        if (connection is null || connection.State != HubConnectionState.Connected || !IsConnected)
        {
            throw new InvalidOperationException("Cineclub n'est pas connecté à une salle.");
        }

        return connection.InvokeAsync(methodName, argument, cancellationToken);
    }

    private Task<TResult> InvokeResultAsync<TResult>(
        string methodName,
        object argument,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connection = _connection;
        if (connection is null || connection.State != HubConnectionState.Connected || !IsConnected)
        {
            throw new InvalidOperationException("Cineclub n'est pas connecté à une salle.");
        }

        return connection.InvokeAsync<TResult>(methodName, argument, cancellationToken);
    }

    private DateTimeOffset ToLocalDeadline(DateTimeOffset serverTimestamp)
    {
        var ticks = serverTimestamp.UtcTicks - Interlocked.Read(ref _serverClockOffsetTicks);
        return new DateTimeOffset(Math.Clamp(ticks, DateTimeOffset.MinValue.UtcTicks, DateTimeOffset.MaxValue.UtcTicks), TimeSpan.Zero);
    }

    private DateTimeOffset ToLocalTimestamp(DateTimeOffset serverTimestamp) =>
        ToLocalDeadline(serverTimestamp);

    private bool TryAcceptPlaybackRevision(long revision)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _lastPlaybackRevision);
            if (revision <= current)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _lastPlaybackRevision, revision, current) == current)
            {
                return true;
            }
        }
    }

    private void AcceptPlaybackRevision(long revision)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _lastPlaybackRevision);
            if (revision <= current ||
                Interlocked.CompareExchange(ref _lastPlaybackRevision, revision, current) == current)
            {
                return;
            }
        }
    }

    private void RaisePlayback(PlaybackCommand command) =>
        Raise(() => PlaybackReceived?.Invoke(this, command));

    private void RaiseStatus(SessionConnectionState state, string message) =>
        Raise(() => StatusChanged?.Invoke(this, new SessionStatus(state, message)));

    private void Raise(Action callback)
    {
        var context = _eventContext;
        if (context is null || ReferenceEquals(SynchronizationContext.Current, context))
        {
            callback();
            return;
        }

        context.Post(static state => ((Action)state!).Invoke(), callback);
    }

    private void ResetSessionState(string displayName, bool isHost)
    {
        _displayName = displayName;
        _isHost = isHost;
        _accessToken = null;
        _hostKey = null;
        _resumeToken = null;
        _connectionBaseUri = null;
        _lastMediaSource = null;
        Interlocked.Exchange(ref _serverClockOffsetTicks, 0);
        Interlocked.Exchange(ref _lastPlaybackRevision, -1);
        Interlocked.Exchange(ref _lastDriftCorrectionUtcTicks, 0);
        Interlocked.Exchange(ref _lastPlayerReportUtcTicks, 0);
        Interlocked.Exchange(ref _lastReportedPositionMs, 0);
        Volatile.Write(ref _lastReportedIsPlaying, 0);
        Volatile.Write(ref _lastReportedIsBuffering, 0);
        Volatile.Write(ref _lastReportedBufferPercent, 0);
        Volatile.Write(ref _firstPlayRequestInFlight, 0);
        Volatile.Write(ref _hasStartedPlayback, 0);
        Volatile.Write(ref _invalidMediaEndpointReported, 0);
        Volatile.Write(ref _invitationPublished, 0);
        Volatile.Write(ref _publicTunnelReady, 0);
        Volatile.Write(ref _tunnelFailureReported, 0);
        Volatile.Write(ref _lastReady, 0);
        Volatile.Write(ref _connected, 0);
        lock (_eventStateGate)
        {
            _seenChatIds.Clear();
            _seenReactionIds.Clear();
        }
    }

    private async Task ResetResourcesAsync()
    {
        Volatile.Write(ref _connected, 0);
        var sessionLifetime = Interlocked.Exchange(ref _sessionLifetime, null);
        if (sessionLifetime is not null)
        {
            try
            {
                sessionLifetime.Cancel();
            }
            catch
            {
                // A cancellation callback cannot be allowed to skip network teardown.
            }
        }

        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null)
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                await connection.StopAsync(stopTimeout.Token).ConfigureAwait(false);
            }
            catch
            {
                // Cleanup continues so a stale connection cannot keep the room alive.
            }

            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Continue with the tunnel and embedded server cleanup.
            }
        }

        var tunnel = Interlocked.Exchange(ref _tunnel, null);
        if (tunnel is not null)
        {
            Volatile.Write(ref _publicTunnelReady, 0);
            tunnel.StatusChanged -= OnTunnelStatusChanged;
            try
            {
                await tunnel.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Kestrel must still be stopped if cloudflared already exited badly.
            }
        }

        var server = Interlocked.Exchange(ref _server, null);
        if (server is not null)
        {
            try
            {
                using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await server.StopAsync(stopTimeout.Token).ConfigureAwait(false);
            }
            catch
            {
                // The process is shutting down or the server is already disposed.
            }

            try
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // StopAsync above already performed the important shutdown attempt.
            }
        }

        try
        {
            sessionLifetime?.Dispose();
        }
        catch
        {
            // Every owned network/process resource has already been stopped above.
        }
    }

    private void OnTunnelStatusChanged(object? sender, TunnelStatus status)
    {
        if (sender is not CloudflareTunnelService tunnel ||
            !ReferenceEquals(_tunnel, tunnel) ||
            status.State != TunnelState.Error ||
            Volatile.Read(ref _disconnectRequested) == 1)
        {
            return;
        }

        Volatile.Write(ref _publicTunnelReady, 0);
        if (Volatile.Read(ref _invitationPublished) == 0 ||
            Interlocked.Exchange(ref _tunnelFailureReported, 1) == 1)
        {
            return;
        }

        RaiseStatus(
            SessionConnectionState.Error,
            $"Le lien public est interrompu ({status.ErrorMessage ?? "tunnel arrêté"}). Recrée la salle.");
    }

    private bool IsPublicTunnelReady(CloudflareTunnelService tunnel) =>
        Volatile.Read(ref _publicTunnelReady) == 1 &&
        tunnel.Status.State == TunnelState.Ready;

    private static bool TryBuildSameOriginMediaUri(
        Uri baseUri,
        string mediaEndpoint,
        out Uri mediaUri)
    {
        mediaUri = null!;
        if (string.IsNullOrWhiteSpace(mediaEndpoint) ||
            !mediaEndpoint.StartsWith("/media/", StringComparison.Ordinal) ||
            mediaEndpoint.StartsWith("//", StringComparison.Ordinal) ||
            !Uri.TryCreate(baseUri, mediaEndpoint, out var candidate))
        {
            return false;
        }

        if (!string.Equals(candidate.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(candidate.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) ||
            candidate.Port != baseUri.Port)
        {
            return false;
        }

        mediaUri = candidate;
        return true;
    }

    private static string? FindBundledCloudflared()
    {
        var roots = new List<string> { AppContext.BaseDirectory };
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string nativeSearchDirectories)
        {
            roots.AddRange(nativeSearchDirectories.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return roots
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(root => Path.Combine(root, "Tools", "cloudflared.exe"))
            .FirstOrDefault(File.Exists);
    }

    private static string CleanError(Exception exception)
    {
        var message = exception.GetBaseException().Message.Trim();
        return message.Length <= 120 ? message : message[..120] + "…";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Volatile.Write(ref _disconnectRequested, 1);
        _lifecycleGate.Wait();
        try
        {
            ResetResourcesAsync().GetAwaiter().GetResult();
        }
        finally
        {
            _lifecycleGate.Release();
        }
        GC.SuppressFinalize(this);
    }

    private readonly record struct ClockSample(
        long RoundTripStopwatchTicks,
        long ServerMinusLocalTicks);

    private sealed class RetryForeverPolicy(IReadOnlyList<TimeSpan> delays) : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            var index = (int)Math.Min(retryContext.PreviousRetryCount, delays.Count - 1L);
            return delays[index];
        }
    }
}
