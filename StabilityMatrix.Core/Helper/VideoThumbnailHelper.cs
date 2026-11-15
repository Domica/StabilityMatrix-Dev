using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace StabilityMatrix.Core.Helper;

/// <summary>
/// Generates poster-frame thumbnails for video files using ffmpeg.
/// <para>
/// The gallery image loaders (SkiaSharp / AsyncImageLoader) cannot decode video
/// containers, so a still frame is extracted once with ffmpeg and cached on disk.
/// When ffmpeg is not available the helper simply reports failure and the gallery
/// falls back to the video placeholder.
/// </para>
/// </summary>
public static class VideoThumbnailHelper
{
    /// <summary>Directory used to cache the generated thumbnails.</summary>
    public static string ThumbnailDirectory { get; } = Path.Combine(
        Path.GetTempPath(),
        "StabilityMatrix",
        "VideoThumbnails"
    );

    /// <summary>Limits how many ffmpeg processes run at the same time.</summary>
    private static readonly SemaphoreSlim FfmpegSemaphore = new(4, 4);

    private static readonly object FfmpegLock = new();

    private static string? cachedFfmpegPath;

    private static bool ffmpegResolved;

    /// <summary>
    /// Returns the deterministic cache location of the thumbnail for a video file.
    /// The file may not exist yet.
    /// </summary>
    public static string GetThumbnailPath(string videoPath)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(videoPath)))
        );

        return Path.Combine(ThumbnailDirectory, $"{hash}.jpg");
    }

    /// <summary>
    /// Returns the thumbnail path if it was already generated, otherwise <c>null</c>.
    /// This is a cheap check (file existence) and is safe to call on the UI thread.
    /// </summary>
    public static string? GetExistingThumbnailPath(string videoPath)
    {
        try
        {
            var path = GetThumbnailPath(videoPath);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts a poster frame for the given video if it does not exist yet.
    /// Returns the thumbnail path, or <c>null</c> when no thumbnail could be created.
    /// </summary>
    public static string? EnsureThumbnail(string videoPath)
    {
        var existing = GetExistingThumbnailPath(videoPath);
        if (existing is not null)
        {
            return existing;
        }

        var ffmpeg = ResolveFfmpegPath();
        if (ffmpeg is null)
        {
            return null;
        }

        var target = GetThumbnailPath(videoPath);

        try
        {
            Directory.CreateDirectory(ThumbnailDirectory);
        }
        catch
        {
            return null;
        }

        FfmpegSemaphore.Wait();

        try
        {
            // Another thread may have created it while we were waiting for the semaphore.
            if (File.Exists(target))
            {
                return target;
            }

            // Seek slightly into the video to avoid a black first frame,
            // then grab a single scaled frame as JPEG.
            var arguments =
                $"-y -loglevel quiet -ss 0.2 -i \"{Escape(videoPath)}\" "
                + $"-frames:v 1 -vf \"scale=480:-2\" -q:v 4 \"{Escape(target)}\"";

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(ffmpeg, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };

            process.Start();

            if (!process.WaitForExit(20000))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    /* ignore */
                }

                return null;
            }

            return File.Exists(target) ? target : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            FfmpegSemaphore.Release();
        }
    }

    /// <summary>Escapes a path for use inside an ffmpeg command line argument.</summary>
    private static string Escape(string path) => path.Replace("\"", "\\\"");

    /// <summary>
    /// Locates the ffmpeg executable. Returns <c>null</c> when it cannot be found,
    /// in which case no thumbnails are generated (the gallery still works).
    /// </summary>
    private static string? ResolveFfmpegPath()
    {
        lock (FfmpegLock)
        {
            if (ffmpegResolved)
            {
                return cachedFfmpegPath;
            }

            ffmpegResolved = true;
            cachedFfmpegPath = FindFfmpeg();
            return cachedFfmpegPath;
        }
    }

    private static string? FindFfmpeg()
    {
        // 1. Explicit override
        var explicitPath = Environment.GetEnvironmentVariable("STABILITY_MATRIX_FFMPEG");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        // 2. Directories on PATH
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var executableName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        foreach (
            var directory in pathVariable
                .Split(Path.PathSeparator)
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
        )
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                /* ignore invalid path entries */
            }
        }

        // 3. Common install locations
        string[] commonLocations = OperatingSystem.IsWindows()
            ?
            [
                @"F:\FFMPEG\ffmpeg.exe",
                @"C:\FFMPEG\ffmpeg.exe",
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            ]
            : ["/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg"];

        foreach (var candidate in commonLocations)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
