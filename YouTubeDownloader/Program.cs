using Microsoft.VisualStudio.Threading;
using Spectre.Console;
using System.Buffers;
using System.CommandLine;
using System.Globalization;
using System.Net.Http.Headers;
using VideoLibrary;

// Add this to your C# console app's Main method to give yourself
// a CancellationToken that is canceled when the user hits Ctrl+C.
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) =>
{
    cts.Cancel();
    e.Cancel = true;
};

// Set a SynchronizationContext to protect SpectreConsole (https://spectreconsole.net/best-practices).
SynchronizationContext.SetSynchronizationContext(new NonConcurrentSynchronizationContext(sticky: true));

Command downloadCommand = new("download", "Downloads one or more YouTube videos.");
Option<string> outputDirectoryOption = new(new[] { "-o", "--output" }, "The path to the directory that will contain the downloaded video(s).");
downloadCommand.AddOption(outputDirectoryOption);
Option<int> concurrentDownloadsOption = new(new[] { "-c", "--concurrent" }, () => 30, "The number of segments to split a video into for concurrent (faster!) download.");
downloadCommand.AddOption(concurrentDownloadsOption);
Option<bool> audioOnly = new(new[] { "-a", "--audio-only" }, "Download only the audio track.");
downloadCommand.AddOption(audioOnly);
Argument<string[]> videosArgument = new("videoUrl", "The URL or ID of the YouTube video(s) to download.") { Arity = ArgumentArity.OneOrMore };
downloadCommand.AddArgument(videosArgument);
downloadCommand.SetHandler(
    (videosArg, outputDirOption, segmentCount, audioOnly) => DownloadAsync(videosArg, outputDirOption, segmentCount, audioOnly, cts.Token),
    videosArgument,
    outputDirectoryOption,
    concurrentDownloadsOption,
    audioOnly);

RootCommand rootCommand = new();
rootCommand.AddCommand(downloadCommand);

try
{
    return await rootCommand.InvokeAsync(args);
}
catch (Exception ex)
{
    AnsiConsole.WriteException(ex);
    return 1;
}

async Task DownloadAsync(string[] videoUrlsOrIds, string? outputDir, int segmentCount, bool audioOnly, CancellationToken cancellationToken)
{
    string[] videoUrls = new string[videoUrlsOrIds.Length];
    for (int i = 0; i < videoUrls.Length; i++)
    {
        videoUrls[i] = Uri.TryCreate(videoUrlsOrIds[i], UriKind.Absolute, out _) ? videoUrlsOrIds[i] : $"https://www.youtube.com/watch?v={videoUrlsOrIds[i]}";
    }

    YouTube youtube = YouTube.Default;
    YouTubeVideo[] videos = new YouTubeVideo[videoUrls.Length];

    await AnsiConsole.Status()
        .StartAsync("Preparing video download...", async ctx =>
    {
        ctx.Status("Downloading video metadata");

        for (int i = 0; i < videoUrls.Length; i++)
        {
            IEnumerable<YouTubeVideo> candidates = await youtube.GetAllVideosAsync(videoUrls[i]);
            YouTubeVideo? video = PickBestVideo(candidates, audioOnly, cancellationToken);
            if (video is null)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Error:[/] No compatible video found for {videoUrls[i]}.");
                return;
            }

            videos[i] = video;
            AnsiConsole.MarkupLineInterpolated($"{videoUrls[i]}: [yellow]{video.Resolution.ToString(CultureInfo.CurrentCulture)}p {video.Format} {video.AudioBitrate} {video.AudioFormat}[/] [blue]{video.Title}[/]");
        }
    });

    await AnsiConsole.Progress()
        .Columns(
            new TaskDescriptionColumn() { Alignment = Justify.Left },
            new ProgressBarColumn(),
            new DownloadedColumn(),
            new PercentageColumn(),
            new TransferSpeedColumn(),
            new RemainingTimeColumn(),
            new ElapsedTimeColumn(),
            new SpinnerColumn())
        .HideCompleted(false)
        .StartAsync(async ctx =>
    {
        ProgressTask[] progressTasks = new ProgressTask[videoUrls.Length];
        Task[] downloadTasks = new Task[videoUrls.Length];
        for (int i = 0; i < videoUrls.Length; i++)
        {
            progressTasks[i] = ctx.AddTask($"[green]{Markup.Escape(videos[i].Title)}[/]");
        }

        outputDir ??= Environment.CurrentDirectory;
        AnsiConsole.MarkupLineInterpolated($"Will download to [yellow]{outputDir}[/]");

        for (int i = 0; i < videos.Length; i++)
        {
            Video video = videos[i];
            ProgressTask task = progressTasks[i];
            string targetPath = Path.Combine(outputDir, videos[i].FullName);
            if (string.IsNullOrEmpty(videos[i].FileExtension) && audioOnly)
            {
                targetPath += $".{videos[i].AudioFormat.ToString()?.ToLowerInvariant()}";
            }

            downloadTasks[i] = CreateDownloadAsync(
                new Uri(video.Uri),
                segmentCount,
                targetPath,
                new Progress<(long Copied, long Total)>(v =>
                {
                    task.Value = v.Copied;
                    task.MaxValue = v.Total;
                }),
                cancellationToken);
        }

        await Task.WhenAll(downloadTasks);
    });
}

