using System.Collections.Concurrent;
using System.Web;
using GenHTTP.Api.Protocol;
using GenHTTP.Modules.Reflection;
using GenHTTP.Modules.Webservices;

namespace SteamWorkshopDownloader;

public class DownloaderService
{
    public static ConcurrentDictionary<ulong, DateTime> LastDownloads { get; } = new();
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    
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

        if (LastDownloads.TryGetValue(extractedPubFileId, out var lastDownload) && DateTime.UtcNow - lastDownload < Interval)
        {
            return new PostQueueResponse(false, "already downloaded within a day");
        }
        LastDownloads[extractedPubFileId] = DateTime.UtcNow;
        var result = WorkshopDownloader.TryPushDownloadQueue(extractedPubFileId);
        return result
            ? new PostQueueResponse(true, "Successfully posted queue!")
            : new PostQueueResponse(false, "Failed to post queue! Too many queued items");
    }
}