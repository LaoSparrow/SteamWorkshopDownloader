using System.IO.Compression;
using System.Reflection;
using GenHTTP.Api.Content;
using GenHTTP.Api.Protocol;
using StringContent = GenHTTP.Modules.IO.Strings.StringContent;

namespace SteamWorkshopDownloader;

public sealed class GzipBombContent : IResponseContent
{
    private readonly byte[] _payload;

    public ulong? Length => (ulong)_payload.Length;

    public GzipBombContent(byte[] payload)
    {
        _payload = payload;
    }

    public ValueTask WriteAsync(Stream target, uint bufferSize)
    {
        return target.WriteAsync(_payload);
    }

    public ValueTask<ulong?> CalculateChecksumAsync() => new((ulong?)_payload.GetHashCode());
}

public class HoneypotConcern : IConcern
{
    private static readonly FlexibleContentType HtmlUtf8ContentType = FlexibleContentType.Get(ContentType.TextHtml, "utf-8");

    private const string FakeWebShellPage = """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>WSH v2.3.4</title>
    <style>
        :root { color-scheme: dark; }
        body {
            margin: 0;
            min-height: 100vh;
            background: #0b0f10;
            color: #45ff70;
            font: 14px/1.4 ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
            display: flex;
            align-items: center;
            justify-content: center;
        }
        .shell {
            width: min(980px, 94vw);
            border: 1px solid #1f3325;
            box-shadow: 0 0 0 1px #0f1d13 inset, 0 20px 40px rgba(0,0,0,.4);
            background: #08100a;
        }
        .bar {
            padding: 8px 12px;
            background: #101a12;
            border-bottom: 1px solid #1f3325;
            color: #9cb3a3;
        }
        .screen {
            padding: 16px;
            white-space: pre-wrap;
            word-break: break-word;
        }
        .prompt { color: #d0ffd8; }
        .muted { color: #7aa184; }
        .err { color: #ff8f8f; }
        .cursor {
            display: inline-block;
            width: 8px;
            background: #45ff70;
            margin-left: 4px;
            animation: blink 1s step-end infinite;
        }
        @keyframes blink { 50% { opacity: 0; } }
        form { margin-top: 12px; display: flex; gap: 8px; align-items: center; }
        input {
            flex: 1;
            border: none;
            outline: none;
            color: inherit;
            background: transparent;
            font: inherit;
        }
    </style>
</head>
<body>
    <div class="shell">
        <div class="bar">root@backup-node-02: /var/www/.cache/.sys/</div>
        <div class="screen">
<span class="muted">[*] Web shell session established
[*] uid=0(root) gid=0(root) groups=0(root)
[*] Kernel: Linux backup-node-02 6.8.12 #1 SMP PREEMPT_DYNAMIC x86_64 GNU/Linux
[*] Filesystem mounted rw on /dev/nvme0n1p1
</span>

<span class="prompt">root@backup-node-02:/var/www/.cache/.sys#</span> ls -la
drwxr-xr-x  4 root root 4096 Mar 11 03:12 .
drwxr-xr-x 14 root root 4096 Mar 11 03:04 ..
-rwx------  1 root root  214 Mar 10 22:49 .bootstrap
-rw-------  1 root root 4096 Mar 11 03:12 .history
drwx------  2 root root 4096 Mar 10 23:18 dumps
drwx------  2 root root 4096 Mar 11 01:01 tmp

<span class="prompt">root@backup-node-02:/var/www/.cache/.sys#</span> cat /etc/shadow | head -n 3
<span class="err">Permission denied: TTY logging enabled</span>

<span class="prompt">root@backup-node-02:/var/www/.cache/.sys#</span><span class="cursor"></span>
            <form method="post" autocomplete="off">
                <label for="cmd">cmd:</label>
                <input id="cmd" name="cmd" type="text" spellcheck="false" autofocus>
            </form>
        </div>
    </div>
</body>
</html>
""";
    
    private static readonly byte[] GzipBombPayload = GenerateGzipBomb(1024L * 1024 * 1024); 

    public IHandler Content { get; }
    private readonly HashSet<string> _trapPaths;

    public HoneypotConcern(IHandler content, HashSet<string> trapPaths)
    {
        Content = content;
        _trapPaths = trapPaths;
    }

    public ValueTask PrepareAsync() => Content.PrepareAsync();

