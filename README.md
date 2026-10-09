# MediaPager.Plugins.Subtitles.OpenSubtitles

Official **OpenSubtitles subtitle provider** plugin for MediaPager.
References the plugin SDK (`MediaPager.App.PluginContracts`) only.

Plugin id: `mediapager.subtitles.opensubtitles` · capability: `subtitles`

## What it does

- `ISubtitleProviderPlugin` — subtitle search and file retrieval.
  - **Search** needs only the API key.
  - **File retrieval** uses a token-cached login with the account username/password.
  - Returns **native** content (SRT); the host converts SRT→WebVTT for browser tracks
    (`MediaPager.App.Core.Subtitles.WebVtt`).
- `IPluginSettingsSchema` — data-driven settings.

## Settings (`plugins.opensubtitles.*`)

| Key | Type | Notes |
|---|---|---|
| `apiKey` | password, required, secret | OpenSubtitles API key (v3). |
| `username` | string, optional | Account username — required for file retrieval. |
| `password` | password, optional, secret | Account password — required for file retrieval. |

## Layout

- `OpenSubtitlesProviderPlugin.cs` — the plugin class.
- `OpenSubtitles/` — API client + DTOs (`OpenSubtitlesApi`, `OpenSubtitlesDtos`).

## Building

```sh
dotnet build MediaPager.Plugins.Subtitles.OpenSubtitles/MediaPager.Plugins.Subtitles.OpenSubtitles.csproj
```
