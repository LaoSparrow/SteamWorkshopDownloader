using System.Collections.Concurrent;
using System.Web;
using GenHTTP.Api.Protocol;
using GenHTTP.Modules.Reflection;
using GenHTTP.Modules.Webservices;

namespace SteamWorkshopDownloader;

public class DownloaderService
{
    public record PostQueueRequest(string UrlOrId);
    public record PostQueueResponse(bool IsSuccess, string Message);
    
    [ResourceMethod(RequestMethod.Post, "queue")]
    public PostQueueResponse PostQueue([FromBody] PostQueueRequest request)
    {
        var extractedPubFileId = ulong.MaxValue;
        if (Uri.TryCreate(request.UrlOrId, UriKind.Absolute, out var uri))
        {
            if (uri.Host != "steamcommunity.com")
                return new PostQueueResponse(false, "not a url to steamcommunity.com");
            if (uri.Scheme != "http" && uri.Scheme != "https")
                return new PostQueueResponse(false, "not a url to steamcommunity.com");
            
            var queryParams = HttpUtility.ParseQueryString(uri.Query);
            var mightBeId = queryParams["id"];
            if (string.IsNullOrEmpty(mightBeId))
                return new PostQueueResponse(false, "id param is not a id");
            if (!ulong.TryParse(mightBeId, out extractedPubFileId))
                return new PostQueueResponse(false, "id param is not a id");
        }
        if (ulong.TryParse(request.UrlOrId, out var pubFileId))
        {
            extractedPubFileId = pubFileId;
        }
        if (extractedPubFileId == ulong.MaxValue)
        {
            return new PostQueueResponse(false, "not a valid URL or ID");
        }
        
        var result = WorkshopDownloader.TryPushDownloadQueue(extractedPubFileId);
        return result switch
        {
            WorkshopDownloader.PushDownloadQueueResult.Success =>
                new PostQueueResponse(true, "Successfully posted queue!"),
            WorkshopDownloader.PushDownloadQueueResult.QueueFull =>
                new PostQueueResponse(false, "Failed to post queue! Too many queued items"),
            WorkshopDownloader.PushDownloadQueueResult.ReachDownloadThreshold =>
                new PostQueueResponse(false, "already downloaded within a day"),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    public record AvailableFreeSpaceResponse(long AvailableFreeSpace, long TotalSize);

    [ResourceMethod(RequestMethod.Get, "available-free-space")]
    public AvailableFreeSpaceResponse GetAvailableFreeSpace()
    {
        var (availableSpace, totalSize) = Utils.GetDriveAvailableFreeSpace("./depots");
        return new AvailableFreeSpaceResponse(availableSpace, totalSize);
    }

    [ResourceMethod(RequestMethod.Get, "logs")]
    public string GetLogs()
    {
        return Utils.ReadTailLines("./log.txt");
    }
}