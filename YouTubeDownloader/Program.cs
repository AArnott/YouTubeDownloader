﻿using Microsoft.VisualStudio.Threading;
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
Argument<string[]> videosArgument = new("videoUrl", "The URL or ID of the YouTube video(s) to download.") { Arity = ArgumentArity.OneOrMore };
downloadCommand.AddArgument(videosArgument);
downloadCommand.SetHandler((videosArg, outputDirOption) => DownloadAsync(videosArg, outputDirOption, cts.Token), videosArgument, outputDirectoryOption);

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

async Task DownloadAsync(string[] videoUrlsOrIds, string? outputDir, CancellationToken cancellationToken)
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
            YouTubeVideo? video = await PickBestVideoAsync(youtube, videoUrls[i], cancellationToken);
            if (video is null)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Error:[/] No compatible video found for {videoUrls[i]}.");
                return;
            }

            videos[i] = video;
            AnsiConsole.MarkupLineInterpolated($"{videoUrls[i]}: [yellow]{video.Resolution.ToString(CultureInfo.CurrentCulture)}p {video.Format}[/]");
        }
    });

    await AnsiConsole.Progress()
        .Columns(
            new TaskDescriptionColumn(),
            new ProgressBarColumn(),
            new PercentageColumn(),
            new TransferSpeedColumn(),
            new RemainingTimeColumn(),
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
            downloadTasks[i] = CreateDownloadAsync(
                new Uri(video.Uri),
                targetPath,
                new Progress<Tuple<long, long>>((Tuple<long, long> v) =>
                {
                    task.MaxValue = v.Item2;
                    task.Value = v.Item1;
                }),
                cancellationToken);
        }

        await Task.WhenAll(downloadTasks);
    });
}

const long ChunkSize = 10 * 1024 * 1024;

static async Task<YouTubeVideo?> PickBestVideoAsync(YouTube youtube, string videoUri, CancellationToken cancellationToken)
{
    IEnumerable<YouTubeVideo> videos = await youtube.GetAllVideosAsync(videoUri);
    return (from video in videos
            where video.Format == VideoFormat.WebM
            orderby video.Resolution descending
            select video).FirstOrDefault();
}

static async Task CreateDownloadAsync(Uri uri, string filePath, IProgress<Tuple<long, long>> progress, CancellationToken cancellationToken)
{
    using HttpClient httpClient = new();

    long totalBytesCopied = 0L;
    long fileSize = await GetContentLengthAsync(httpClient, uri.AbsoluteUri) ?? throw new InvalidOperationException("Unable to determine total file size.");

    using FileStream output = new(filePath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
    int segmentCount = (int)Math.Ceiling(1.0 * fileSize / ChunkSize);
    for (int i = 0; i < segmentCount; i++)
    {
        long from = i * ChunkSize;
        long to = (i + 1) * ChunkSize - 1;
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(from, to);

        HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int bytesCopied;
            do
            {
                bytesCopied = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                output.Write(buffer, 0, bytesCopied);
                totalBytesCopied += bytesCopied;
                progress.Report(new Tuple<long, long>(totalBytesCopied, fileSize));
            } while (bytesCopied > 0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

static async Task<long?> GetContentLengthAsync(HttpClient httpClient, string requestUri)
{
    using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Head, requestUri);
    using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();
    return response.Content.Headers.ContentLength;
}