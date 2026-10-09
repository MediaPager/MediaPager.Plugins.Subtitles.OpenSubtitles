using MediaPager.App.PluginContracts;
using MediaPager.Plugins.Subtitles.OpenSubtitles.OpenSubtitles;

namespace MediaPager.Plugins.Subtitles.OpenSubtitles;

/// <summary>
/// Official OpenSubtitles subtitle provider: search by IMDb/TMDb id or title query, plus
/// subtitle files retrieved with account credentials. Settings live under plugins.opensubtitles.*
/// and flow through the SDK's IPluginSettingsStore; config is the fallback. Files return the
/// native SRT — the host converts it to WebVTT for browser tracks.
/// </summary>
public sealed class OpenSubtitlesProviderPlugin(IPluginSettingsStore settingsStore) :
    IMediaPagerPlugin, IPluginSettingsSchema, ISubtitleProviderPlugin
{
    public const string PluginKey = "opensubtitles";
    public const string ApiKeySetting = "apiKey";
    public const string UsernameSetting = "username";
    public const string PasswordSetting = "password";

    public const string SourceKey = "mediapager.subtitles.opensubtitles";

    public PluginDescriptor Descriptor { get; } = new(
        Id: SourceKey,
        Name: "OpenSubtitles",
        Version: "0.1.0",
        Author: "MediaPager",
        Description: "Official OpenSubtitles provider for movie and TV captions (API v1).");

    public IReadOnlyList<PluginSettingDefinition> Settings { get; } =
    [
        new PluginSettingDefinition(
            ApiKeySetting,
            "OpenSubtitles API key (v3)",
            PluginSettingType.Password,
            Required: true,
            Secret: true),
        new PluginSettingDefinition(
            UsernameSetting,
            "OpenSubtitles account username (required for file retrieval)",
            PluginSettingType.String),
        new PluginSettingDefinition(
            PasswordSetting,
            "OpenSubtitles account password (required for file retrieval)",
            PluginSettingType.Password,
            Secret: true),
    ];

    private readonly OpenSubtitlesApi _api = new();

    public async Task<IReadOnlyList<SubtitleHit>> SearchAsync(SubtitleRequest request, CancellationToken cancellationToken)
    {
        var hasImdb = !string.IsNullOrWhiteSpace(request.ImdbId);
        var hasTmdb = !string.IsNullOrWhiteSpace(request.TmdbId);
        var query = request.Query?.Trim();
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        if ((hasImdb || hasTmdb || hasQuery) is false)
            return [];
        if (query is { Length: > 200 })
            return [];
        if ((request.Season != null) != (request.Episode != null))
            return [];
        if (request.Season is < 1 || request.Episode is < 1)
            return [];

        var apiKey = await ApiKeyAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
            return [];

        var isEpisode = request.Season is not null;
        var hits = await _api.SearchAsync(
            apiKey,
            request.Language,
            DigitsOnly(request.ImdbId),
            request.TmdbId,
            query,
            isEpisode,
            request.Season,
            request.Episode,
            cancellationToken);

        return hits
            .Select(hit => new SubtitleHit(
                FileId: hit.FileId.ToString(),
                Language: hit.Language ?? request.Language,
                FileName: hit.FileName,
                Release: hit.Release,
                HearingImpaired: hit.HearingImpaired,
                Popularity: hit.Downloads))
            .ToList();
    }

    public async Task<SubtitleDocument?> FetchAsync(SubtitleFetchRequest request, CancellationToken cancellationToken)
    {
        if (!long.TryParse(request.FileId, out var fileId) || fileId <= 0)
            return null;

        var apiKey = await ApiKeyAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        var username = await settingsStore.GetAsync(PluginKey, UsernameSetting, cancellationToken);
        var password = await settingsStore.GetAsync(PluginKey, PasswordSetting, cancellationToken);

        var srt = await _api.FetchSrtAsync(apiKey, username, password, fileId, cancellationToken);

        return srt is null ? null : new SubtitleDocument(srt, SubtitleFormat.Srt);
    }

    private async Task<string?> ApiKeyAsync(CancellationToken cancellationToken) =>
        (await settingsStore.GetAsync(PluginKey, ApiKeySetting, cancellationToken))?.Trim();

    private static string? DigitsOnly(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new string(value.Where(char.IsDigit).ToArray());
}