static YouTubeVideo? PickBestVideo(IEnumerable<YouTubeVideo> videos, bool audioOnly, CancellationToken cancellationToken)
{
    IOrderedEnumerable<YouTubeVideo> query = audioOnly
        ? from video in videos
          where video.AudioBitrate > 0 && video.Resolution == -1
          orderby video.AudioBitrate descending
          select video
        : from video in videos
          where video.Format != VideoFormat.Unknown && video.AudioBitrate > 0
          orderby video.Resolution descending, video.AudioBitrate descending
          select video;
    return query.FirstOrDefault();
}

static async Task CreateDownloadAsync(Uri uri, int segmentCount, string filePath, IProgress<(long Copied, long Total)> progress, CancellationToken cancellationToken)
{
    try
    {
        using HttpClient httpClient = new();

        long totalBytesCopied = 0L;
        long fileSize = await GetContentLengthAsync(httpClient, uri.AbsoluteUri) ?? throw new InvalidOperationException("Unable to determine total file size.");

        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? throw new ArgumentException("Bad file path.", nameof(filePath)));
        using FileStream output = new(filePath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        output.SetLength(fileSize);

        AsyncSemaphore fileWriterContext = new(1);

        async Task WriteBytesAsync(long position, ReadOnlyMemory<byte> buffer)
        {
            using (await fileWriterContext.EnterAsync(cancellationToken))
            {
                output.Position = position;
                await output.WriteAsync(buffer, cancellationToken);
                totalBytesCopied += buffer.Length;
                progress.Report((totalBytesCopied, fileSize));
            }
        }

        long ChunkSize = (long)Math.Ceiling((double)fileSize / segmentCount);
        Task[] segmentDownloadTasks = new Task[segmentCount];
        for (int i = 0; i < segmentCount; i++)
        {
            segmentDownloadTasks[i] = DownloadSegmentAsync(i);
        }

        await Task.WhenAll(segmentDownloadTasks);
        AnsiConsole.MarkupLineInterpolated($"Completed download of [green]{filePath}[/]");

        async Task DownloadSegmentAsync(int segment)
        {
            long from = segment * ChunkSize;
            long to = (segment + 1) * ChunkSize - 1;
            long position = from;

            HttpResponseMessage? response = null;
            try
            {
                while (true)
                {
                    using HttpRequestMessage request = new(HttpMethod.Get, uri);
                    request.Headers.Range = new RangeHeaderValue(from, to);
                    response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized)
                    {
                        AnsiConsole.MarkupLineInterpolated($"[red]YouTube is throttling {uri}. Pausing...[/] If this happens often, try reducing the concurrency level.");
                        await Task.Delay(5000);
                        response?.Dispose();
                        continue;
                    }

                    response.EnsureSuccessStatusCode();
                    break;
                }

                using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    int bytesCopied;
                    do
                    {
                        bytesCopied = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                        await WriteBytesAsync(position, buffer.AsMemory(0, bytesCopied));
                        position += bytesCopied;
                    } while (bytesCopied > 0);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            finally
            {
                response?.Dispose();
            }
        }
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        AnsiConsole.MarkupLineInterpolated($"[red]An error occurred[/] while downloading {Path.GetFileName(filePath)}.");
        AnsiConsole.WriteException(ex);
        throw;
    }
}

static async Task<long?> GetContentLengthAsync(HttpClient httpClient, string requestUri)
{
    using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Head, requestUri);
    using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();
    return response.Content.Headers.ContentLength;
}
