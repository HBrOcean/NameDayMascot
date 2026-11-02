using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace NameDayMascot.Services;

/// <summary>检查 GitHub Releases 是否有新版本。所有失败都通过返回值表达，不抛异常。</summary>
public static class UpdateChecker
{
    private const string LatestApi = "https://api.github.com/repos/HBrOcean/NameDayMascot/releases/latest";
    private const string ReleasesPage = "https://github.com/HBrOcean/NameDayMascot/releases";

    /// <summary>当前程序版本，四段式（如 0.1.0.0）。</summary>
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(4) ?? "0.0.0.0";

    public static string ReleasesUrl => ReleasesPage;

    public readonly record struct Result(bool HasUpdate, string Current, string Latest, string Url, string? Error);

    /// <summary>查询最新 release 并与当前版本比较。</summary>
    public static async Task<Result> CheckAsync()
    {
        string current = CurrentVersion;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NameDayMascot", current));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            string json = await http.GetStringAsync(LatestApi).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            string tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            string url = doc.RootElement.TryGetProperty("html_url", out var u) ? (u.GetString() ?? ReleasesPage) : ReleasesPage;

            string latestRaw = tag.TrimStart('v', 'V');
            bool has = latestRaw.Length > 0 && Normalize(latestRaw) > Normalize(current);

            return new Result(has, current, latestRaw.Length > 0 ? latestRaw : tag, url, null);
        }
        catch (Exception ex)
        {
            return new Result(false, current, "", ReleasesPage, ex.Message);
        }
    }

    /// <summary>补齐到四段再比较，否则 "0.1.0" 会被当成小于 "0.1.0.0"（Revision -1 &lt; 0）。</summary>
    private static Version Normalize(string s)
    {
        var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (parts.Count < 4) parts.Add("0");
        return Version.TryParse(string.Join(".", parts.Take(4)), out var v) ? v : new Version(0, 0, 0, 0);
    }
}
