using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NebulaAuth.Helpers;

public sealed class BrowserBookmarkItem
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
}

public sealed class BrowserBookmarksData
{
    public List<BrowserBookmarkItem> Bookmarks { get; set; } = [];
    public List<string> Folders { get; set; } = [];
}

public static class BrowserBookmarksStorage
{
    private static readonly string BookmarksFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NebulaAuth",
        "browser_bookmarks.json");

    public static BrowserBookmarksData LoadData()
    {
        try
        {
            if (!File.Exists(BookmarksFilePath))
            {
                return new BrowserBookmarksData();
            }

            var json = File.ReadAllText(BookmarksFilePath);
            using var doc = JsonDocument.Parse(json);

            // Legacy format: plain bookmarks array
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return new BrowserBookmarksData
                {
                    Bookmarks = LoadLegacyBookmarks(doc.RootElement).ToList(),
                    Folders = []
                };
            }

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new BrowserBookmarksData();
            }

            var result = JsonSerializer.Deserialize<BrowserBookmarksData>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new BrowserBookmarksData();

            result.Bookmarks = Normalize(result.Bookmarks).ToList();
            result.Folders = result.Folders
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return result;
        }
        catch
        {
            return new BrowserBookmarksData();
        }
    }

    public static IReadOnlyList<BrowserBookmarkItem> Load()
    {
        return LoadData().Bookmarks;
    }

    public static void SaveData(BrowserBookmarksData data)
    {
        var normalized = new BrowserBookmarksData
        {
            Bookmarks = Normalize(data.Bookmarks).ToList(),
            Folders = data.Folders
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        var dir = Path.GetDirectoryName(BookmarksFilePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(BookmarksFilePath, json);
    }

    public static void Save(IEnumerable<BrowserBookmarkItem> bookmarks)
    {
        SaveData(new BrowserBookmarksData
        {
            Bookmarks = bookmarks.ToList(),
            Folders = []
        });
    }

    private static IReadOnlyList<BrowserBookmarkItem> LoadLegacyBookmarks(JsonElement arrayNode)
    {
        var result = new List<BrowserBookmarkItem>();
        foreach (var element in arrayNode.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var url = element.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(url)) continue;
                result.Add(new BrowserBookmarkItem
                {
                    Url = url,
                    Title = BuildTitle(url),
                    Folder = string.Empty
                });
                continue;
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                var url = element.TryGetProperty("url", out var urlNode)
                    ? urlNode.GetString()?.Trim()
                    : null;
                if (string.IsNullOrWhiteSpace(url)) continue;

                var title = element.TryGetProperty("title", out var titleNode)
                    ? titleNode.GetString()?.Trim()
                    : null;
                var folder = element.TryGetProperty("folder", out var folderNode)
                    ? folderNode.GetString()?.Trim()
                    : null;

                result.Add(new BrowserBookmarkItem
                {
                    Url = url,
                    Title = string.IsNullOrWhiteSpace(title) ? BuildTitle(url) : title,
                    Folder = folder ?? string.Empty
                });
            }
        }

        return Normalize(result);
    }

    private static IReadOnlyList<BrowserBookmarkItem> Normalize(IEnumerable<BrowserBookmarkItem> bookmarks)
    {
        return bookmarks
            .Where(b => !string.IsNullOrWhiteSpace(b.Url))
            .Select(b =>
            {
                var url = b.Url.Trim();
                var title = string.IsNullOrWhiteSpace(b.Title) ? BuildTitle(url) : b.Title.Trim();
                var folder = b.Folder?.Trim() ?? string.Empty;
                return new BrowserBookmarkItem
                {
                    Url = url,
                    Title = title,
                    Folder = folder
                };
            })
            .GroupBy(b => b.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(b => b.Folder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildTitle(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var host = uri.Host;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        var first = host.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(first) ? host : first;
    }
}
