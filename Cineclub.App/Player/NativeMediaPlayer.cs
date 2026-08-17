using LibVLCSharp.Shared;
using System.IO;

namespace Cineclub.App.Player;

/// <summary>
/// Thin, UI-independent wrapper around LibVLC. Cineclub deliberately feeds it the
/// HTTP Range URL exposed by the room host; no local remuxing or transcoding occurs.
/// </summary>
public sealed class NativeMediaPlayer : IDisposable
{
    private readonly LibVLC _libVlc;
    private Media? _media;
    private bool _pauseOnFirstFrame;
    private bool _rewindOnNextPause;
    private int _primingInProgress;
    private int _completingPriming;
    private int _sourceGeneration;
    private bool _disposed;

    public NativeMediaPlayer()
    {
        InitializeLibVlc();

        _libVlc = new LibVLC(
            "--avcodec-hw=any",
            "--network-caching=1200",
            "--file-caching=800",
            "--http-reconnect",
            "--drop-late-frames",
            "--skip-frames",
            "--quiet");

        MediaPlayer = new MediaPlayer(_libVlc)
        {
            Volume = 82,
            EnableHardwareDecoding = true
        };

        MediaPlayer.Opening += (_, _) => StateChanged?.Invoke(this, NativePlaybackState.Opening);
        MediaPlayer.Buffering += (_, e) =>
        {
            BufferingChanged?.Invoke(this, e.Cache);
            if (e.Cache < 99.5f)
            {
                StateChanged?.Invoke(this, NativePlaybackState.Buffering);
            }
        };
        MediaPlayer.Playing += (_, _) =>
        {
            if (_pauseOnFirstFrame)
            {
                _pauseOnFirstFrame = false;
                _rewindOnNextPause = true;
                var generation = Volatile.Read(ref _sourceGeneration);
                // Never call libvlc_media_player_set_time/set_pause reentrantly from
                // a native Playing callback. Let the callback unwind first.
                _ = Task.Run(() => EnsurePrimedAndPausedAsync(generation));
                return;
            }

            ReadyChanged?.Invoke(this, true);
            StateChanged?.Invoke(this, NativePlaybackState.Playing);
            _ = RefreshTracksAfterStartupAsync();
        };
        MediaPlayer.Paused += (_, _) =>
        {
            if (_rewindOnNextPause)
            {
                var generation = Volatile.Read(ref _sourceGeneration);
                _ = Task.Run(() => CompletePrimingAsync(generation));
                return;
            }

            ReadyChanged?.Invoke(this, true);
            StateChanged?.Invoke(this, NativePlaybackState.Paused);
            _ = RefreshTracksAfterStartupAsync();
        };
        MediaPlayer.Stopped += (_, _) => StateChanged?.Invoke(this, NativePlaybackState.Stopped);
        MediaPlayer.EndReached += (_, _) => StateChanged?.Invoke(this, NativePlaybackState.Ended);
        MediaPlayer.EncounteredError += (_, _) => StateChanged?.Invoke(this, NativePlaybackState.Error);
        MediaPlayer.TimeChanged += (_, e) => PositionChanged?.Invoke(this, new PositionSnapshot(e.Time, DurationMs));
        MediaPlayer.LengthChanged += (_, e) =>
        {
            DurationChanged?.Invoke(this, e.Length);
            PositionChanged?.Invoke(this, new PositionSnapshot(PositionMs, e.Length));
        };
        MediaPlayer.ESAdded += (_, _) => _ = RefreshTracksAfterStartupAsync();
    }

    public MediaPlayer MediaPlayer { get; }

    public Uri? Source { get; private set; }

    public long PositionMs => Math.Max(0, MediaPlayer.Time);

    public long DurationMs => Math.Max(0, MediaPlayer.Length);

    public bool IsPlaying => MediaPlayer.IsPlaying;

    public bool IsSeekable => MediaPlayer.IsSeekable;

    public int Volume
    {
        get => MediaPlayer.Volume;
        set => MediaPlayer.Volume = Math.Clamp(value, 0, 100);
    }

    public event EventHandler<NativePlaybackState>? StateChanged;

    public event EventHandler<PositionSnapshot>? PositionChanged;

    public event EventHandler<long>? DurationChanged;

    public event EventHandler<float>? BufferingChanged;

    public event EventHandler<bool>? ReadyChanged;

    public event EventHandler<TrackSnapshot>? TracksChanged;

