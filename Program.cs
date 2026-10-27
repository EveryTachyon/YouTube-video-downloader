using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

string outputDirectory = Environment.GetEnvironmentVariable("DOWNLOAD_DIRECTORY")
    ?? "/downloads";
string downloaderExecutable = Environment.GetEnvironmentVariable("YTDLP_EXECUTABLE")
    ?? "yt-dlp";

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/info", async (InfoRequest request, HttpContext context) =>
{
    if (!TryGetVideoUrl(request.Url, out string videoUrl))
    {
        return Results.BadRequest(new { error = "Enter a valid YouTube video URL." });
    }

    var startInfo = CreateProcessStartInfo("--dump-single-json", "--skip-download",
        "--no-playlist", "--no-warnings", "--js-runtimes", "deno", videoUrl);

    try
    {
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return Results.Problem("The downloader could not be started.");
        }

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(
            context.RequestAborted);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(
            context.RequestAborted);
        await process.WaitForExitAsync(context.RequestAborted);
        string output = await outputTask;
        string error = await errorTask;

        if (process.ExitCode != 0)
        {
            return Results.Problem(string.IsNullOrWhiteSpace(error)
                ? "Video information could not be loaded."
                : error.Trim());
        }

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        string? title = root.TryGetProperty("title", out JsonElement titleValue)
            ? titleValue.GetString()
            : null;
        string? thumbnail = root.TryGetProperty("thumbnail", out JsonElement thumbnailValue)
            ? thumbnailValue.GetString()
            : null;
        string? duration = root.TryGetProperty("duration_string", out JsonElement durationValue)
            ? durationValue.GetString()
            : null;

        return Results.Ok(new
        {
            title = title ?? "YouTube video",
            thumbnail,
            duration,
            formats = new[]
            {
                new { type = "mp3", quality = "original", label = "Original audio" },
                new { type = "mp3", quality = "320", label = "320 Kbps" },
                new { type = "mp3", quality = "256", label = "256 Kbps" },
                new { type = "mp3", quality = "192", label = "192 Kbps" },
                new { type = "mp3", quality = "128", label = "128 Kbps" },
                new { type = "mp3", quality = "64", label = "64 Kbps" },
                new { type = "mp4", quality = "1080", label = "1080p video" },
                new { type = "mp4", quality = "720", label = "720p video" },
                new { type = "mp4", quality = "480", label = "480p video" }
            }
        });
    }
    catch (JsonException)
    {
        return Results.Problem("The downloader returned invalid video information.");
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
    }
});

app.MapPost("/api/download", async (DownloadRequest request, HttpContext context) =>
{
    if (!TryGetVideoUrl(request.Url, out string videoUrl))
    {
        return Results.BadRequest(new { error = "Enter a valid YouTube video URL." });
    }

    if (request.Format is not ("mp4" or "mp3"))
    {
        return Results.BadRequest(new { error = "Choose MP4 video or MP3 audio." });
    }

    Directory.CreateDirectory(outputDirectory);
    string jobDirectory = Path.Combine(outputDirectory, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(jobDirectory);

    try
    {
        var startInfo = CreateProcessStartInfo("--no-playlist", "--windows-filenames",
            "--no-colors", "--js-runtimes", "deno");
        startInfo.WorkingDirectory = jobDirectory;
        AddArgument(startInfo, "-o");
        AddArgument(startInfo, "%(title).200B [%(id)s].%(ext)s");

        if (request.Format == "mp4")
        {
            AddArgument(startInfo, "-f");
            AddArgument(startInfo, GetVideoFormat(request.Quality));
            AddArgument(startInfo, "--merge-output-format");
            AddArgument(startInfo, "mp4");
        }
        else
        {
            AddArgument(startInfo, "-x");
            AddArgument(startInfo, "--audio-format");
            AddArgument(startInfo, "mp3");
            AddArgument(startInfo, "--audio-quality");
            AddArgument(startInfo, GetAudioQuality(request.Quality));
        }

        AddArgument(startInfo, videoUrl);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return Results.Problem("The downloader could not be started.");
        }

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(
            context.RequestAborted);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(
            context.RequestAborted);
        await process.WaitForExitAsync(context.RequestAborted);
        _ = await outputTask;
        string standardError = await errorTask;

        if (process.ExitCode != 0)
        {
            string detail = standardError.Trim();
            Console.Error.WriteLine(detail);
            return Results.Problem(
                detail.Length > 0 ? detail : "The download failed.");
        }

        string? downloadedFile = Directory.EnumerateFiles(jobDirectory)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (downloadedFile is null)
        {
            return Results.Problem("The download completed without creating a file.");
        }

        string contentType = request.Format == "mp3"
            ? "audio/mpeg"
            : "video/mp4";

        context.Response.OnCompleted(() =>
        {
            try
            {
                if (Directory.Exists(jobDirectory))
                {
                    Directory.Delete(jobDirectory, recursive: true);
                }
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine(
                    $"Could not clean up download directory: {exception.Message}");
            }

            return Task.CompletedTask;
        });

        return Results.File(
            downloadedFile,
            contentType,
            Path.GetFileName(downloadedFile),
            enableRangeProcessing: true);
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception);
        return Results.Problem("The server could not complete the download.");
    }
});

app.Run();

static void AddArgument(ProcessStartInfo info, string argument)
{
    info.ArgumentList.Add(argument);
}

static ProcessStartInfo CreateProcessStartInfo(params string[] arguments)
{
    var info = new ProcessStartInfo
    {
        FileName = downloaderExecutable,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    foreach (string argument in arguments)
    {
        AddArgument(info, argument);
    }

    return info;
}

static string GetAudioQuality(string? quality) =>
    quality is "320" or "256" or "192" or "128" or "64" ? quality : "0";

static string GetVideoFormat(string? quality) =>
    quality switch
    {
        "1080" => "bv*[height<=1080][ext=mp4]+ba[ext=m4a]/b[height<=1080][ext=mp4]/b",
        "720" => "bv*[height<=720][ext=mp4]+ba[ext=m4a]/b[height<=720][ext=mp4]/b",
        "480" => "bv*[height<=480][ext=mp4]+ba[ext=m4a]/b[height<=480][ext=mp4]/b",
        _ => "bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/b"
    };

static bool TryGetVideoUrl(string? input, out string videoUrl)
{
    videoUrl = "";

    if (string.IsNullOrWhiteSpace(input) ||
        !Uri.TryCreate(input.Trim(), UriKind.Absolute, out Uri? uri) ||
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
            if (parts.Length == 2 && parts[0] == "v")
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
                (path[0] == "shorts" || path[0] == "embed" || path[0] == "live"))
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

record InfoRequest(string? Url);

record DownloadRequest(string? Url, string? Format, string? Quality);