    public ValueTask<IResponse?> HandleAsync(IRequest request)
    {
        var normalizedPath = HoneypotPaths.Normalize(request.Target.Path.ToString());
        if (!_trapPaths.Contains(normalizedPath))
        {
            return Content.HandleAsync(request);
        }

        LogTrapHit(request, normalizedPath);

        // 检查扫描器是否支持并声明了接受 gzip 压缩
        bool acceptsGzip = request.Headers.TryGetValue("Accept-Encoding", out var enc) &&
                           enc.Contains("gzip", StringComparison.OrdinalIgnoreCase);

        var responseBuilder = request.Respond()
            .Status(ResponseStatus.Ok)
            .Type(HtmlUtf8ContentType)
            // 关键：加入 no-transform 防止 Cloudflare WAF/Auto-Minify 尝试解压该炸弹
            .Header("Cache-Control", "public, max-age=86400, no-transform")
            .Header("CDN-Cache-Control", "max-age=86400")
            .Header("Vary", "Accept-Encoding")
            .Header("X-Robots-Tag", "noindex, nofollow, noarchive");

        if (request.Method != RequestMethod.Head)
        {
            if (acceptsGzip && request.Method == RequestMethod.Get || request.Method == RequestMethod.Post)
            {
                responseBuilder = responseBuilder
                    .Encoding("gzip")
                    .Content(new GzipBombContent(GzipBombPayload));
            }
            else
            {
                // 如果对方不支持 gzip，回退到原有的伪造页面 (或者配合之前的 Tarpit 焦油坑)
                responseBuilder = responseBuilder.Content(new StringContent(FakeWebShellPage));
            }
        }

        return new ValueTask<IResponse?>(responseBuilder.Build());
    }

    private static byte[] GenerateGzipBomb(long targetUncompressedSize)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal))
        {
            byte[] chunk = new byte[81920]; // 80KB 的空数据块
            for (long i = 0; i < targetUncompressedSize; i += chunk.Length)
            {
                gz.Write(chunk, 0, chunk.Length);
            }
        }
        return ms.ToArray();
    }

    private static void LogTrapHit(IRequest request, string path)
    {
        var connectionIp = request.Client.IPAddress?.ToString() ?? "unknown";
        var cfConnectingIp = GetHeaderValue(request, "CF-Connecting-IP") ?? "-";
        var cfRay = GetHeaderValue(request, "CF-Ray") ?? "-";
        var userAgent = GetHeaderValue(request, "User-Agent") ?? "-";

        Console.WriteLine(
            $"[honeypot] ip={connectionIp} cfip={cfConnectingIp} {request.Method.RawMethod} {path} cfray={cfRay} ua=\"{userAgent}\"");
    }

    private static string? GetHeaderValue(IRequest request, string name)
    {
        return request.Headers.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }
}

public class HoneypotBuilder : IConcernBuilder
{
    private readonly HashSet<string> _trapPaths;

    public HoneypotBuilder()
    {
        var trapStream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"{nameof(SteamWorkshopDownloader)}.Resources.trapurls.txt");

        if (trapStream is null)
        {
            Console.WriteLine("Could not load trapurls.txt");
            _trapPaths = [];
            return;
        }

        using var trapStreamReader = new StreamReader(trapStream);
        _trapPaths = trapStreamReader.ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(HoneypotPaths.Normalize)
            .Where(HoneypotPaths.IsTrapPathAllowed)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public HoneypotBuilder(HashSet<string> trapPaths)
    {
        _trapPaths = trapPaths
            .Select(HoneypotPaths.Normalize)
            .Where(HoneypotPaths.IsTrapPathAllowed)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public IConcern Build(IHandler content) => new HoneypotConcern(content, _trapPaths);
}

internal static class HoneypotPaths
{
    private static readonly string[] ReservedPrefixes =
    [
        "/api/downloader",
        "/depots"
    ];

    private static readonly HashSet<string> ReservedExactPaths =
    [
        "/",
        "/index.html",
        "/api/downloader",
        "/api/downloader/queue",
        "/api/downloader/available-free-space",
        "/api/downloader/logs",
        "/depots"
    ];

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

    public static bool IsTrapPathAllowed(string path)
    {
        if (ReservedExactPaths.Contains(path))
        {
            return false;
        }

        return !ReservedPrefixes.Any(prefix =>
            path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
    }
}