    /// <summary>
    /// Opens a network source. Local file URIs are intentionally rejected so host and
    /// guests always exercise the same byte-serving and seeking path.
    /// </summary>
    public void OpenNetworkSource(Uri source, bool pauseOnFirstFrame = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!source.IsAbsoluteUri || (source.Scheme != Uri.UriSchemeHttp && source.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Le lecteur Cineclub attend une URL HTTP ou HTTPS absolue.", nameof(source));
        }

        if (MediaPlayer.Hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "La surface vidéo intégrée n’est pas prête. Cineclub refuse d’ouvrir une fenêtre VLC séparée.");
        }

        // Invalidate every delayed callback from the previous source before asking
        // LibVLC to stop it. Native Stopped/Paused events can arrive asynchronously.
        Interlocked.Increment(ref _sourceGeneration);
        MediaPlayer.Stop();
        _media?.Dispose();
        _media = new Media(_libVlc, source,
            ":network-caching=1200",
            ":http-reconnect",
            ":input-repeat=0");
        Source = source;
        _pauseOnFirstFrame = pauseOnFirstFrame;
        _rewindOnNextPause = false;
        Interlocked.Exchange(ref _primingInProgress, 0);
        Interlocked.Exchange(ref _completingPriming, 0);
        ReadyChanged?.Invoke(this, false);
        StateChanged?.Invoke(this, NativePlaybackState.Opening);
        MediaPlayer.Media = _media;

