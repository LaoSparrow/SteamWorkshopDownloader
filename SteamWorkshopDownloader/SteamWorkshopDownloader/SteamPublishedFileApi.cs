using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

namespace SteamWorkshopDownloader;

public static class SteamPublishedFileApi
{
    private static readonly Uri CollectionDetailsUri = new("https://api.steampowered.com/ISteamRemoteStorage/GetCollectionDetails/v1/");
    private static readonly Uri PublishedFileDetailsUri = new("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/");
    private static readonly HttpClient HttpClient = new();
    private static readonly ConcurrentDictionary<ulong, bool> CollectionCache = new();
    private static readonly ConcurrentDictionary<ulong, uint> ConsumerAppIdCache = new();

    public static async Task<bool> IsCollectionAsync(ulong pubFileId, CancellationToken cancellationToken = default)
    {
        if (CollectionCache.TryGetValue(pubFileId, out var cachedIsCollection))
            return cachedIsCollection;

        using var requestContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["collectioncount"] = "1",
            ["publishedfileids[0]"] = pubFileId.ToString(),
        });

        using var response = await HttpClient.PostAsync(CollectionDetailsUri, requestContent, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("response", out var responseElement))
            throw new InvalidOperationException("Steam API response is missing the response object.");

        if (!responseElement.TryGetProperty("result", out var responseResultElement) ||
            responseResultElement.ValueKind != JsonValueKind.Number ||
            responseResultElement.GetInt32() != 1)
        {
            var responseResultCode = responseResultElement.ValueKind == JsonValueKind.Number
                ? responseResultElement.GetInt32()
                : -1;
            throw new InvalidOperationException($"Steam API returned result {responseResultCode} while retrieving collection details for pubfile {pubFileId}.");
        }

        if (!responseElement.TryGetProperty("collectiondetails", out var detailsElement) ||
            detailsElement.ValueKind != JsonValueKind.Array ||
            detailsElement.GetArrayLength() == 0)
        {
            CollectionCache[pubFileId] = false;
            return false;
        }

        var detailElement = detailsElement[0];
        if (!detailElement.TryGetProperty("result", out var resultElement) ||
            resultElement.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidOperationException($"Steam API returned an invalid collection result for pubfile {pubFileId}.");
        }

        var isCollection = resultElement.GetInt32() == 1;
        CollectionCache[pubFileId] = isCollection;
        return isCollection;
    }

    public static async Task<uint> GetConsumerAppIdAsync(ulong pubFileId, CancellationToken cancellationToken = default)
    {
        if (ConsumerAppIdCache.TryGetValue(pubFileId, out var cachedConsumerAppId))
            return cachedConsumerAppId;

        using var requestContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["itemcount"] = "1",
            ["publishedfileids[0]"] = pubFileId.ToString(),
        });

        using var response = await HttpClient.PostAsync(PublishedFileDetailsUri, requestContent, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("response", out var responseElement))
            throw new InvalidOperationException("Steam API response is missing the response object.");

        if (!responseElement.TryGetProperty("publishedfiledetails", out var detailsElement) ||
            detailsElement.ValueKind != JsonValueKind.Array ||
            detailsElement.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"Steam API returned no published file details for pubfile {pubFileId}.");
        }

        var detailElement = detailsElement[0];
        if (!detailElement.TryGetProperty("result", out var resultElement) ||
            resultElement.ValueKind != JsonValueKind.Number ||
            resultElement.GetInt32() != 1)
        {
            var resultCode = resultElement.ValueKind == JsonValueKind.Number
                ? resultElement.GetInt32()
                : -1;
            throw new InvalidOperationException($"Steam API returned result {resultCode} for pubfile {pubFileId}.");
        }

        if (!detailElement.TryGetProperty("consumer_app_id", out var consumerAppIdElement) ||
            consumerAppIdElement.ValueKind != JsonValueKind.Number ||
            !consumerAppIdElement.TryGetUInt32(out var consumerAppId) ||
            consumerAppId == 0)
        {
            throw new InvalidOperationException($"Steam API returned an invalid consumer_app_id for pubfile {pubFileId}.");
        }

        ConsumerAppIdCache[pubFileId] = consumerAppId;
        return consumerAppId;
    }
}
