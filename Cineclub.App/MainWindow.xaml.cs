using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cineclub.App.Desktop;
using Cineclub.App.Player;
using Microsoft.Win32;

namespace Cineclub.App;

public partial class MainWindow : Window
{
    private readonly ClientSession _session;
    private readonly NativeMediaPlayer? _player;
    private readonly DispatcherTimer _statusTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _scheduledPlayback;
    private PlaybackCommand? _deferredPlaybackCommand;
    private CancellationTokenSource? _toastLifetime;
    private CancellationTokenSource? _reactionLifetime;
    private long _playbackScheduleVersion;
    private long _countdownScheduleVersion;
    private int _statusReportInFlight;
    private int _playbackRequestInFlight;
    private int _readyRequestInFlight;
    private bool _isSeeking;
    private bool _suppressTrackSelection;
    private bool _decoderReady;
    private bool _userReady;
    private bool _isBuffering;
    private float _bufferPercent;
    private bool _isBusy;
    private bool _isLeaving;
    private bool _isClosing;
    private bool _isFullscreen;
    private WindowState _windowStateBeforeFullscreen;
    private long _latestPlaybackRevision = -1;
    private Uri? _pendingMediaSource;
    private Uri? _openedMediaSource;
    private int _videoSurfaceRenderPhase;
    private DateTimeOffset _videoSurfaceDeadlineUtc;

    public MainWindow()
        : this(new ClientSession(App.RoomTransportFactory?.Invoke()))
    {
    }

