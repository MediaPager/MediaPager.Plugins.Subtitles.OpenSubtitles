using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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

internal sealed class OpenSubtitlesApiException(string message, HttpStatusCode? statusCode = null)
    : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>
/// OpenSubtitles API v1 wrapper. Search needs the API key; file retrieval also needs an account
/// login. Cached tokens are scoped to the credentials that produced them.
/// </summary>
internal sealed class OpenSubtitlesApi
{
    private const string BaseUrl = "https://api.opensubtitles.com/api/v1";
    private const string UserAgent = "MediaPager v1.0";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(20);

    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _token;
    private string? _tokenCredentialFingerprint;
    private DateTimeOffset _tokenAcquiredAt = DateTimeOffset.MinValue;

    public OpenSubtitlesApi(HttpClient? http = null) =>
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

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
        AddApiHeaders(request, apiKey);

        using var response = await SendAsync(request, cancellationToken, "subtitle search");
        if (!response.IsSuccessStatusCode)
            throw ApiFailure("subtitle search", response.StatusCode);

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var envelope = await JsonSerializer.DeserializeAsync<OsSearchEnvelope>(stream, _json, cancellationToken);
            if (envelope?.Data is null)
                throw new OpenSubtitlesApiException("OpenSubtitles returned an invalid subtitle search response.");

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
        catch (JsonException)
        {
            throw new OpenSubtitlesApiException("OpenSubtitles returned an invalid subtitle search response.");
        }
    }

    public async Task<string?> FetchSrtAsync(
        string apiKey,
        string? username,
        string? password,
        long fileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new OpenSubtitlesApiException("The OpenSubtitles API key is not configured.");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            throw new OpenSubtitlesApiException(
                "OpenSubtitles account username and password are required to download subtitle files.");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await GetTokenAsync(apiKey, username, password, cancellationToken);
            using var fileRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/download")
            {
                Content = JsonContent.Create(new { file_id = fileId }),
            };
            AddApiHeaders(fileRequest, apiKey);
            fileRequest.Headers.Add("Authorization", $"Bearer {token}");

            using var fileResponse = await SendAsync(fileRequest, cancellationToken, "subtitle download ticket");
            if (fileResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                await InvalidateTokenAsync(token, cancellationToken);
                if (attempt == 0) continue;
                throw new OpenSubtitlesApiException(
                    "OpenSubtitles rejected the refreshed account token. Check the account credentials and API key.",
                    fileResponse.StatusCode);
            }
            if (fileResponse.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!fileResponse.IsSuccessStatusCode)
                throw ApiFailure("subtitle download ticket", fileResponse.StatusCode);

            OsFileLinkEnvelope? fileLink;
            try
            {
                await using var responseStream = await fileResponse.Content.ReadAsStreamAsync(cancellationToken);
                fileLink = await JsonSerializer.DeserializeAsync<OsFileLinkEnvelope>(responseStream, _json, cancellationToken);
            }
            catch (JsonException)
            {
                throw new OpenSubtitlesApiException("OpenSubtitles returned an invalid subtitle download response.");
            }

            if (string.IsNullOrWhiteSpace(fileLink?.Link))
                throw new OpenSubtitlesApiException("OpenSubtitles did not provide a subtitle download link.");

            using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, fileLink.Link);
            using var downloadResponse = await SendAsync(downloadRequest, cancellationToken, "subtitle file download");
            if (downloadResponse.StatusCode == HttpStatusCode.NotFound)
                throw new OpenSubtitlesApiException(
                    "The OpenSubtitles download link expired or is no longer available. Search for the subtitle again.",
                    downloadResponse.StatusCode);
            if (!downloadResponse.IsSuccessStatusCode)
                throw ApiFailure("subtitle file download", downloadResponse.StatusCode);

            try
            {
                var content = await downloadResponse.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(content))
                    throw new OpenSubtitlesApiException("OpenSubtitles returned an empty subtitle file.");
                return content;
            }
            catch (HttpRequestException)
            {
                throw new OpenSubtitlesApiException("OpenSubtitles subtitle file could not be read.");
            }
        }

        throw new OpenSubtitlesApiException("OpenSubtitles rejected the refreshed login token.");
    }

    private async Task<string> GetTokenAsync(
        string apiKey,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var fingerprint = CredentialFingerprint(apiKey, username, password);
        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && _tokenCredentialFingerprint == fingerprint &&
                DateTimeOffset.UtcNow - _tokenAcquiredAt < TokenLifetime)
                return _token;

            _token = null;
            _tokenCredentialFingerprint = null;
            using var loginRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/login")
            {
                Content = JsonContent.Create(new { username, password }),
            };
            AddApiHeaders(loginRequest, apiKey);

            using var loginResponse = await SendAsync(loginRequest, cancellationToken, "account login");
            if (!loginResponse.IsSuccessStatusCode)
                throw LoginFailure(loginResponse.StatusCode);

            OsLoginEnvelope? login;
            try
            {
                await using var loginStream = await loginResponse.Content.ReadAsStreamAsync(cancellationToken);
                login = await JsonSerializer.DeserializeAsync<OsLoginEnvelope>(loginStream, _json, cancellationToken);
            }
            catch (JsonException)
            {
                throw new OpenSubtitlesApiException("OpenSubtitles returned an invalid account login response.");
            }

            if (string.IsNullOrWhiteSpace(login?.Token))
                throw new OpenSubtitlesApiException("OpenSubtitles did not return an account login token.");

            _token = login.Token;
            _tokenCredentialFingerprint = fingerprint;
            _tokenAcquiredAt = DateTimeOffset.UtcNow;
            return _token;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private async Task InvalidateTokenAsync(string token, CancellationToken cancellationToken)
    {
        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (!string.Equals(_token, token, StringComparison.Ordinal)) return;
            _token = null;
            _tokenCredentialFingerprint = null;
            _tokenAcquiredAt = DateTimeOffset.MinValue;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private static string CredentialFingerprint(string apiKey, string username, string password) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{apiKey}\0{username}\0{password}")));

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        string operation)
    {
        try
        {
            return await _http.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new OpenSubtitlesApiException($"OpenSubtitles {operation} request timed out.");
        }
        catch (HttpRequestException)
        {
            throw new OpenSubtitlesApiException($"Could not reach OpenSubtitles for the {operation} request.");
        }
    }

    private static void AddApiHeaders(HttpRequestMessage request, string apiKey)
    {
        request.Headers.Add("Api-Key", apiKey);
        request.Headers.Add("User-Agent", UserAgent);
    }

    private static OpenSubtitlesApiException LoginFailure(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? new OpenSubtitlesApiException(
                "OpenSubtitles rejected the account login. Check the account username/password and API key.", statusCode)
            : ApiFailure("account login", statusCode);

    private static OpenSubtitlesApiException ApiFailure(string operation, HttpStatusCode statusCode) =>
        new($"OpenSubtitles {operation} failed (HTTP {(int)statusCode}).", statusCode);

    private static string Query(Dictionary<string, string?> parameters) =>
        string.Join("&", parameters
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value!)}"));
}
