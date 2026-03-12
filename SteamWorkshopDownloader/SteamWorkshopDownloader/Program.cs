using System.Threading.RateLimiting;
using GenHTTP.Engine.Internal;
using GenHTTP.Modules.Practices;
using GenHTTP.Modules.OpenApi;
using GenHTTP.Modules.ApiBrowsing;
using GenHTTP.Modules.DirectoryBrowsing;
using GenHTTP.Modules.IO;
using GenHTTP.Modules.Layouting;
using GenHTTP.Modules.StaticWebsites;
using GenHTTP.Modules.Webservices;
using SteamWorkshopDownloader;

if (!Directory.Exists("./depots"))
    Directory.CreateDirectory("./depots");
WorkshopDownloader.Init();

// 创建按 IP 隔离的限流器
var ipLimiter = PartitionedRateLimiter.Create<string, string>(ip =>
{
    return RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: ip,
        factory: partition => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromSeconds(60),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        });
});

var app = Layout.Create()
    .Add(StaticWebsite.From(ResourceTree.FromAssembly("Content")))
    .Add("depots", new TrailingSlashFix(Listing.From(ResourceTree.FromDirectory("./depots")), "/depots"))
    .AddService<DownloaderService>("/api/downloader")
    .Add(new WorkshopDownloaderLockConcernBuilder())
    .Add(new HoneypotBuilder())
#if DEBUG
    .AddOpenApi()
    .AddSwaggerUi()
#endif
    .AddIpRateLimiting(ipLimiter);

return await Host.Create()
    .Handler(app)
    .Defaults()
    .Console()
    .Port(80)
#if DEBUG
    .Development()
#endif
    .RunAsync();