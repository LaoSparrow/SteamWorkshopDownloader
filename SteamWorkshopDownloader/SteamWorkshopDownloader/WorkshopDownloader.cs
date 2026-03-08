using System.Threading.Channels;
using DepotDownloader;

namespace SteamWorkshopDownloader;

public static class WorkshopDownloader
{
    public record DownloadQueueEntry(ulong PubFileId, DateTime RequestDateTime);
    
    public static Task? DownloaderTask;
    public static CancellationTokenSource Cts = new();
    public static Channel<DownloadQueueEntry> DownloadQueue = Channel.CreateBounded<DownloadQueueEntry>(5);
    public static Channel<(DownloadQueueEntry?, Exception)> ExceptionQueue = Channel.CreateUnbounded<(DownloadQueueEntry?, Exception)>();
    public static Channel<(DownloadQueueEntry?, string)> MessageQueue = Channel.CreateUnbounded<(DownloadQueueEntry?, string)>();

    public static DownloadQueueEntry? CurrentDownloadEntry;

    public const uint APP_ID = 1281930;
    public const long EXPECTED_FREE_SPACE = 2L * 1024 * 1024 * 1024; // 2GB

    public static void Init()
    {
        Cts.TryReset();
        DownloadQueue = Channel.CreateBounded<DownloadQueueEntry>(5);
        DownloaderTask = Task.Run(DownloaderLoop, Cts.Token);

        ContentDownloader.Config.MaxDownloads = 8;
        AccountSettingsStore.LoadFromFile("account.config");
        
        // TODO: true handling
        Task.Run(async () =>
        {
            while (!Cts.IsCancellationRequested)
            {
                await foreach (var i in ExceptionQueue.Reader.ReadAllAsync(Cts.Token))
                {
                    Console.WriteLine(i.Item1);
                    Console.WriteLine(i.Item2);
                }
            }
        });
        Task.Run(async () =>
        {
            while (!Cts.IsCancellationRequested)
            {
                await foreach (var i in MessageQueue.Reader.ReadAllAsync(Cts.Token))
                {
                    Console.WriteLine(i);
                }
            }
        });
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

    public static bool TryPushDownloadQueue(ulong pubFileId)
    {
        return DownloadQueue.Writer.TryWrite(new DownloadQueueEntry(pubFileId, DateTime.UtcNow));
    } 

    private static async Task DownloaderLoop()
    {
        Action<string> msgCallback = msg => MessageQueue.Writer.TryWrite((CurrentDownloadEntry, msg));
        ContentDownloader.MessageCallback += msgCallback;
        try
        {
            while (!Cts.Token.IsCancellationRequested &&
                   await DownloadQueue.Reader.WaitToReadAsync(Cts.Token))
            {
                Utils.CleanUpDirectory($"./depots/{APP_ID}", EXPECTED_FREE_SPACE);
                
                // anonymous
                if (ContentDownloader.InitializeSteam3(null, null))
                {
                    try
                    {
                        while (DownloadQueue.Reader.TryRead(out var entry))
                        {
                            try
                            {
                                CurrentDownloadEntry = entry;
                                // Do the true download operations
                                ContentDownloader.Config.InstallDirectorySuffix = entry.PubFileId.ToString();
                                await ContentDownloader.DownloadPubfileAsync(APP_ID, entry.PubFileId);
                                ContentDownloader.Config.InstallDirectorySuffix = string.Empty;
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                await ExceptionQueue.Writer.WriteAsync((entry, ex));
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
                    await ExceptionQueue.Writer.WriteAsync((null, new Exception("Failed to login as anonymous")));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // no op
        }
        catch (Exception ex)
        {
            await ExceptionQueue.Writer.WriteAsync((null, ex));
        }
        finally
        {
            ContentDownloader.MessageCallback += msgCallback;
            ContentDownloader.Config.InstallDirectorySuffix = string.Empty;
        }
    }
}