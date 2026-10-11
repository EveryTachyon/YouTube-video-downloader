
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;

const string OutputDirectory = "/downloads";

Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine("       YouTube Downloader");
Console.WriteLine();
Console.WriteLine("Choose a download from:");
Console.WriteLine("  1. Video (MP4)");
Console.WriteLine("  2. Audio (MP3)");
Console.Write("Selection: ");

string? choice = Console.ReadLine()?.Trim();

if (choice is not ("1" or "2"))
{
    Console.WriteLine("Invalid selection. Enter 1 or 2.");
    return;
}

Console.WriteLine();
Console.Write("Paste a YouTube URL: ");

string? input = Console.ReadLine()?.Trim();

if (!TryGetVideoUrl(input, out string videoUrl))
{
    Console.WriteLine("Invalid URL. Enter a valid YouTube video link.");
    return;
}

if (!Directory.Exists(OutputDirectory))
{
    Console.WriteLine($"Output directory does not exist: {OutputDirectory}");
    return;
}

var startInfo = new ProcessStartInfo
{
    FileName = "yt-dlp",
    WorkingDirectory = OutputDirectory,
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    CreateNoWindow = true
};


AddArgument(startInfo, "--no-playlist");
AddArgument(startInfo, "--windows-filenames");
AddArgument(startInfo, "--no-colors");
AddArgument(startInfo, "--newline");
AddArgument(startInfo, "--js-runtimes");
AddArgument(startInfo, "deno");

AddArgument(startInfo, "-o");
AddArgument(startInfo, "%(title).200B [%(id)s].%(ext)s");


AddArgument(startInfo, "--progress-template");
AddArgument(startInfo,
    "download:PROGRESS:%(info.ext)s|%(progress._percent_str)s|" +
    "%(progress._speed_str)s|%(progress._eta_str)s");

if (choice == "1")
{
    Console.WriteLine();
    Console.WriteLine("Mode: MP4 video");
    

    AddArgument(startInfo, "-f");
    AddArgument(startInfo,
        "bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/b");

    AddArgument(startInfo, "--merge-output-format");
    AddArgument(startInfo, "mp4");
}
else
{
    Console.WriteLine();
    Console.WriteLine("Mode: MP3 audio");

    AddArgument(startInfo, "-x");
    AddArgument(startInfo, "--audio-format");
    AddArgument(startInfo, "mp3");
    AddArgument(startInfo, "--audio-quality");
    AddArgument(startInfo, "0");
}

AddArgument(startInfo, videoUrl);

var stopwatch = Stopwatch.StartNew();

try
{
    using var process = Process.Start(startInfo);

    if (process is null)
    {
        Console.WriteLine("Could not start yt-dlp.");
        return;
    }

    var progress = new DownloadProgress();

    Task stdoutTask = ReadOutputAsync(
        process.StandardOutput, progress, false);

    Task stderrTask = ReadOutputAsync(
        process.StandardError, progress, true);

    await process.WaitForExitAsync();
    await Task.WhenAll(stdoutTask, stderrTask);

    stopwatch.Stop();

    Console.WriteLine();

    if (process.ExitCode == 0)
    {
        Console.WriteLine("Download completed successfully.");
        Console.WriteLine($"Elapsed time: {stopwatch.Elapsed:hh\\:mm\\:ss}");
        Console.WriteLine($"Saved in: {OutputDirectory}");
    }
    else
    {
        Console.WriteLine($"Download failed (exit code {process.ExitCode}).");
        Console.WriteLine("Check the error messages above.");
    }
}
catch (Exception ex)
{
    stopwatch.Stop();
    Console.WriteLine($"Error: {ex.Message}");
}

static void AddArgument(ProcessStartInfo info, string argument)
{
    info.ArgumentList.Add(argument);
}

static bool TryGetVideoUrl(string? input, out string videoUrl)
{
    videoUrl = "";

    if (string.IsNullOrWhiteSpace(input) ||
        !Uri.TryCreate(input, UriKind.Absolute, out Uri? uri) ||
        uri.Scheme != Uri.UriSchemeHttps)
    {
        return false;
    }

    string host = uri.Host.ToLowerInvariant();
    string? videoId = null;

    if (host == "youtu.be")
    {
        videoId = uri.AbsolutePath.Trim('/');
    }
    else if (host == "youtube.com" ||
             host.EndsWith(".youtube.com", StringComparison.Ordinal))
    {
        var query = uri.Query.TrimStart('?');

        foreach (string parameter in query.Split(
                     '&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = parameter.Split('=', 2);

            if (parts.Length == 2 &&
                parts[0] == "v")
            {
                videoId = Uri.UnescapeDataString(parts[1]);
                break;
            }
        }

        if (videoId is null)
        {
            string[] path = uri.AbsolutePath.Split(
                '/', StringSplitOptions.RemoveEmptyEntries);

            if (path.Length >= 2 &&
                (path[0] == "shorts" ||
                 path[0] == "embed" ||
                 path[0] == "live"))
            {
                videoId = path[1];
            }
        }
    }

    if (videoId is null ||
        !Regex.IsMatch(videoId, @"^[A-Za-z0-9_-]{11}$"))
    {
        return false;
    }

    videoUrl = $"https://www.youtube.com/watch?v={videoId}";
    return true;
}

static async Task ReadOutputAsync(
    StreamReader reader,
    DownloadProgress progress,
    bool isError)
{
    string? line;

    while ((line = await reader.ReadLineAsync()) is not null)
    {
        if (!isError && line.StartsWith(
                "PROGRESS:", StringComparison.Ordinal))
        {
            progress.ProcessLine(line);
            continue;
        }

        // Keep normal yt-dlp messages and errors visible.
        if (!string.IsNullOrWhiteSpace(line))
        {
            Console.WriteLine(line);
        }
    }
}

sealed class DownloadProgress
{
    private int lastMilestone;
    private double lastPercent;
    private string? lastFormat;

    public void ProcessLine(string line)
    {
        string data = line["PROGRESS:".Length..];
        string[] parts = data.Split('|');

        if (parts.Length < 4)
            return;

        Match match = Regex.Match(parts[1], @"(\d+(?:\.\d+)?)");

        if (!match.Success ||
            !double.TryParse(
                match.Groups[1].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double percent))
        {
            return;
        }

        percent = Math.Clamp(percent, 0, 100);

        if (!string.Equals(lastFormat, parts[0], StringComparison.Ordinal) ||
            percent < lastPercent)
        {
            lastMilestone = 0;
            lastFormat = parts[0];
        }

        int milestone = (int)(percent / 5) * 5;

        if (percent >= 100)
            milestone = 100;

        lastPercent = percent;

        if (milestone <= lastMilestone)
            return;

        Console.Write($"Download progress ({parts[0]}): {milestone}%");
        Console.Write($" (actual {percent:F1}%)");

        if (!string.IsNullOrWhiteSpace(parts[2]) && parts[2] != "NA")
            Console.Write($" | Speed: {parts[2]}");

        if (!string.IsNullOrWhiteSpace(parts[3]) && parts[3] != "NA")
            Console.Write($" | ETA: {parts[3]}");

        Console.WriteLine();

        lastMilestone = milestone;
    }
}
