// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;

namespace LinFan.App.Services;

/// <summary>
/// Prüft die GitHub-<i>latest-release</i>-API (<c>api.github.com/repos/{slug}/releases/latest</c>) und meldet eine
/// neuere Version. Nativ über <see cref="HttpClient"/> (keine zusätzliche Abhängigkeit). <b>Wirft nie</b>: jeder
/// Fehler — offline, Timeout, HTTP-Fehler (404 vor dem ersten Release, 403 Rate-Limit), kaputtes JSON — ergibt
/// <c>null</c>, damit der Update-Hinweis rein additiv ist und nie nervt.
/// </summary>
public sealed class UpdateCheckService : IUpdateCheckService
{
    /// <summary>
    /// Ziel-Repo (<c>owner/name</c>): der öffentliche GitHub-Mirror. Vor dem ersten Release liefert
    /// <c>/releases/latest</c> 404 → still.
    /// </summary>
    public const string DefaultRepoSlug = "Patrick-mpy/linfan";

    private readonly HttpClient _http;
    private readonly string _slug;

    /// <param name="http">Injizierbar für Tests; Standard ist ein eigener Client mit kurzem Timeout.</param>
    /// <param name="repoSlug">Ziel-Repo; Standard <see cref="DefaultRepoSlug"/>.</param>
    public UpdateCheckService(HttpClient? http = null, string? repoSlug = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _slug = repoSlug ?? DefaultRepoSlug;

        // GitHub verlangt einen User-Agent; ohne ihn kommt 403 zurück. Accept-Header ist gute Praxis.
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("LinFan-UpdateCheck");
        if (!_http.DefaultRequestHeaders.Accept.Any())
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<UpdateInfo?> CheckAsync(SemVer current, CancellationToken ct = default)
    {
        try
        {
            string url = $"https://api.github.com/repos/{_slug}/releases/latest";
            using HttpResponseMessage resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null; // 404 (kein Release), 403 (Rate-Limit), 5xx … → kein Hinweis

            await using Stream stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tag_name", out JsonElement tag)
                || tag.ValueKind != JsonValueKind.String)
                return null;

            if (!SemVer.TryParse(tag.GetString(), out SemVer latest) || latest.CompareTo(current) <= 0)
                return null; // nicht parsbar ODER nicht neuer → nichts zu melden

            string releaseUrl =
                root.TryGetProperty("html_url", out JsonElement html) && html.ValueKind == JsonValueKind.String
                    ? html.GetString()!
                    : $"https://github.com/{_slug}/releases/latest";

            return new UpdateInfo(latest.ToString(), releaseUrl);
        }
        catch
        {
            return null; // Netz-/Parse-/Abbruchfehler → still
        }
    }
}