        // LibVLC has no separate "prepare" call. Starting then pausing on the first
        // decoded frame primes the HTTP connection and hardware decoder for countdown.
        if (!MediaPlayer.Play())
        {
            StateChanged?.Invoke(this, NativePlaybackState.Error);
        }
    }

    public void Play()
    {
        if (_disposed)
        {
            return;
        }

        _pauseOnFirstFrame = false;
        MediaPlayer.Play();
    }

    public void Pause()
    {
        if (_disposed)
        {
            return;
        }

        if (_pauseOnFirstFrame)
        {
            // The initial authoritative room snapshot is paused and commonly
            // arrives while HTTP buffering is still at 0%. Pausing here would
            // prevent LibVLC from ever decoding the first frame, so let the
            // priming Playing handler perform the first pause itself.
            return;
        }

        MediaPlayer.SetPause(true);
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Increment(ref _sourceGeneration);
        _pauseOnFirstFrame = false;
        _rewindOnNextPause = false;
        MediaPlayer.Stop();
    }

    public void TogglePlayPause()
    {
        if (MediaPlayer.IsPlaying)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    public void SeekTo(long positionMs)
    {
        if (_disposed || DurationMs <= 0)
        {
            return;
        }

        MediaPlayer.Time = Math.Clamp(positionMs, 0, DurationMs);
    }

    public void SeekBy(long deltaMs) => SeekTo(PositionMs + deltaMs);

    public void SetPlaybackRate(float rate)
    {
        if (!_disposed)
        {
            MediaPlayer.SetRate(Math.Clamp(rate, 0.95f, 1.05f));
        }
    }

    public void SelectAudioTrack(int id)
    {
        if (_disposed)
        {
            return;
        }

        MediaPlayer.SetAudioTrack(id);
        RefreshTracks();
    }

    public void SelectSubtitleTrack(int id)
    {
        if (_disposed)
        {
            return;
        }

        MediaPlayer.SetSpu(id);
        RefreshTracks();
    }

    public bool AddSubtitleFile(string filePath)
    {
        if (_disposed)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            throw new FileNotFoundException("Le fichier de sous-titres est introuvable.", filePath);
        }

        var subtitleUri = new Uri(Path.GetFullPath(filePath)).AbsoluteUri;
        var added = MediaPlayer.AddSlave(MediaSlaveType.Subtitle, subtitleUri, select: true);
        if (added)
        {
            _ = RefreshTracksAfterStartupAsync();
        }

        return added;
    }

    public void ApplyRemoteState(bool playing, long positionMs, bool hardSeek, float correctionRate = 1f)
    {
        if (_disposed)
        {
            return;
        }

        if (hardSeek)
        {
            SeekTo(positionMs);
        }

        SetPlaybackRate(correctionRate);
        if (playing && !MediaPlayer.IsPlaying)
        {
            Play();
        }
        else if (!playing && MediaPlayer.IsPlaying)
        {
            Pause();
        }
    }

    public void RefreshTracks()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var audio = (MediaPlayer.AudioTrackDescription ?? [])
                .Select(track => new MediaTrackOption(track.Id, CleanTrackName(track.Name), track.Id == MediaPlayer.AudioTrack))
                .ToArray();

            var subtitles = (MediaPlayer.SpuDescription ?? [])
                .Select(track => new MediaTrackOption(
                    track.Id,
                    track.Id == -1 ? "Désactivés" : CleanTrackName(track.Name),
                    track.Id == MediaPlayer.Spu))
                .ToArray();

            TracksChanged?.Invoke(this, new TrackSnapshot(audio, subtitles));
        }
        catch
        {
            // Track discovery can briefly race the demuxer while a network source opens.
            // Playback remains valid and the next ESAdded/Playing event retries it.
        }
    }

    private async Task RefreshTracksAfterStartupAsync()
    {
        await Task.Delay(250).ConfigureAwait(false);
        if (!_disposed)
        {
            RefreshTracks();
        }
    }

    private async Task EnsurePrimedAndPausedAsync(int generation)
    {
        if (Interlocked.Exchange(ref _primingInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            // Give LibVLC's native Playing callback time to return before invoking
            // another player command. CanPause may become true a few frames later.
            await Task.Delay(25).ConfigureAwait(false);
            for (var attempt = 0; attempt < 50 && !_disposed; attempt++)
            {
                if (generation != Volatile.Read(ref _sourceGeneration))
                {
                    return;
                }

                if (!MediaPlayer.IsPlaying)
                {
                    await CompletePrimingAsync(generation).ConfigureAwait(false);
                    return;
                }

                if (MediaPlayer.CanPause)
                {
                    MediaPlayer.SetPause(true);
                }

                await Task.Delay(40).ConfigureAwait(false);
            }

            if (!_disposed)
            {
                // Never let priming silently consume the beginning of the movie.
                // A later authoritative Play starts the retained media again at 0.
                MediaPlayer.Stop();
                _rewindOnNextPause = false;
                ReadyChanged?.Invoke(this, false);
                StateChanged?.Invoke(this, NativePlaybackState.Error);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _primingInProgress, 0);
        }
    }

    private async Task CompletePrimingAsync(int generation)
    {
        if (Interlocked.Exchange(ref _completingPriming, 1) == 1)
        {
            return;
        }

        try
        {
            if (!_rewindOnNextPause || _disposed || generation != Volatile.Read(ref _sourceGeneration))
            {
                return;
            }

            _rewindOnNextPause = false;
            // As with Playing, let the native Paused callback unwind before seeking.
            await Task.Delay(25).ConfigureAwait(false);
            if (_disposed || generation != Volatile.Read(ref _sourceGeneration))
            {
                return;
            }

            MediaPlayer.Time = 0;
            MediaPlayer.SetRate(1f);
            BufferingChanged?.Invoke(this, 100f);
            PositionChanged?.Invoke(this, new PositionSnapshot(0, DurationMs));
            ReadyChanged?.Invoke(this, true);
            StateChanged?.Invoke(this, NativePlaybackState.Paused);
            _ = RefreshTracksAfterStartupAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _completingPriming, 0);
        }
    }

    private static void InitializeLibVlc()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64"),
            AppContext.BaseDirectory
        };

        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string searchDirectories)
        {
            foreach (var searchDirectory in searchDirectories.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                candidates.Add(searchDirectory);
                candidates.Add(Path.Combine(searchDirectory, "libvlc", "win-x64"));
            }
        }

        var nativeDirectory = candidates
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(path =>
                File.Exists(Path.Combine(path, "libvlc.dll")) &&
                File.Exists(Path.Combine(path, "libvlccore.dll")));

        if (nativeDirectory is null)
        {
            Core.Initialize();
        }
        else
        {
            Core.Initialize(nativeDirectory);
        }
    }

    private static string CleanTrackName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Piste sans nom";
        }

        return name.Replace("Track ", "Piste ", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _sourceGeneration);
        _pauseOnFirstFrame = false;
        _rewindOnNextPause = false;
        try
        {
            MediaPlayer.Stop();
        }
        catch
        {
            // Best-effort native shutdown; disposal below still releases handles.
        }
        finally
        {
            MediaPlayer.Dispose();
            _media?.Dispose();
            _libVlc.Dispose();
        }
    }
}

public enum NativePlaybackState
{
    Idle,
    Opening,
    Buffering,
    Playing,
    Paused,
    Stopped,
    Ended,
    Error
}

public sealed record PositionSnapshot(long PositionMs, long DurationMs);

public sealed record MediaTrackOption(int Id, string Name, bool IsSelected)
{
    public override string ToString() => Name;
}

public sealed record TrackSnapshot(
    IReadOnlyList<MediaTrackOption> AudioTracks,
    IReadOnlyList<MediaTrackOption> SubtitleTracks);
