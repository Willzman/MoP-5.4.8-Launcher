using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace MoPLauncher.Update;

// The server's player-facing news: REALM_NEWS.md on the server repository's
// Development branch. The "What's new" panel shows its newest entries;
// launcher release notes take over while an update is waiting, and when the
// news can't be read.
public static class RealmNews
{
    public const string DefaultUrl = "https://raw.githubusercontent.com/Willzman/SkyFire_548/Development/REALM_NEWS.md";
    public const string DefaultPageUrl = "https://github.com/Willzman/SkyFire_548/blob/Development/REALM_NEWS.md";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MoPLauncher", UpdateService.CurrentVersion.ToString()));
        return client;
    }

    // The news file's text, or null when there is none at that address.
    public static async Task<string?> GetAsync(string url, CancellationToken cancellationToken = default)
    {
        using var response = await Http.GetAsync(url, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    // The newest entries as Markdown: the "## date" headings and their "- "
    // lines, from the first heading on, until maxEntries lines are taken (a
    // date's lines are never cut in half). Null when the file has none.
    public static string? Newest(string markdown, int maxEntries = 8)
    {
        var text = new StringBuilder();
        var taken = 0;
        var started = false;

        foreach (var raw in markdown.Replace("\r", string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("## "))
            {
                if (taken >= maxEntries)
                    break;

                if (started)
                    text.AppendLine();
                text.AppendLine(line);
                started = true;
            }
            else if (started && (line.StartsWith("- ") || line.StartsWith("* ")))
            {
                text.AppendLine(line);
                taken++;
            }
        }

        return taken > 0 ? text.ToString().Trim() : null;
    }
}
