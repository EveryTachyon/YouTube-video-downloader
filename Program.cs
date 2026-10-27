using System.Diagnostics;
using System.Text.RegularExpressions;

string outputDirectory = Environment.GetEnvironmentVariable("DOWNLOAD_DIRECTORY")
    ?? "/downloads";
string downloaderExecutable = Environment.GetEnvironmentVariable("YTDLP_EXECUTABLE")
    ?? "yt-dlp";

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

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
        var startInfo = new ProcessStartInfo
        {
            FileName = downloaderExecutable,
            WorkingDirectory = jobDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        AddArgument(startInfo, "--no-playlist");
        AddArgument(startInfo, "--windows-filenames");
        AddArgument(startInfo, "--no-colors");
        AddArgument(startInfo, "--js-runtimes");
        AddArgument(startInfo, "deno");
        AddArgument(startInfo, "-o");
        AddArgument(startInfo, "%(title).200B [%(id)s].%(ext)s");

        if (request.Format == "mp4")
        {
            AddArgument(startInfo, "-f");
            AddArgument(startInfo, "bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/b");
            AddArgument(startInfo, "--merge-output-format");
            AddArgument(startInfo, "mp4");
        }
        else
        {
            AddArgument(startInfo, "-x");
            AddArgument(startInfo, "--audio-format");
            AddArgument(startInfo, "mp3");
            AddArgument(startInfo, "--audio-quality");
            AddArgument(startInfo, "0");
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

record DownloadRequest(string? Url, string? Format);
