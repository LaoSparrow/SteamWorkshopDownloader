using System.Collections.Concurrent;
using System.IO.Compression;
using System.Threading.Channels;
using DepotDownloader;

namespace SteamWorkshopDownloader;

public static class WorkshopDownloader
{
    private static readonly HashSet<uint> AllowedConsumerAppIds =
    [
        1281930,
        105600
    ];

    private static readonly HashSet<uint> ArchivedConsumerAppIds =
    [
        105600
    ];

    public record DownloadQueueEntry(ulong PubFileId, DateTime RequestDateTime);
    
    public static Task? DownloaderTask;
    public static CancellationTokenSource Cts = new();
    public static Channel<DownloadQueueEntry> DownloadQueue = Channel.CreateBounded<DownloadQueueEntry>(5);

    public static DownloadQueueEntry? CurrentDownloadEntry;
    
    public static ConcurrentDictionary<ulong, DateTime> LastDownloads { get; } = new();
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    
    public const long EXPECTED_FREE_SPACE = 5L * 1024 * 1024 * 1024; // 5GB

    public static void Init()
    {
        Cts.TryReset();
        DownloadQueue = Channel.CreateBounded<DownloadQueueEntry>(5);
        DownloaderTask = Task.Run(DownloaderLoop, Cts.Token);

        ContentDownloader.Config.MaxDownloads = 8;
        AccountSettingsStore.LoadFromFile("account.config");
    }

    public static async Task Shutdown()
    {
        await Cts.CancelAsync();
        DownloadQueue.Writer.Complete();
        if (DownloaderTask != null)
        {
            await DownloaderTask.WaitAsync(CancellationToken.None);
        }
        DownloaderTask = null;
    }
    
    public enum PushDownloadQueueResult
    {
        Success,
        QueueFull,
        ReachDownloadThreshold
    }

    public static PushDownloadQueueResult TryPushDownloadQueue(ulong pubFileId)
    {
        if (LastDownloads.TryGetValue(pubFileId, out var lastDownload) && DateTime.UtcNow - lastDownload < Interval)
        {
            return PushDownloadQueueResult.ReachDownloadThreshold;
        }
        
        var result = DownloadQueue.Writer.TryWrite(new DownloadQueueEntry(pubFileId, DateTime.UtcNow))
            ? PushDownloadQueueResult.Success
            : PushDownloadQueueResult.QueueFull;

        if (result == PushDownloadQueueResult.Success)
            LastDownloads[pubFileId] = DateTime.UtcNow;
        
        return result;
    }

