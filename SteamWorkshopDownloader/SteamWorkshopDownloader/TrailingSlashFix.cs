using GenHTTP.Api.Content;
using GenHTTP.Api.Protocol;
using GenHTTP.Modules.Redirects;

namespace SteamWorkshopDownloader;

public class TrailingSlashFix : IHandlerBuilder
{
    private readonly IHandlerBuilder _inner;
    private readonly string _targetPath;

    public TrailingSlashFix(IHandlerBuilder inner, string targetPath)
    {
        _inner = inner;
        _targetPath = targetPath;
    }

    public IHandler Build() => new FixHandler(_inner.Build(), _targetPath);

    private class FixHandler : IHandler
    {
        private readonly IHandler _inner;
        private readonly string _targetPath;

        public FixHandler(IHandler inner, string targetPath)
        {
            _inner = inner;
            _targetPath = targetPath;
        }

        public ValueTask PrepareAsync() => _inner.PrepareAsync();

        public ValueTask<IResponse?> HandleAsync(IRequest request)
        {
            if (request.Target.Path.ToString().Equals(_targetPath, StringComparison.OrdinalIgnoreCase))
            {
                return Redirect.To(_targetPath + "/").Build().HandleAsync(request);
            }
            
            return _inner.HandleAsync(request);
        }
    }
}