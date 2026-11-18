using System.Text.Json;
using System.Text.RegularExpressions;

namespace TrueFlac.Core;

public enum ArchiveLinkKind
{
    None,
    SearchQuery,
    ItemIdentifier,
    DirectFlacUrl,
    NonFlacArchiveUrl
}

public sealed record ArchiveSearchItem(
    string Identifier,
    string Title,
    string Creator,
    string Date,
    string Description,
    string DetailsUrl);

public sealed record ArchiveFlacFile(
    string FileName,
    long SizeBytes,
    string Format,
    string LengthText,
    string DownloadUrl)
{
    public string SizeMbText => SizeBytes > 0 ? $"{SizeBytes / (1024.0 * 1024.0):0.0} MB" : "--";
}

public sealed class ArchiveOrgClient : IDisposable
{
    private readonly HttpClient _http;

    public ArchiveOrgClient(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TrueFlacPlayer/1.0");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<IReadOnlyList<ArchiveSearchItem>> SearchFlacItemsAsync(string query, int rows = 25, CancellationToken ct = default)
    {
        string clean = query.Trim();
        if (string.IsNullOrWhiteSpace(clean)) return Array.Empty<ArchiveSearchItem>();

        string scoped = clean.Contains("format:", StringComparison.OrdinalIgnoreCase)
            ? clean
            : $"({clean}) AND format:FLAC";

        string url = $"https://archive.org/advancedsearch.php?q={Uri.EscapeDataString(scoped)}" +
                     $"&fl[]=identifier&fl[]=title&fl[]=creator&fl[]=date&fl[]=description" +
                     $"&rows={Math.Clamp(rows, 5, 50)}&page=1&output=json";

        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var list = new List<ArchiveSearchItem>();
        if (!doc.RootElement.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("docs", out var docs))
            return list;

        foreach (var el in docs.EnumerateArray())
        {
            string id = ReadString(el, "identifier");
            if (string.IsNullOrWhiteSpace(id)) continue;
            string title = ReadString(el, "title");
            string creator = ReadString(el, "creator");
            string date = ReadString(el, "date");
            string desc = StripHtml(ReadString(el, "description"));
            if (desc.Length > 220) desc = desc[..220] + "...";

            list.Add(new ArchiveSearchItem(
                id,
                string.IsNullOrWhiteSpace(title) ? id : title,
                string.IsNullOrWhiteSpace(creator) ? "Sin autor declarado" : creator,
                date,
                desc,
                $"https://archive.org/details/{id}"));
        }
        return list;
    }

    public async Task<IReadOnlyList<ArchiveFlacFile>> GetFlacFilesForItemAsync(string identifierOrUrl, CancellationToken ct = default)
    {
        var (_, files) = await GetItemWithFlacFilesAsync(identifierOrUrl, ct);
        return files;
    }

    public async Task<(ArchiveSearchItem? Item, IReadOnlyList<ArchiveFlacFile> Files)> GetItemWithFlacFilesAsync(
        string identifierOrUrl,
        CancellationToken ct = default)
    {
        string identifier = ExtractIdentifier(identifierOrUrl);
        if (string.IsNullOrWhiteSpace(identifier))
            return (null, Array.Empty<ArchiveFlacFile>());

        string url = $"https://archive.org/metadata/{Uri.EscapeDataString(identifier)}";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        ArchiveSearchItem? item = null;
        if (doc.RootElement.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            string title = ReadString(meta, "title");
            string creator = ReadString(meta, "creator");
            string date = ReadString(meta, "date");
            string desc = StripHtml(ReadString(meta, "description"));
            if (desc.Length > 220) desc = desc[..220] + "...";
            item = new ArchiveSearchItem(
                identifier,
                string.IsNullOrWhiteSpace(title) ? identifier : title,
                string.IsNullOrWhiteSpace(creator) ? "Sin autor declarado" : creator,
                date,
                desc,
                $"https://archive.org/details/{identifier}");
        }

        var files = new List<ArchiveFlacFile>();
        if (!doc.RootElement.TryGetProperty("files", out var filesEl) || filesEl.ValueKind != JsonValueKind.Array)
            return (item, files);

        foreach (var f in filesEl.EnumerateArray())
        {
            string name = ReadString(f, "name");
            if (!name.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)) continue;
            long size = 0;
            if (f.TryGetProperty("size", out var sizeEl))
            {
                if (sizeEl.ValueKind == JsonValueKind.String) long.TryParse(sizeEl.GetString(), out size);
                else if (sizeEl.ValueKind == JsonValueKind.Number) sizeEl.TryGetInt64(out size);
            }
            string format = ReadString(f, "format");
            string lengthText = ReadString(f, "length");
            string escapedPath = string.Join("/", name.Split('/').Select(Uri.EscapeDataString));
            string dlUrl = $"https://archive.org/download/{Uri.EscapeDataString(identifier)}/{escapedPath}";
            files.Add(new ArchiveFlacFile(name, size, string.IsNullOrWhiteSpace(format) ? "Flac" : format, lengthText, dlUrl));
        }

        if (item is null && files.Count > 0)
        {
            item = new ArchiveSearchItem(identifier, identifier, "Archive.org", string.Empty, string.Empty, $"https://archive.org/details/{identifier}");
        }

        return (item, files.OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public async Task<string> DownloadFileAsync(string url, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string? dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

        string tempPath = destinationPath + ".part";
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using (var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buf = new byte[65536];
                long readTotal = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    readTotal += n;
                    if (total is > 0) progress?.Report((double)readTotal / total.Value * 100.0);
                }
            }

            File.Move(tempPath, destinationPath, overwrite: true);
            return destinationPath;
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    public static (ArchiveLinkKind Kind, string Value) ClassifyUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return (ArchiveLinkKind.None, string.Empty);

        if (!uri.Host.Equals("archive.org", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.EndsWith(".archive.org", StringComparison.OrdinalIgnoreCase))
            return (ArchiveLinkKind.None, string.Empty);

        string path = uri.AbsolutePath.Trim('/');
        string[] segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segs.Length >= 1 && (segs[0].Equals("search", StringComparison.OrdinalIgnoreCase) || segs[0].Equals("advancedsearch.php", StringComparison.OrdinalIgnoreCase)))
        {
            string? q = ExtractQueryParam(uri.Query, "query") ?? ExtractQueryParam(uri.Query, "q");
            return !string.IsNullOrWhiteSpace(q)
                ? (ArchiveLinkKind.SearchQuery, q.Trim())
                : (ArchiveLinkKind.None, string.Empty);
        }

        if (segs.Length >= 2 && segs[0].Equals("download", StringComparison.OrdinalIgnoreCase))
        {
            if (segs.Length == 2)
                return (ArchiveLinkKind.ItemIdentifier, Uri.UnescapeDataString(segs[1]));

            if (uri.AbsolutePath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
                return (ArchiveLinkKind.DirectFlacUrl, url.Trim());

            string last = Uri.UnescapeDataString(segs[^1]);
            return Path.HasExtension(last)
                ? (ArchiveLinkKind.NonFlacArchiveUrl, url.Trim())
                : (ArchiveLinkKind.ItemIdentifier, Uri.UnescapeDataString(segs[1]));
        }

        if (segs.Length >= 2 && segs[0].Equals("details", StringComparison.OrdinalIgnoreCase))
        {
            string id = Uri.UnescapeDataString(segs[1]);
            if (segs.Length >= 3 && uri.AbsolutePath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
            {
                string subPath = string.Join("/", segs.Skip(2));
                return (ArchiveLinkKind.DirectFlacUrl, $"https://archive.org/download/{segs[1]}/{subPath}");
            }
            return (ArchiveLinkKind.ItemIdentifier, id);
        }

        return (ArchiveLinkKind.None, string.Empty);
    }

    private static string? ExtractQueryParam(string query, string targetName)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            string key = Uri.UnescapeDataString(pair[..eq].Replace('+', ' '));
            if (key.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return null;
    }

    public static string ExtractIdentifier(string input)
    {
        string s = input.Trim();
        if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Host.Contains("archive.org", StringComparison.OrdinalIgnoreCase))
        {
            var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length >= 2 && (segs[0].Equals("details", StringComparison.OrdinalIgnoreCase) || segs[0].Equals("download", StringComparison.OrdinalIgnoreCase)))
                return Uri.UnescapeDataString(segs[1]);
        }
        return s.Trim('/');
    }

    private static string ReadString(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var p)) return string.Empty;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join(", ", p.EnumerateArray().Select(x => x.ToString())),
            _ => p.ToString()
        };
    }

    private static string StripHtml(string html) =>
        string.IsNullOrWhiteSpace(html) ? string.Empty : Regex.Replace(html, "<.*?>", " ").Replace("  ", " ").Trim();

    public void Dispose() => _http.Dispose();
}