    private static async Task DownloaderLoop()
    {
        Action<string> msgCallback = msg => Console.WriteLine((CurrentDownloadEntry, msg));
        ContentDownloader.MessageCallback += msgCallback;
        try
        {
            while (!Cts.Token.IsCancellationRequested &&
                   await DownloadQueue.Reader.WaitToReadAsync(Cts.Token))
            {
                var cleanedDirectories = Utils.CleanUpDirectory("./depots", EXPECTED_FREE_SPACE);
                LogCleanedDirectories(cleanedDirectories);
                RemoveFromLastDownloads(cleanedDirectories);
                
                // anonymous
                if (ContentDownloader.InitializeSteam3(null, null))
                {
                    try
                    {
                        while (DownloadQueue.Reader.TryRead(out var entry))
                        {
                            try
                            {
                                using var depotLocker = new DepotLocker(entry.PubFileId);
                                var installDirectory = $"./depots/{entry.PubFileId}";
                                if (Directory.Exists(installDirectory))
                                    Directory.SetLastAccessTimeUtc(installDirectory, DateTime.UtcNow);
                                
                                CurrentDownloadEntry = entry;
                                try
                                {
                                    if (await SteamPublishedFileApi.IsCollectionAsync(entry.PubFileId, Cts.Token))
                                    {
                                        Console.WriteLine((entry,
                                            new InvalidOperationException($"pubfile {entry.PubFileId} is a collection and collection downloads are not supported.")));
                                        continue;
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    LastDownloads.TryRemove(entry.PubFileId, out _);
                                    Console.WriteLine((entry, ex));
                                    continue;
                                }

                                uint appId;
                                try
                                {
                                    appId = await SteamPublishedFileApi.GetConsumerAppIdAsync(entry.PubFileId, Cts.Token);
                                    EnsureAllowedConsumerAppId(entry.PubFileId, appId);
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    LastDownloads.TryRemove(entry.PubFileId, out _);
                                    Console.WriteLine((entry, ex));
                                    continue;
                                }

                                // Do the true download operations
                                try
                                {
                                    ContentDownloader.Config.InstallDirectorySuffix = entry.PubFileId.ToString();
                                    await ContentDownloader.DownloadPubfileAsync(appId, entry.PubFileId)
                                        .ConfigureAwait(false);

                                    if (ArchivedConsumerAppIds.Contains(appId))
                                        CreatePubfileArchive(entry.PubFileId, installDirectory);
                                }
                                catch (Exception ex) when (ex is OperationCanceledException)
                                {
                                    LastDownloads.TryRemove(entry.PubFileId, out _);
                                    Console.WriteLine((entry, $"Workshop download cancelled: {ex}"));
                                    continue;
                                }
                                finally
                                {
                                    ContentDownloader.Config.InstallDirectorySuffix = string.Empty;
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                LastDownloads.TryRemove(entry.PubFileId, out _);
                                Console.WriteLine((entry, ex));
                            }
                            finally
                            {
                                CurrentDownloadEntry = null;
                            }
                        }
                    }
                    finally
                    {
                        ContentDownloader.ShutdownSteam3();
                    }
                }
                else
                {
                    Console.WriteLine(ValueTuple.Create(new Exception("Failed to login as anonymous")));
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Workshop downloader was canceled.");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ValueTuple.Create(ex));
        }
        finally
        {
            ContentDownloader.MessageCallback -= msgCallback;
            ContentDownloader.Config.InstallDirectorySuffix = string.Empty;
            Console.WriteLine("Workshop downloader has been shutdown.");
        }
    }

    private static void EnsureAllowedConsumerAppId(ulong pubFileId, uint appId)
    {
        if (!AllowedConsumerAppIds.Contains(appId))
            throw new InvalidOperationException($"consumer_app_id {appId} is not allowed for pubfile {pubFileId}.");
    }

    private static void LogCleanedDirectories(IEnumerable<string> cleanedDirectories)
    {
        foreach (var cleanedDirectory in cleanedDirectories)
        {
            Console.WriteLine(ValueTuple.Create($"Cleaned up depot directory: {cleanedDirectory}"));
        }
    }

    private static void CreatePubfileArchive(ulong pubFileId, string installDirectory)
    {
        if (!Directory.Exists(installDirectory))
            throw new DirectoryNotFoundException($"Install directory '{installDirectory}' was not found.");

        var installDirectoryFullPath = Path.GetFullPath(installDirectory);
        var archivePath = Path.Combine(installDirectoryFullPath, $"{pubFileId}.zip");
        if (File.Exists(archivePath))
            File.Delete(archivePath);

        var stagingArchivePath = Path.Combine(
            Directory.GetParent(installDirectoryFullPath)?.FullName ?? installDirectoryFullPath,
            $".{pubFileId}.{Guid.NewGuid():N}.zip");

        var rootDirectoryInfo = new DirectoryInfo(installDirectoryFullPath);
        var emptyDirectories = rootDirectoryInfo
            .EnumerateDirectories("*", SearchOption.AllDirectories)
            .Select(directory => new
            {
                Directory = directory,
                RelativePath = Path.GetRelativePath(installDirectoryFullPath, directory.FullName).Replace('\\', '/')
            })
            .Where(x => !ShouldSkipArchivePath(x.RelativePath))
            .Where(x => !x.Directory.EnumerateFileSystemInfos().Any())
            .ToList();

        var filesToArchive = rootDirectoryInfo
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Select(file => new
            {
                File = file,
                RelativePath = Path.GetRelativePath(installDirectoryFullPath, file.FullName).Replace('\\', '/')
            })
            .Where(x => !ShouldSkipArchivePath(x.RelativePath))
            .Where(x => !string.Equals(x.File.FullName, archivePath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        try
        {
            using (var archive = ZipFile.Open(stagingArchivePath, ZipArchiveMode.Create))
            {
                foreach (var directory in emptyDirectories)
                {
                    archive.CreateEntry(directory.RelativePath + "/");
                }

                foreach (var file in filesToArchive)
                {
                    archive.CreateEntryFromFile(file.File.FullName, file.RelativePath, CompressionLevel.Optimal);
                }
            }

            File.Move(stagingArchivePath, archivePath, true);
        }
        finally
        {
            if (File.Exists(stagingArchivePath))
                File.Delete(stagingArchivePath);
        }
    }

    private static bool ShouldSkipArchivePath(string relativePath)
    {
        var normalizedRelativePath = relativePath.Replace('\\', '/');
        return normalizedRelativePath.Equals(".DepotDownloader", StringComparison.OrdinalIgnoreCase) ||
               normalizedRelativePath.StartsWith(".DepotDownloader/", StringComparison.OrdinalIgnoreCase);
    }

    public static void RemoveFromLastDownloads(IEnumerable<string> pubFileIds)
    {
        foreach (var idStr in pubFileIds)
        {
            if (ulong.TryParse(idStr, out var id))
                LastDownloads.TryRemove(id, out _);
        }
    }
}
