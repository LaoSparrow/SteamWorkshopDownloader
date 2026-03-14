using System.Collections.Concurrent;
using GenHTTP.Api.Content;
using GenHTTP.Api.Protocol;
using GenHTTP.Modules.IO;

namespace SteamWorkshopDownloader;

public class DepotLocker : IDisposable
{
    private ulong _pubFileId;

    public DepotLocker(ulong pubFileId)
    {
        _pubFileId = pubFileId;
        WorkshopDownloaderLockConcern.LockedPubfileIds.TryAdd(_pubFileId, 0);
    }

    public void Dispose()
    {
        WorkshopDownloaderLockConcern.LockedPubfileIds.TryRemove(_pubFileId, out _);
    }
}

// well, users might be downloading a mod before a download thread start....
// but the chance is low, so i dont care now...
// TODO: better locker
public class WorkshopDownloaderLockConcern(IHandler content) : IConcern
{
    public static ConcurrentDictionary<ulong, byte> LockedPubfileIds = []; // emulate set

    public static string ResponseLockedMessage = "Haste makes waste... / 心急吃不了热豆腐...";
    
    public IHandler Content { get; } = content;

    public ValueTask PrepareAsync() => Content.PrepareAsync();

    public ValueTask<IResponse?> HandleAsync(IRequest request)
    {
        var normalizedTarget = Normalize(request.Target.Path.ToString());
        var splitTarget = normalizedTarget.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        
        // GenHTTP blocks .. and . in url, so i think it is safe??
        // /depots/PUBFILE_ID
        if (splitTarget.Length < 2)
            return Content.HandleAsync(request); // not enough length, pass
        
        if (splitTarget[0] != "depots")
            return Content.HandleAsync(request); // not depots, pass
        
        if (!ulong.TryParse(splitTarget[1], out var pubFileId))
            return Content.HandleAsync(request); // cannot be parsed as ulong, pass

        if (!LockedPubfileIds.ContainsKey(pubFileId))
            return Content.HandleAsync(request); // not locked currently
        
        // locked!
        return new ValueTask<IResponse?>(request.Respond()
            .Status(ResponseStatus.Locked)
            .Type(FlexibleContentType.Get(ContentType.TextPlain))
            .Content(ResponseLockedMessage)
            .Build());
    }
    
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var normalized = path.Trim();
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        try
        {
            normalized = Uri.UnescapeDataString(normalized);
        }
        catch (UriFormatException)
        {
        }

        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        if (normalized.Length > 1)
        {
            normalized = normalized.TrimEnd('/');
        }

        return normalized;
    }
}

public class WorkshopDownloaderLockConcernBuilder : IConcernBuilder
{
    public IConcern Build(IHandler content) => new WorkshopDownloaderLockConcern(content);
}