    internal MainWindow(ClientSession session)
    {
        _session = session;
        InitializeComponent();

        ParticipantsItems.ItemsSource = Array.Empty<SessionParticipant>();
        ChatItems.ItemsSource = Array.Empty<SessionChatMessage>();
        SeatsItems.ItemsSource = BuildSeatCards([]);

        try
        {
            _player = new NativeMediaPlayer();
            WirePlayer();
            PlayerVideoView.Loaded += (_, _) => SchedulePendingMediaOpen();
            PlayerVideoView.IsVisibleChanged += (_, _) => SchedulePendingMediaOpen();
            PlayerVideoView.SizeChanged += (_, _) => SchedulePendingMediaOpen();
        }
        catch (Exception exception)
        {
            EmptyPlayerTitle.Text = "VLC n’a pas pu démarrer";
            EmptyPlayerSubtitle.Text = exception.Message;
            OpenMovieButton.Visibility = Visibility.Collapsed;
            WelcomeErrorText.Text = $"Lecteur VLC indisponible : {exception.Message}";
            WelcomeErrorText.Visibility = Visibility.Visible;
            HostButton.IsEnabled = false;
            ShowJoinButton.IsEnabled = false;
        }

        WireSession();
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, StatusTimer_Tick, Dispatcher);
        _statusTimer.Start();
        WelcomeNameBox.Focus();
    }

    private void WirePlayer()
    {
        if (_player is null)
        {
            return;
        }

        _player.StateChanged += Player_StateChanged;
        _player.PositionChanged += Player_PositionChanged;
        _player.BufferingChanged += Player_BufferingChanged;
        _player.ReadyChanged += Player_ReadyChanged;
        _player.TracksChanged += Player_TracksChanged;
    }

    private void WireSession()
    {
        _session.StatusChanged += Session_StatusChanged;
        _session.MediaSourceAvailable += Session_MediaSourceAvailable;
        _session.PlaybackCommandReceived += Session_PlaybackCommandReceived;
        _session.ParticipantsChanged += Session_ParticipantsChanged;
        _session.ChatReceived += Session_ChatReceived;
        _session.ReactionReceived += Session_ReactionReceived;
        _session.InvitationAvailable += Session_InvitationAvailable;
    }

    private void UnwirePlayer()
    {
        if (_player is null)
        {
            return;
        }

        _player.StateChanged -= Player_StateChanged;
        _player.PositionChanged -= Player_PositionChanged;
        _player.BufferingChanged -= Player_BufferingChanged;
        _player.ReadyChanged -= Player_ReadyChanged;
        _player.TracksChanged -= Player_TracksChanged;
    }

    private void UnwireSession()
    {
        _session.StatusChanged -= Session_StatusChanged;
        _session.MediaSourceAvailable -= Session_MediaSourceAvailable;
        _session.PlaybackCommandReceived -= Session_PlaybackCommandReceived;
        _session.ParticipantsChanged -= Session_ParticipantsChanged;
        _session.ChatReceived -= Session_ChatReceived;
        _session.ReactionReceived -= Session_ReactionReceived;
        _session.InvitationAvailable -= Session_InvitationAvailable;
    }

    private void Dispatch(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isClosing)
            {
                action();
            }
        }), priority);
    }

    private async void HostButton_Click(object sender, RoutedEventArgs e)
    {
        var path = PickMovieFile();
        if (path is not null)
        {
            await BeginHostingAsync(path);
        }
    }

    private async void OpenMovieButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.IsConnected)
        {
            ShowToast("Quitte la salle pour choisir un autre film.", false);
            return;
        }

        var path = PickMovieFile();
        if (path is not null)
        {
            await BeginHostingAsync(path);
        }
    }

    private string? PickMovieFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choisir le film original",
            Filter = "Films|*.mkv;*.mp4;*.m4v;*.avi;*.mov;*.webm;*.ts;*.m2ts|Tous les fichiers|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private async Task BeginHostingAsync(string moviePath)
    {
        if (!TryGetDisplayName(out var name))
        {
            return;
        }

        SetBusy(true, "Ouverture de la salle…");
        try
        {
            await _session.HostAsync(name, moviePath, _lifetime.Token);
            ShowRoom(isHost: true, Path.GetFileNameWithoutExtension(moviePath));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowWelcomeError(exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowJoinButton_Click(object sender, RoutedEventArgs e)
    {
        JoinLinkPanel.Visibility = Visibility.Visible;
        ShowJoinButton.Visibility = Visibility.Collapsed;
        JoinLinkBox.Focus();
    }

    private async void JoinButton_Click(object sender, RoutedEventArgs e) => await BeginJoiningAsync();

    private async Task BeginJoiningAsync()
    {
        if (!TryGetDisplayName(out var name))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(JoinLinkBox.Text))
        {
            ShowWelcomeError("Colle d’abord le lien reçu sur Discord.");
            JoinLinkBox.Focus();
            return;
        }

        SetBusy(true, "Connexion à la séance…");
        try
        {
            await _session.JoinAsync(name, JoinLinkBox.Text, _lifetime.Token);
            ShowRoom(isHost: false, "Séance Cineclub");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowWelcomeError(exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool TryGetDisplayName(out string name)
    {
        name = WelcomeNameBox.Text.Trim();
        if (name.Length is >= 2 and <= 24)
        {
            WelcomeErrorText.Visibility = Visibility.Collapsed;
            return true;
        }

        ShowWelcomeError("Entre un prénom de 2 à 24 caractères.");
        WelcomeNameBox.Focus();
        return false;
    }

    private void ShowRoom(bool isHost, string movieTitle)
    {
        if (_isClosing)
        {
            return;
        }

        WelcomeView.Visibility = Visibility.Collapsed;
        RoomView.Visibility = Visibility.Visible;
        RoleBadgeText.Text = isHost ? "HÔTE" : "INVITÉ";
        InvitePanel.Visibility = isHost ? Visibility.Visible : Visibility.Collapsed;
        OpenMovieButton.Visibility = isHost ? Visibility.Visible : Visibility.Collapsed;
        EmptyPlayerSubtitle.Text = isHost
            ? "Préparation de la source originale…"
            : "Connexion au film de l’hôte…";
        EmptyPlayerTitle.Text = "La salle est prête";
        MovieTitleText.Text = movieTitle;
        SetHostControls(isHost);
        RefreshParticipants();
        SchedulePendingMediaOpen();
    }

    private void SetHostControls(bool isHost)
    {
        var enabled = isHost && _decoderReady && !_isLeaving;
        PlayPauseButton.IsEnabled = enabled;
        BackTenButton.IsEnabled = enabled;
        ForwardTenButton.IsEnabled = enabled;
        SeekSlider.IsEnabled = enabled;
        SeekSlider.Opacity = enabled ? 1 : 0.72;
    }

    private async void LeaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLeaving || _isClosing)
        {
            return;
        }

        _isLeaving = true;
        LeaveButton.IsEnabled = false;
        SetHostControls(_session.IsHost);
        if (_isFullscreen)
        {
            ToggleFullscreen();
        }

        try
        {
            await _session.LeaveAsync(_lifetime.Token);
        }
        catch (Exception exception)
        {
            ShowToast(exception.Message, false);
        }
        finally
        {
            ResetRoomPresentation();
            _isLeaving = false;
            LeaveButton.IsEnabled = true;
        }
    }

    private void ResetRoomPresentation()
    {
        _player?.Stop();
        var scheduled = Interlocked.Exchange(ref _scheduledPlayback, null);
        scheduled?.Cancel();
        scheduled?.Dispose();
        Interlocked.Increment(ref _playbackScheduleVersion);
        Interlocked.Exchange(ref _countdownScheduleVersion, 0);
        Interlocked.Exchange(ref _latestPlaybackRevision, -1);
        _decoderReady = false;
        _userReady = false;
        _isBuffering = false;
        _bufferPercent = 0;
        _deferredPlaybackCommand = null;
        StopWatchingVideoSurface();
        _pendingMediaSource = null;
        _openedMediaSource = null;

        ReadyButton.IsEnabled = false;
        ReadyButton.Content = "JE SUIS PRÊT";
        ReadyButton.Background = (Brush)FindResource("SurfaceRaisedBrush");
        AudioTracksCombo.ItemsSource = null;
        AudioTracksCombo.IsEnabled = false;
        SubtitleTracksCombo.ItemsSource = null;
        SubtitleTracksCombo.IsEnabled = false;
        SubtitleFileButton.IsEnabled = false;
        SeekSlider.Maximum = 1;
        SeekSlider.Value = 0;
        PositionText.Text = "00:00";
        DurationText.Text = "--:--";
        PlayPauseButton.Content = "▶";
        BufferingPanel.Visibility = Visibility.Collapsed;
        ReactionOverlay.Visibility = Visibility.Collapsed;
        PlayerEmptyOverlay.Visibility = Visibility.Visible;
        EmptyPlayerTitle.Text = "La salle est prête";
        EmptyPlayerSubtitle.Text = "Choisis le film original pour démarrer la diffusion.";
        SyncBadgeText.Text = "SYNCHRO";
        InviteTextBox.Text = "Création du lien…";
        CopyInviteButton.IsEnabled = false;
        ParticipantsItems.ItemsSource = Array.Empty<SessionParticipant>();
        ChatItems.ItemsSource = Array.Empty<SessionChatMessage>();
        SeatsItems.ItemsSource = BuildSeatCards([]);
        ParticipantCountText.Text = "0 / 6";
        ChatBox.Clear();
        RoomView.Visibility = Visibility.Collapsed;
        WelcomeView.Visibility = Visibility.Visible;
    }

    private void Session_StatusChanged(object? sender, SessionStatus status) => Dispatch(() =>
    {
        RoomStatusText.Text = status.Message;
        StatusDot.Fill = status.State switch
        {
            SessionConnectionState.Connected => (Brush)FindResource("SuccessBrush"),
            SessionConnectionState.Error => new SolidColorBrush(Color.FromRgb(255, 89, 126)),
            _ => (Brush)FindResource("WarningBrush")
        };

        if (_isBusy)
        {
            WelcomeBusyText.Text = status.Message;
        }

        if (status.State == SessionConnectionState.Error)
        {
            ShowToast(status.Message, false);
        }
    });

    private void Session_InvitationAvailable(object? sender, string invitation) => Dispatch(() =>
    {
        InviteTextBox.Text = invitation;
        CopyInviteButton.IsEnabled = Uri.TryCreate(invitation, UriKind.Absolute, out _);
    });

    private void Session_MediaSourceAvailable(object? sender, Uri source) => Dispatch(() =>
    {
        if (_player is null)
        {
            ShowToast("Le lecteur VLC n’est pas disponible.", false);
            return;
        }

        try
        {
            PlayerEmptyOverlay.Visibility = Visibility.Collapsed;
            BufferingPanel.Visibility = Visibility.Visible;
            if (source != _openedMediaSource)
            {
                var wasUserReady = _userReady;
                _decoderReady = false;
                _userReady = false;
                ReadyButton.IsEnabled = false;
                ReadyButton.Content = "CHARGEMENT…";
                ReadyButton.Background = (Brush)FindResource("SurfaceRaisedBrush");
                SetHostControls(_session.IsHost);
                if (wasUserReady && _session.IsConnected)
                {
                    _ = SafeSessionCallAsync(() => _session.SetReadyAsync(false, _lifetime.Token));
                }
            }
            _pendingMediaSource = source;
            SchedulePendingMediaOpen();
        }
        catch (Exception exception)
        {
            _pendingMediaSource = null;
            ShowIntegratedPlayerError("Impossible d’ouvrir le film", exception.Message, exception.Message);
        }
    });

    private void SchedulePendingMediaOpen()
    {
        if (_isClosing || _isLeaving ||
            _videoSurfaceRenderPhase != 0 ||
            RoomView.Visibility != Visibility.Visible ||
            _pendingMediaSource is null ||
            _pendingMediaSource == _openedMediaSource)
        {
            return;
        }

        _videoSurfaceRenderPhase = 1;
        _videoSurfaceDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(3);
        CompositionTarget.Rendering += VideoSurface_Rendering;
    }

    private void VideoSurface_Rendering(object? sender, EventArgs e)
    {
        if (_isClosing || _isLeaving || RoomView.Visibility != Visibility.Visible || _pendingMediaSource is null)
        {
            StopWatchingVideoSurface();
            return;
        }

        if (!CanUseEmbeddedVideoSurface() || _player is null)
        {
            // Stay subscribed until WPF has completed layout and produced the
            // presentation source for the newly visible WindowsFormsHost.
            if (DateTimeOffset.UtcNow >= _videoSurfaceDeadlineUtc)
            {
                FailEmbeddedVideoSurface();
            }
            return;
        }

        if (_videoSurfaceRenderPhase == 1)
        {
            // Force LibVLCSharp.WPF to bind the MediaPlayer after its HWND host
            // exists. The following render pass creates/attaches the child HWND.
            PlayerVideoView.MediaPlayer = null;
            PlayerVideoView.MediaPlayer = _player.MediaPlayer;
            _videoSurfaceRenderPhase = 2;
            _videoSurfaceDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(3);
            return;
        }

        if (_player.MediaPlayer.Hwnd == IntPtr.Zero)
        {
            if (DateTimeOffset.UtcNow >= _videoSurfaceDeadlineUtc)
            {
                FailEmbeddedVideoSurface();
            }
            return;
        }

        // A full composition pass has occurred since attachment: LibVLC now has
        // an embedded vout target and cannot fall back to a separate D3D11 window.
        StopWatchingVideoSurface();
        OpenPendingMediaOnEmbeddedSurface();
    }

    private void OpenPendingMediaOnEmbeddedSurface()
    {
        if (_isClosing || _isLeaving || !CanUseEmbeddedVideoSurface() || _player is null)
        {
            return;
        }

        var source = _pendingMediaSource;
        if (source is null || source == _openedMediaSource)
        {
            return;
        }

        try
        {
            _player.OpenNetworkSource(source, pauseOnFirstFrame: true);
            _openedMediaSource = source;
        }
        catch (Exception exception)
        {
            _pendingMediaSource = null;
            ShowIntegratedPlayerError("Impossible d’ouvrir le film", exception.Message, exception.Message);
        }
    }

    private void StopWatchingVideoSurface()
    {
        if (_videoSurfaceRenderPhase == 0)
        {
            return;
        }

        CompositionTarget.Rendering -= VideoSurface_Rendering;
        _videoSurfaceRenderPhase = 0;
        _videoSurfaceDeadlineUtc = default;
    }

    private void FailEmbeddedVideoSurface()
    {
        StopWatchingVideoSurface();
        _pendingMediaSource = null;
        ShowIntegratedPlayerError(
            "Surface vidéo indisponible",
            "Cineclub n’a pas pu intégrer VLC à la fenêtre. Quitte puis rejoins la salle pour réessayer.",
            "Surface VLC intégrée indisponible — aucune fenêtre externe n’a été ouverte.");
    }

    private void ShowIntegratedPlayerError(string title, string details, string toast)
    {
        var wasUserReady = _userReady;
        _decoderReady = false;
        _userReady = false;
        _deferredPlaybackCommand = null;
        _scheduledPlayback?.Cancel();
        Interlocked.Increment(ref _playbackScheduleVersion);
        Interlocked.Exchange(ref _countdownScheduleVersion, 0);
        _isBuffering = false;
        BufferingPanel.Visibility = Visibility.Collapsed;
        PlayerEmptyOverlay.Visibility = Visibility.Visible;
        OpenMovieButton.Visibility = Visibility.Collapsed;
        EmptyPlayerTitle.Text = title;
        EmptyPlayerSubtitle.Text = details;
        ReadyButton.IsEnabled = false;
        ReadyButton.Content = "CHARGEMENT…";
        ReadyButton.Background = (Brush)FindResource("SurfaceRaisedBrush");
        SubtitleFileButton.IsEnabled = false;
        SetHostControls(_session.IsHost);
        ShowToast(toast, false);

        if (wasUserReady && _session.IsConnected)
        {
            _ = SafeSessionCallAsync(() => _session.SetReadyAsync(false, _lifetime.Token));
        }
    }

    private bool CanUseEmbeddedVideoSurface()
    {
        return RoomView.Visibility == Visibility.Visible &&
               PlayerVideoView.IsLoaded &&
               PlayerVideoView.IsVisible &&
               PlayerVideoView.ActualWidth >= 2 &&
               PlayerVideoView.ActualHeight >= 2 &&
               PlayerVideoView.IsMeasureValid &&
               PlayerVideoView.IsArrangeValid &&
               PresentationSource.FromVisual(PlayerVideoView) is not null;
    }

    private void Session_PlaybackCommandReceived(object? sender, PlaybackCommand command)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatch(() => Session_PlaybackCommandReceived(sender, command));
            return;
        }

        if (_isClosing || _isLeaving)
        {
            return;
        }

        var latestRevision = Interlocked.Read(ref _latestPlaybackRevision);
        if (!_decoderReady)
        {
            if (command.Revision < latestRevision)
            {
                return;
            }

            var deferred = _deferredPlaybackCommand;
            if (command.IsCorrection)
            {
                // The initial/reconnect snapshot is intentionally represented as a
                // correction and can arrive before LibVLC has decoded its first frame.
                // Keep the freshest such snapshot, but never let a same-revision
                // correction overwrite an already retained scheduled command.
                if (deferred is { IsCorrection: false } && command.Revision <= deferred.Revision)
                {
                    return;
                }
            }
            else if (command.Revision == latestRevision && deferred is { IsCorrection: false })
            {
                return;
            }

            var isNewerRevision = command.Revision > latestRevision;
            if (isNewerRevision)
            {
                Interlocked.Exchange(ref _latestPlaybackRevision, command.Revision);
            }

            _deferredPlaybackCommand = command;
            if (!command.IsCorrection || isNewerRevision)
            {
                _scheduledPlayback?.Cancel();
            }
            return;
        }

        if (command.IsCorrection && command.Revision == latestRevision)
        {
            // Drift corrections are snapshots of the currently active revision. They
            // must not cancel a newer play/pause deadline that is already counting down.
            Dispatch(() => ApplyPlaybackCorrection(command));
            return;
        }

        if (command.Revision <= latestRevision)
        {
            return;
        }

        Interlocked.Exchange(ref _latestPlaybackRevision, command.Revision);
        QueueAuthoritativePlayback(command);
    }

    private void QueueAuthoritativePlayback(PlaybackCommand command)
    {
        var version = Interlocked.Increment(ref _playbackScheduleVersion);
        var current = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var previous = Interlocked.Exchange(ref _scheduledPlayback, current);
        previous?.Cancel();
        previous?.Dispose();
        _ = ApplyScheduledPlaybackAsync(command, version, current.Token);
    }

    private void ApplyPlaybackCorrection(PlaybackCommand command)
    {
        if (_player is null)
        {
            return;
        }

        var targetPosition = command.PositionMs;
        if (command.IsPlaying && command.ExecuteAt is { } sampledAt)
        {
            targetPosition += Math.Max(0, (long)(DateTimeOffset.Now - sampledAt).TotalMilliseconds);
        }

        _player.ApplyRemoteState(command.IsPlaying, targetPosition, command.HardSeek, command.CorrectionRate);
        SyncBadgeText.Text = command.HardSeek
            ? "RESYNCHRO"
            : Math.Abs(command.CorrectionRate - 1f) > 0.001f ? "AJUSTEMENT" : "SYNCHRO";
    }

    private async Task ApplyScheduledPlaybackAsync(
        PlaybackCommand command,
        long scheduleVersion,
        CancellationToken cancellationToken)
    {
        var executeAt = command.ExecuteAt ?? DateTimeOffset.Now;
        var wait = executeAt - DateTimeOffset.Now;
        if (wait > TimeSpan.Zero)
        {
            try
            {
                if (command.IsPlaying && wait >= TimeSpan.FromMilliseconds(900))
                {
                    while ((wait = executeAt - DateTimeOffset.Now) > TimeSpan.Zero)
                    {
                        var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (scheduleVersion != Interlocked.Read(ref _playbackScheduleVersion))
                            {
                                return;
                            }

                            Interlocked.Exchange(ref _countdownScheduleVersion, scheduleVersion);
                            BufferingText.Text = $"La séance commence dans {seconds}…";
                            BufferingPanel.Visibility = Visibility.Visible;
                        });
                        await Task.Delay(
                            TimeSpan.FromMilliseconds(Math.Min(250, Math.Max(1, wait.TotalMilliseconds))),
                            cancellationToken);
                    }
                }
                else
                {
                    await Task.Delay(wait, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                HideCountdown(scheduleVersion);
                return;
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            HideCountdown(scheduleVersion);
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            if (_player is null || scheduleVersion != Interlocked.Read(ref _playbackScheduleVersion))
            {
                return;
            }

            var lateByMs = command.IsPlaying
                ? Math.Max(0, (long)(DateTimeOffset.Now - executeAt).TotalMilliseconds)
                : 0;
            var targetPosition = command.PositionMs + lateByMs;
            var hardSeek = command.HardSeek || Math.Abs(_player.PositionMs - targetPosition) > 750;
            _player.ApplyRemoteState(command.IsPlaying, targetPosition, hardSeek, command.CorrectionRate);
            SyncBadgeText.Text = lateByMs > 350 ? "RATTRAPAGE" : "SYNCHRO";
            Interlocked.CompareExchange(ref _countdownScheduleVersion, 0, scheduleVersion);
            if (!_isBuffering)
            {
                BufferingPanel.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void HideCountdown(long scheduleVersion)
    {
        Dispatch(() =>
        {
            if (Interlocked.CompareExchange(ref _countdownScheduleVersion, 0, scheduleVersion) == scheduleVersion &&
                !_isBuffering)
            {
                BufferingPanel.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void Session_ParticipantsChanged(object? sender, EventArgs e) => Dispatch(RefreshParticipants);

    private void RefreshParticipants()
    {
        var participants = _session.Participants.ToArray();
        ParticipantsItems.ItemsSource = participants;
        ParticipantCountText.Text = $"{participants.Length} / 6";
        SeatsItems.ItemsSource = BuildSeatCards(participants);

        var snapshot = _session.LatestSnapshot;
        if (!string.IsNullOrWhiteSpace(snapshot?.MovieTitle))
        {
            MovieTitleText.Text = snapshot.MovieTitle;
        }

        if (snapshot?.AllReady == true)
        {
            SyncBadgeText.Text = "TOUS PRÊTS";
        }
        else if (SyncBadgeText.Text == "TOUS PRÊTS")
        {
            SyncBadgeText.Text = "SYNCHRO";
        }
    }

    private IReadOnlyList<SeatCardViewModel> BuildSeatCards(IReadOnlyList<SessionParticipant> participants)
    {
        return Enumerable.Range(0, 6)
            .Select(number =>
            {
                var occupant = participants.FirstOrDefault(participant => participant.Seat == number);
                return new SeatCardViewModel(
                    number,
                    $"Fauteuil {number + 1}",
                    occupant?.DisplayName ?? "Libre",
                    occupant?.Initials ?? (number + 1).ToString(),
                    occupant?.DisplayName == _session.DisplayName);
            })
            .ToArray();
    }

    private void Session_ChatReceived(object? sender, SessionChatMessage message) => Dispatch(() =>
    {
        ChatItems.ItemsSource = _session.Messages.ToArray();
        Dispatch(ChatScrollViewer.ScrollToEnd, DispatcherPriority.ContextIdle);
    });

    private void Session_ReactionReceived(object? sender, SessionReaction reaction) => Dispatch(() =>
    {
        _ = DisplayReactionAsync(reaction.Emoji);
    });

    private async Task DisplayReactionAsync(string emoji)
    {
        if (_isClosing || _isLeaving)
        {
            return;
        }

        _reactionLifetime?.Cancel();
        _reactionLifetime?.Dispose();
        _reactionLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        ReactionOverlayText.Text = emoji;
        ReactionOverlay.Visibility = Visibility.Visible;
        try
        {
            await Task.Delay(1700, _reactionLifetime.Token);
            ReactionOverlay.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Player_StateChanged(object? sender, NativePlaybackState state) => Dispatch(() =>
    {
        if (RoomView.Visibility != Visibility.Visible)
        {
            return;
        }

        PlayPauseButton.Content = state == NativePlaybackState.Playing ? "Ⅱ" : "▶";
        switch (state)
        {
            case NativePlaybackState.Opening:
                BufferingText.Text = "Ouverture du film…";
                BufferingPanel.Visibility = Visibility.Visible;
                break;
            case NativePlaybackState.Buffering:
                BufferingPanel.Visibility = Visibility.Visible;
                break;
            case NativePlaybackState.Playing:
            case NativePlaybackState.Paused:
                if (!_isBuffering && Interlocked.Read(ref _countdownScheduleVersion) == 0)
                {
                    BufferingPanel.Visibility = Visibility.Collapsed;
                }
                break;
            case NativePlaybackState.Error:
                ShowIntegratedPlayerError(
                    "Lecture impossible",
                    "VLC n’arrive pas à lire le flux original. Vérifie la connexion puis quitte et rejoins la salle.",
                    "VLC n’arrive pas à lire le flux du film.");
                break;
        }
    });

    private void Player_PositionChanged(object? sender, PositionSnapshot position) => Dispatch(() =>
    {
        if (RoomView.Visibility != Visibility.Visible)
        {
            return;
        }

        if (!_isSeeking)
        {
            SeekSlider.Maximum = Math.Max(1, position.DurationMs);
            SeekSlider.Value = Math.Clamp(position.PositionMs, 0, Math.Max(1, position.DurationMs));
        }

        PositionText.Text = FormatDuration(position.PositionMs);
        DurationText.Text = position.DurationMs > 0 ? FormatDuration(position.DurationMs) : "--:--";
    });

    private void Player_BufferingChanged(object? sender, float percent) => Dispatch(() =>
    {
        if (RoomView.Visibility != Visibility.Visible)
        {
            return;
        }

        _bufferPercent = percent;
        _isBuffering = percent < 99.5f;
        if (_isBuffering)
        {
            Interlocked.Exchange(ref _countdownScheduleVersion, 0);
            BufferingText.Text = $"Mise en mémoire… {Math.Round(percent):0}%";
            BufferingPanel.Visibility = Visibility.Visible;
        }
        else if (Interlocked.Read(ref _countdownScheduleVersion) == 0)
        {
            BufferingPanel.Visibility = Visibility.Collapsed;
        }
    });

    private void Player_ReadyChanged(object? sender, bool ready) => Dispatch(async () =>
    {
        if (RoomView.Visibility != Visibility.Visible)
        {
            return;
        }

        _decoderReady = ready;
        ReadyButton.IsEnabled = ready;
        SubtitleFileButton.IsEnabled = ready;
        ReadyButton.Content = _userReady ? "PRÊT ✓" : ready ? "JE SUIS PRÊT" : "CHARGEMENT…";
        SetHostControls(_session.IsHost);
        if (ready && _deferredPlaybackCommand is { } deferred)
        {
            _deferredPlaybackCommand = null;
            // Apply without passing through revision filtering again. The scheduled
            // method recomputes remaining/late time from ExecuteAt at this instant.
            QueueAuthoritativePlayback(deferred);
        }
        if (!ready && _userReady)
        {
            _userReady = false;
            ReadyButton.Content = "JE SUIS PRÊT";
            ReadyButton.Background = (Brush)FindResource("SurfaceRaisedBrush");
            await SafeSessionCallAsync(() => _session.SetReadyAsync(false, _lifetime.Token));
        }
    });

    private void Player_TracksChanged(object? sender, TrackSnapshot tracks) => Dispatch(() =>
    {
        if (RoomView.Visibility != Visibility.Visible)
        {
            return;
        }

        _suppressTrackSelection = true;
        AudioTracksCombo.ItemsSource = tracks.AudioTracks;
        AudioTracksCombo.SelectedItem = tracks.AudioTracks.FirstOrDefault(track => track.IsSelected)
            ?? tracks.AudioTracks.FirstOrDefault();
        AudioTracksCombo.IsEnabled = tracks.AudioTracks.Count > 0;
        SubtitleTracksCombo.ItemsSource = tracks.SubtitleTracks;
        SubtitleTracksCombo.SelectedItem = tracks.SubtitleTracks.FirstOrDefault(track => track.IsSelected)
            ?? tracks.SubtitleTracks.FirstOrDefault();
        SubtitleTracksCombo.IsEnabled = tracks.SubtitleTracks.Count > 0;
        _suppressTrackSelection = false;
    });

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null || !_decoderReady || _isLeaving || _isClosing ||
            Interlocked.CompareExchange(ref _playbackRequestInFlight, 1, 0) != 0)
        {
            return;
        }

        PlayPauseButton.IsEnabled = false;
        try
        {
            if (_session.IsConnected)
            {
                await SafeSessionCallAsync(() => _session.RequestPlaybackAsync(!_player.IsPlaying, _player.PositionMs, _lifetime.Token));
            }
            else
            {
                _player.TogglePlayPause();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _playbackRequestInFlight, 0);
            SetHostControls(_session.IsHost);
        }
    }

    private async void BackTenButton_Click(object sender, RoutedEventArgs e) => await RequestSeekAsync(-10_000);

    private async void ForwardTenButton_Click(object sender, RoutedEventArgs e) => await RequestSeekAsync(10_000);

    private async Task RequestSeekAsync(long deltaMs)
    {
        if (_player is null || !_decoderReady || _isLeaving || _isClosing)
        {
            return;
        }

        var target = Math.Clamp(_player.PositionMs + deltaMs, 0, Math.Max(0, _player.DurationMs));
        if (_session.IsConnected)
        {
            await SafeSessionCallAsync(() => _session.RequestSeekAsync(target, _lifetime.Token));
        }
        else
        {
            _player.SeekTo(target);
        }
    }

    private void SeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _isSeeking = true;

    private void SeekSlider_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (e.MouseDevice.LeftButton == MouseButtonState.Released)
        {
            _isSeeking = false;
        }
    }

    private async void SeekSlider_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var delta = e.Key switch
        {
            Key.Left => -10_000L,
            Key.Right => 10_000L,
            _ => 0L
        };
        if (delta == 0)
        {
            return;
        }

        e.Handled = true;
        await RequestSeekAsync(delta);
    }

    private async void SeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isSeeking = false;
        if (_player is null)
        {
            return;
        }

        var target = (long)SeekSlider.Value;
        if (_session.IsConnected)
        {
            await SafeSessionCallAsync(() => _session.RequestSeekAsync(target, _lifetime.Token));
        }
        else
        {
            _player.SeekTo(target);
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player is not null)
        {
            _player.Volume = (int)Math.Round(e.NewValue);
        }
    }

    private void AudioTracksCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressTrackSelection && _player is not null && AudioTracksCombo.SelectedItem is MediaTrackOption track)
        {
            _player.SelectAudioTrack(track.Id);
        }
    }

    private void SubtitleTracksCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressTrackSelection && _player is not null && SubtitleTracksCombo.SelectedItem is MediaTrackOption track)
        {
            _player.SelectSubtitleTrack(track.Id);
        }
    }

    private void SubtitleFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null || !_decoderReady)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Ajouter des sous-titres locaux",
            Filter = "Sous-titres|*.srt;*.ass;*.ssa;*.vtt;*.sub|Tous les fichiers|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            if (_player.AddSubtitleFile(dialog.FileName))
            {
                ShowToast($"Sous-titres ajoutés : {Path.GetFileName(dialog.FileName)}");
            }
            else
            {
                ShowToast("VLC n’a pas pu ajouter ce fichier de sous-titres.", false);
            }
        }
        catch (Exception exception)
        {
            ShowToast(exception.Message, false);
        }
    }

    private async void ReadyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_decoderReady || _isLeaving || _isClosing)
        {
            ShowToast("Attends que le film soit chargé sur ton PC.", false);
            return;
        }

        if (Interlocked.CompareExchange(ref _readyRequestInFlight, 1, 0) != 0)
        {
            return;
        }

        _userReady = !_userReady;
        ReadyButton.Content = _userReady ? "PRÊT ✓" : "JE SUIS PRÊT";
        ReadyButton.Background = _userReady ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("SurfaceRaisedBrush");
        ReadyButton.IsEnabled = false;
        try
        {
            var requestedReady = _userReady;
            if (!await SafeSessionCallAsync(() => _session.SetReadyAsync(requestedReady, _lifetime.Token)))
            {
                _userReady = !requestedReady;
                ReadyButton.Content = _userReady ? "PRÊT ✓" : "JE SUIS PRÊT";
                ReadyButton.Background = _userReady
                    ? (Brush)FindResource("SuccessBrush")
                    : (Brush)FindResource("SurfaceRaisedBrush");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _readyRequestInFlight, 0);
            ReadyButton.IsEnabled = _decoderReady && !_isLeaving && !_isClosing;
        }
    }

    private async void SeatButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: not null } button && int.TryParse(button.Tag.ToString(), out var seat))
        {
            await SafeSessionCallAsync(() => _session.ChooseSeatAsync(seat, _lifetime.Token));
        }
    }

    private async void SendChatButton_Click(object sender, RoutedEventArgs e) => await SendChatAsync();

    private async Task SendChatAsync()
    {
        var text = ChatBox.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        ChatBox.Clear();
        if (!await SafeSessionCallAsync(() => _session.SendChatAsync(text, _lifetime.Token)) &&
            string.IsNullOrWhiteSpace(ChatBox.Text) && !_isClosing)
        {
            ChatBox.Text = text;
            ChatBox.CaretIndex = ChatBox.Text.Length;
        }
    }

    private async void ReactionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string reaction })
        {
            await SafeSessionCallAsync(() => _session.SendReactionAsync(reaction, _lifetime.Token));
        }
    }

    private async void ChatBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SendChatAsync();
        }
    }

    private async void StatusTimer_Tick(object? sender, EventArgs e)
    {
        if (_isClosing || _isLeaving || !_session.IsConnected || _player is null ||
            Interlocked.CompareExchange(ref _statusReportInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await _session.ReportPlayerStatusAsync(
                _decoderReady && _userReady,
                _isBuffering,
                _bufferPercent,
                _player.PositionMs,
                _player.IsPlaying,
                _lifetime.Token);
        }
        catch
        {
            // The transport owns reconnect feedback; a periodic status sample is expendable.
        }
        finally
        {
            Interlocked.Exchange(ref _statusReportInFlight, 0);
        }
    }

    private async Task<bool> SafeSessionCallAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            ShowToast(exception.Message, false);
            return false;
        }
    }

    private void CopyInviteButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Uri.TryCreate(InviteTextBox.Text, UriKind.Absolute, out _))
            {
                ShowToast("Le lien d’invitation n’est pas encore prêt.", false);
                return;
            }

            Clipboard.SetText(InviteTextBox.Text);
            ShowToast("Lien d’invitation copié");
        }
        catch (Exception exception)
        {
            ShowToast($"Copie impossible : {exception.Message}", false);
        }
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void PlayerVideoView_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (_isClosing || RoomView.Visibility != Visibility.Visible)
        {
            return;
        }

        _isFullscreen = !_isFullscreen;
        if (_isFullscreen)
        {
            FullscreenButton.ToolTip = "Quitter le plein écran (Échap)";
            AutomationProperties.SetName(FullscreenButton, "Quitter le plein écran");
            _windowStateBeforeFullscreen = WindowState;
            TitleBar.Visibility = Visibility.Collapsed;
            TitleBarRow.Height = new GridLength(0);
            RoomHeader.Visibility = Visibility.Collapsed;
            RoomHeaderRow.Height = new GridLength(0);
            SidebarPanel.Visibility = Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(0);
            ControlsPanel.Visibility = Visibility.Collapsed;
            SeatsPanel.Visibility = Visibility.Collapsed;
            PlayerColumn.Margin = new Thickness(0);
            RoomContentGrid.Margin = new Thickness(0);
            VideoFrame.CornerRadius = new CornerRadius(0);
            WindowState = WindowState.Maximized;
        }
        else
        {
            FullscreenButton.ToolTip = "Plein écran";
            AutomationProperties.SetName(FullscreenButton, "Activer le plein écran");
            TitleBar.Visibility = Visibility.Visible;
            TitleBarRow.Height = new GridLength(40);
            RoomHeader.Visibility = Visibility.Visible;
            RoomHeaderRow.Height = new GridLength(62);
            SidebarPanel.Visibility = Visibility.Visible;
            SidebarColumn.Width = new GridLength(330);
            ControlsPanel.Visibility = Visibility.Visible;
            SeatsPanel.Visibility = Visibility.Visible;
            PlayerColumn.Margin = new Thickness(0, 0, 12, 0);
            RoomContentGrid.Margin = new Thickness(16, 0, 16, 16);
            VideoFrame.CornerRadius = new CornerRadius(16);
            WindowState = _windowStateBeforeFullscreen;
        }
    }

    private async void WelcomeNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && JoinLinkPanel.Visibility == Visibility.Visible)
        {
            await BeginJoiningAsync();
        }
    }

    private async void JoinLinkBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await BeginJoiningAsync();
        }
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _isFullscreen)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if (RoomView.Visibility != Visibility.Visible ||
            ChatBox.IsKeyboardFocusWithin ||
            Keyboard.FocusedElement is Button or Slider or ComboBox or TextBox ||
            !_decoderReady ||
            !_session.IsHost)
        {
            return;
        }

        if (e.Key == Key.Space)
        {
            PlayPauseButton_Click(PlayPauseButton, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            await RequestSeekAsync(-10_000);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            await RequestSeekAsync(10_000);
            e.Handled = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // The pointer can be released between the mouse event and DragMove.
            }
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy, string? message = null)
    {
        _isBusy = busy;
        WelcomeBusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        var canStartSession = !busy && _player is not null && !_isClosing;
        HostButton.IsEnabled = canStartSession;
        ShowJoinButton.IsEnabled = canStartSession;
        JoinButton.IsEnabled = canStartSession;
        if (message is not null)
        {
            WelcomeBusyText.Text = message;
        }
    }

    private void ShowWelcomeError(string message)
    {
        WelcomeErrorText.Text = message;
        WelcomeErrorText.Visibility = Visibility.Visible;
    }

    private async void ShowToast(string message, bool success = true)
    {
        if (_isClosing)
        {
            return;
        }

        _toastLifetime?.Cancel();
        _toastLifetime?.Dispose();
        _toastLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        ToastText.Text = message;
        ToastDot.Fill = success ? (Brush)FindResource("SuccessBrush") : new SolidColorBrush(Color.FromRgb(255, 89, 126));
        ToastPanel.Visibility = Visibility.Visible;
        try
        {
            await Task.Delay(2600, _toastLifetime.Token);
            ToastPanel.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string FormatDuration(long milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        _statusTimer.Stop();
        UnwireSession();
        UnwirePlayer();
        _lifetime.Cancel();
        var scheduled = Interlocked.Exchange(ref _scheduledPlayback, null);
        scheduled?.Cancel();
        scheduled?.Dispose();
        Interlocked.Increment(ref _playbackScheduleVersion);
        Interlocked.Exchange(ref _countdownScheduleVersion, 0);
        _deferredPlaybackCommand = null;
        _toastLifetime?.Cancel();
        _toastLifetime?.Dispose();
        _reactionLifetime?.Cancel();
        _reactionLifetime?.Dispose();
        StopWatchingVideoSurface();
        _session.Dispose();
        PlayerVideoView.MediaPlayer = null;
        _player?.Dispose();
        PlayerVideoView.Dispose();
        _lifetime.Dispose();
    }
}

internal sealed record SeatCardViewModel(
    int Number,
    string Label,
    string OccupantName,
    string Initials,
    bool IsMine);
