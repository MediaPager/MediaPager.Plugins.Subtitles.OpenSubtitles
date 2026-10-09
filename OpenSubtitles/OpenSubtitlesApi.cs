using System.Net.Http.Json;
using System.Text.Json;
using MediaPager.App.PluginContracts;

namespace MediaPager.Plugins.Subtitles.OpenSubtitles.OpenSubtitles;

internal sealed record OsSubtitleHit(
    long FileId,
    string? FileName,
    string? Release,
    string? Language,
    bool HearingImpaired,
    long Downloads);

/// <summary>
/// OpenSubtitles API v1 wrapper. The search endpoint only needs the API key; the
/// file endpoint additionally requires a login (username/password) whose token is cached
/// for its ~20h lifetime.
/// </summary>
internal sealed class OpenSubtitlesApi
{
    private const string BaseUrl = "https://api.opensubtitles.com/api/v1";
    private const string UserAgent = "MediaPager v1.0";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    private string? _token;
    private DateTime _tokenAcquiredAt = DateTime.MinValue;

    public async Task<IReadOnlyList<OsSubtitleHit>> SearchAsync(
        string apiKey,
        string language,
        string? imdbId,
        string? tmdbId,
        string? query,
        bool isEpisode,
        int? season,
        int? episode,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["languages"] = language,
            ["imdb_id"] = imdbId,
            ["tmdb_id"] = tmdbId,
            ["query"] = query,
        };
        if (isEpisode)
        {
            parameters["type"] = "episode";
            parameters["season_number"] = season?.ToString();
            parameters["episode_number"] = episode?.ToString();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/subtitles?{Query(parameters)}");
        request.Headers.Add("Api-Key", apiKey);
        request.Headers.Add("User-Agent", UserAgent);

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return [];

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var envelope = await JsonSerializer.DeserializeAsync<OsSearchEnvelope>(stream, _json, cancellationToken);
        if (envelope?.Data is null)
            return [];

        return envelope.Data
            .Select(row => row.Attributes)
            .Where(attributes => attributes is { Files.Length: > 0 })
            .Select(attributes =>
            {
                var file = attributes.Files![0];
                return new OsSubtitleHit(
                    file.FileId,
                    file.FileName,
                    attributes.Release,
                    attributes.Language,
                    attributes.HearingImpaired ?? false,
                    attributes.DownloadCount ?? 0);
            })
            .ToList();
    }

    public async Task<string?> FetchSrtAsync(
        string apiKey,
        string? username,
        string? password,
        long fileId,
        CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(apiKey, username, password, cancellationToken);
        if (token is null)
            return null;

        using var fileRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/download")
        {
            Content = JsonContent.Create(new { file_id = fileId }),
        };
        fileRequest.Headers.Add("Api-Key", apiKey);
        fileRequest.Headers.Add("User-Agent", UserAgent);
        fileRequest.Headers.Add("Authorization", $"Bearer {token}");

        using var fileResponse = await _http.SendAsync(fileRequest, cancellationToken);
        if (!fileResponse.IsSuccessStatusCode)
            return null;

        await using var responseStream = await fileResponse.Content.ReadAsStreamAsync(cancellationToken);
        var fileLink = await JsonSerializer.DeserializeAsync<OsFileLinkEnvelope>(responseStream, _json, cancellationToken);
        if (string.IsNullOrWhiteSpace(fileLink?.Link))
            return null;

        try
        {
            return await _http.GetStringAsync(fileLink.Link, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> GetTokenAsync(
        string apiKey,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        if (_token is not null && DateTime.UtcNow - _tokenAcquiredAt < TimeSpan.FromHours(20))
            return _token;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return null;

        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/login")
        {
            Content = JsonContent.Create(new { username, password }),
        };
        loginRequest.Headers.Add("Api-Key", apiKey);
        loginRequest.Headers.Add("User-Agent", UserAgent);

        using var loginResponse = await _http.SendAsync(loginRequest, cancellationToken);
        if (!loginResponse.IsSuccessStatusCode)
            return null;

        await using var loginStream = await loginResponse.Content.ReadAsStreamAsync(cancellationToken);
        var login = await JsonSerializer.DeserializeAsync<OsLoginEnvelope>(loginStream, _json, cancellationToken);
        if (string.IsNullOrWhiteSpace(login?.Token))
            return null;

        _token = login.Token;
        _tokenAcquiredAt = DateTime.UtcNow;
        return _token;
    }

    private static string Query(Dictionary<string, string?> parameters) =>
        string.Join("&", parameters
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value!)}"));
}
