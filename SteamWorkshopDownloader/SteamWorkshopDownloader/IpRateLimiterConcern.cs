using System.Threading.RateLimiting;
using GenHTTP.Api.Content;
using GenHTTP.Api.Protocol;
using GenHTTP.Modules.IO;

namespace SteamWorkshopDownloader;

// 1. 实现 IConcernBuilder
public class IpRateLimiterConcernBuilder : IConcernBuilder
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public IpRateLimiterConcernBuilder(PartitionedRateLimiter<string> limiter)
    {
        _limiter = limiter;
    }

    // 新版接口：只需要接收一个 IHandler 参数
    public IConcern Build(IHandler content)
    {
        return new IpRateLimiterConcern(content, _limiter);
    }
}

// 2. 实现 IConcern
public class IpRateLimiterConcern : IConcern
{
    private readonly PartitionedRateLimiter<string> _limiter;
    
    // 必须实现 Content 属性
    public IHandler Content { get; }

    // 构造函数也对应简化
    public IpRateLimiterConcern(IHandler content, PartitionedRateLimiter<string> limiter)
    {
        Content = content;
        _limiter = limiter;
    }

    public ValueTask PrepareAsync() => Content.PrepareAsync();

    public async ValueTask<IResponse?> HandleAsync(IRequest request)
    {
        // 提取客户端 IP
        string clientIp = request.Client.IPAddress?.ToString() ?? "unknown";

        // 尝试为该 IP 获取 1 个令牌
        using var lease = _limiter.AttemptAcquire(clientIp);

        if (lease.IsAcquired)
        {
            // 获取成功，将请求传递给下层（真实的业务逻辑）
            return await Content.HandleAsync(request);
        }

        // 获取失败，返回 429
        return request.Respond()
            .Status(ResponseStatus.TooManyRequests)
            .Content($"Too Many Requests. Rate limit exceeded for IP: {clientIp}")
            .Build();
    }
}

// 3. 扩展方法
public static class IpRateLimiterExtensions
{
    public static T AddIpRateLimiting<T>(this T builder, PartitionedRateLimiter<string> limiter) where T : IHandlerBuilder<T>
    {
        builder.Add(new IpRateLimiterConcernBuilder(limiter));
        return builder;
    }
}