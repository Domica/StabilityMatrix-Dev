using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using Projektanker.Icons.Avalonia;
using StabilityMatrix.Avalonia.Controls;

namespace StabilityMatrix.Avalonia.Controls;

/// <summary>
/// Plays back local (or remote) video files inside the app using LibVLCSharp.
/// Intended to live inside the <see cref="ImageSourceTemplateType.Video" /> data template
/// so generated .mp4/.webm/etc. files can be previewed in the internal media viewer.
/// </summary>
public partial class VideoPlayerControl : UserControl
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<VideoPlayerControl, string?>(nameof(Source));

    public static readonly StyledProperty<bool> AutoPlayProperty =
        AvaloniaProperty.Register<VideoPlayerControl, bool>(nameof(AutoPlay), defaultValue: true);

    /// <summary>
    /// A single shared LibVLC instance for the whole process. Created lazily and
    /// wrapped in a try/catch because the native libraries may be missing on some platforms.
    /// </summary>
    private static readonly Lazy<LibVLC?> SharedLibVlc =
        new(() =>
        {
            try
            {
                return new LibVLC();
            }
            catch (Exception)
            {
                return null;
            }
        });

    private MediaPlayer? mediaPlayer;
    private Media? currentMedia;
    private bool isPlaying;
    private bool isLoaded;

    public VideoPlayerControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool AutoPlay
    {
        get => GetValue(AutoPlayProperty);
        set => SetValue(AutoPlayProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty)
        {
            isPlaying = false;
            UpdatePlayPauseIcon();

            if (isLoaded)
            {
                StopPlayback();
                if (AutoPlay)
                {
                    StartPlayback();
                }
            }
        }
        else if (change.Property == AutoPlayProperty)
        {
            if (isLoaded && AutoPlay && !isPlaying && !string.IsNullOrWhiteSpace(Source))
            {
                StartPlayback();
            }
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        isLoaded = true;

        if (SharedLibVlc.Value is null)
        {
            ShowFallback("Video preview is not available on this platform.");
            return;
        }

        if (AutoPlay && !string.IsNullOrWhiteSpace(Source))
        {
            StartPlayback();
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        isLoaded = false;
        StopPlayback();
    }

    private void PlayPauseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (SharedLibVlc.Value is null)
            return;

        if (isPlaying)
        {
            PausePlayback();
        }
        else
        {
            StartPlayback();
        }
    }

    private void StartPlayback()
    {
        if (string.IsNullOrWhiteSpace(Source) || SharedLibVlc.Value is not { } libVlc)
            return;

        try
        {
            if (mediaPlayer is null)
            {
                mediaPlayer = new MediaPlayer(libVlc);
                // Lambda subscriptions keep us agnostic to the exact event delegate
                // signature across LibVLCSharp versions.
                mediaPlayer.EncounteredError += (_, _) => OnEncounteredError();
                mediaPlayer.EndReached += (_, _) => OnEndReached();
                VideoView.MediaPlayer = mediaPlayer;
            }

            currentMedia?.Dispose();
            currentMedia = new Media(libVlc, Source, FromType.FromPath);
            mediaPlayer.Play(currentMedia);

            isPlaying = true;
            UpdatePlayPauseIcon();
        }
        catch (Exception ex)
        {
            ShowFallback($"Could not play this video: {ex.Message}");
        }
    }

    private void PausePlayback()
    {
        try
        {
            mediaPlayer?.Pause();
        }
        catch
        {
            /* ignore */
        }

        isPlaying = false;
        UpdatePlayPauseIcon();
    }

    private void StopPlayback()
    {
        // Detach the native view FIRST so it stops rendering to the (soon-to-be
        // destroyed) window handle. Then dispose the player off the UI thread to
        // avoid blocking/deadlocking the UI thread -- a common LibVLCSharp hang/crash.
        var mp = mediaPlayer;
        var media = currentMedia;
        mediaPlayer = null;
        currentMedia = null;
        VideoView.MediaPlayer = null;

        isPlaying = false;
        UpdatePlayPauseIcon();

        if (mp is null && media is null)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                mp?.Stop();
            }
            catch
            {
                /* ignore */
            }

            try
            {
                mp?.Dispose();
            }
            catch
            {
                /* ignore */
            }

            try
            {
                media?.Dispose();
            }
            catch
            {
                /* ignore */
            }
        });
    }

    private void OnEncounteredError()
    {
        Dispatcher.UIThread.Post(() =>
        {
            StopPlayback();
            ShowFallback("This video could not be played.");
        });
    }

    private void OnEndReached()
    {
        Dispatcher.UIThread.Post(() =>
        {
            isPlaying = false;
            UpdatePlayPauseIcon();
        });
    }

    private void UpdatePlayPauseIcon()
    {
        Attached.SetIcon(PlayPauseButton, isPlaying ? "fa-solid fa-pause" : "fa-solid fa-play");
    }

    private void ShowFallback(string message)
    {
        FallbackText.Text = message;
        FallbackBorder.IsVisible = true;
        PlayPauseButton.IsVisible = false;
    }
}
