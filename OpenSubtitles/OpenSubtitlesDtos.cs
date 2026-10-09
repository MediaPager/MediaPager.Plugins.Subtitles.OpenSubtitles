using System.Text.Json.Serialization;

namespace MediaPager.Plugins.Subtitles.OpenSubtitles.OpenSubtitles;

internal sealed record OsSearchEnvelope([property: JsonPropertyName("data")] OsSubtitleRow[]? Data);

internal sealed record OsSubtitleRow([property: JsonPropertyName("attributes")] OsAttributes Attributes);

internal sealed record OsAttributes(
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("download_count")] long? DownloadCount,
    [property: JsonPropertyName("hearing_impaired")] bool? HearingImpaired,
    [property: JsonPropertyName("release")] string? Release,
    [property: JsonPropertyName("files")] OsFile[]? Files);

internal sealed record OsFile(
    [property: JsonPropertyName("file_id")] long FileId,
    [property: JsonPropertyName("file_name")] string? FileName);

internal sealed record OsLoginEnvelope([property: JsonPropertyName("token")] string? Token);

internal sealed record OsFileLinkEnvelope([property: JsonPropertyName("link")] string? Link);
