using System.Diagnostics;
using System.Globalization;

namespace Gatherum.Infrastructure.Analysis;

/// <summary>The two things a video has to be taken apart into before a model can make
/// anything of it: the audio track, and a handful of frames spread across its length.
/// Shelled out rather than bound, because a native binding would be a far heavier
/// dependency than the binary most hosts already carry.</summary>
public class Ffmpeg(string ffmpegPath)
{
    private string FfprobePath =>
        Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "",
            Path.GetFileName(ffmpegPath).Replace("ffmpeg", "ffprobe"));

    /// <summary>16 kHz mono WAV — what speech models want, and an order of magnitude
    /// smaller than the video it came out of, which matters when it is about to be
    /// base64'd into a request body.</summary>
    public async Task<byte[]> ExtractAudioAsync(string inputPath, CancellationToken ct)
    {
        var output = Path.Combine(Path.GetTempPath(), $"gatherum-audio-{Guid.NewGuid():N}.wav");
        try
        {
            await RunAsync(ffmpegPath,
                ["-nostdin", "-v", "error", "-y", "-i", inputPath,
                 "-vn", "-ac", "1", "-ar", "16000", "-f", "wav", output], ct);
            return await File.ReadAllBytesAsync(output, ct);
        }
        finally
        {
            File.Delete(output);
        }
    }

    /// <summary>Frames sampled evenly across the whole run, so a summary sees the end of
    /// a video and not four variations on its title card. A video whose duration ffprobe
    /// cannot read still yields its opening frames rather than nothing.</summary>
    public async Task<IReadOnlyList<byte[]>> SampleFramesAsync(string inputPath, int count,
        CancellationToken ct)
    {
        if (count <= 0)
            return [];

        var directory = Path.Combine(Path.GetTempPath(), $"gatherum-frames-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var duration = await DurationSecondsAsync(inputPath, ct);
            var pattern = Path.Combine(directory, "frame-%03d.jpg");
            string[] rate = duration is > 1
                ? ["-vf", FormattableString.Invariant($"fps={count}/{duration}")]
                : [];
            await RunAsync(ffmpegPath,
                ["-nostdin", "-v", "error", "-y", "-i", inputPath, .. rate,
                 "-frames:v", count.ToString(CultureInfo.InvariantCulture),
                 "-q:v", "4", pattern], ct);

            var frames = new List<byte[]>();
            foreach (var file in Directory.GetFiles(directory, "frame-*.jpg").Order())
                frames.Add(await File.ReadAllBytesAsync(file, ct));
            return frames;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task<double?> DurationSecondsAsync(string inputPath, CancellationToken ct)
    {
        try
        {
            var output = await RunAsync(FfprobePath,
                ["-v", "error", "-show_entries", "format=duration",
                 "-of", "default=nw=1:nk=1", inputPath], ct);
            return double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                out var seconds) ? seconds : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<string> RunAsync(string path, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {path}.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} exited {process.ExitCode}: {(await stderr).Trim()}");
        return await stdout;
    }
}
