using System.Collections.ObjectModel;
using System.IO.Compression;
using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace FoxBrowser.Core;

public sealed class PinnedExtensionItem
{
    public required CoreWebView2BrowserExtension Extension { get; init; }
    public required string Name { get; init; }
    public string Glyph => string.IsNullOrWhiteSpace(Name) ? "🧩" : Name[..1].ToUpperInvariant();
    public string? IconPath { get; init; }
    public bool HasIcon => !string.IsNullOrWhiteSpace(IconPath) && File.Exists(IconPath);
    public string? PopupPage { get; init; }
}

public sealed class ExtensionManager
{
    private readonly CoreWebView2Profile _profile;
    private readonly string _metadataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FoxBrowser", "extension-options.json");
    private readonly string _uiMetadataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FoxBrowser", "extension-ui.json");
    private readonly string _pinnedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FoxBrowser", "pinned-extensions.json");
    private readonly Dictionary<string, string?> _optionsPages;
    private readonly Dictionary<string, ExtensionUiMetadata> _uiMetadata;
    private readonly HashSet<string> _pinnedIds;

    public static string ExtensionsRootPath => WebView2EnvironmentFactory.PortableExtensionsPath;

    public ExtensionManager(CoreWebView2Profile profile)
    {
        _profile = profile;
        _optionsPages = LoadOptionsPages();
        _uiMetadata = LoadUiMetadata();
        _pinnedIds = LoadPinnedIds();
    }

    public ObservableCollection<CoreWebView2BrowserExtension> Extensions { get; } = new();
    public ObservableCollection<PinnedExtensionItem> PinnedExtensions { get; } = new();
    public event EventHandler? PinnedExtensionsChanged;

    public async Task RefreshAsync()
    {
        Extensions.Clear();
        PinnedExtensions.Clear();
        var installed = await _profile.GetBrowserExtensionsAsync();
        foreach (var extension in installed) Extensions.Add(extension);
        RebuildPinned();
    }

    public async Task<CoreWebView2BrowserExtension> AddExtensionAsync(string folderPath)
    {
        if (!Directory.Exists(folderPath)) throw new DirectoryNotFoundException(folderPath);
        var manifestPath = Path.Combine(folderPath, "manifest.json");
        if (!File.Exists(manifestPath)) throw new InvalidDataException("У вибраній папці немає manifest.json.");

        var manifest = await ReadManifestAsync(manifestPath);
        var extension = await _profile.AddBrowserExtensionAsync(folderPath);
        _optionsPages[extension.Id] = ReadOptionsPage(manifest);
        _uiMetadata[extension.Id] = ReadUiMetadata(manifest, folderPath);
        SaveOptionsPages();
        SaveUiMetadata();
        Extensions.Add(extension);
        RebuildPinned();
        return extension;
    }

    public async Task<CoreWebView2BrowserExtension> AddArchiveAsync(string archivePath)
    {
        if (!File.Exists(archivePath)) throw new FileNotFoundException("Файл extension не знайдено.", archivePath);
        var extensionName = Path.GetFileNameWithoutExtension(archivePath);
        var safeName = string.Concat(extensionName.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "extension";
        var destination = Path.Combine(ExtensionsRootPath, $"{safeName}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(destination);

        try
        {
            await using var input = File.OpenRead(archivePath);
            using var archive = await OpenZipPayloadAsync(input, archivePath);
            archive.ExtractToDirectory(destination);

            var manifestFolder = FindManifestFolder(destination);
            return await AddExtensionAsync(manifestFolder);
        }
        catch
        {
            try { Directory.Delete(destination, recursive: true); } catch { }
            throw;
        }
    }

    public static void OpenExtensionsFolder()
    {
        Directory.CreateDirectory(ExtensionsRootPath);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{ExtensionsRootPath}\"",
            UseShellExecute = true
        });
    }

    private static string FindManifestFolder(string destination)
    {
        var rootManifest = Path.Combine(destination, "manifest.json");
        if (File.Exists(rootManifest)) return destination;
        var nested = Directory.EnumerateFiles(destination, "manifest.json", SearchOption.AllDirectories).FirstOrDefault();
        if (nested is null) throw new InvalidDataException("В архіві не знайдено manifest.json.");
        return Path.GetDirectoryName(nested)!;
    }

    private static async Task<ZipArchive> OpenZipPayloadAsync(FileStream input, string archivePath)
    {
        if (!archivePath.EndsWith(".crx", StringComparison.OrdinalIgnoreCase))
            return new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);

        var header = new byte[16];
        var read = await input.ReadAsync(header);
        if (read < 16 || header[0] != (byte)'C' || header[1] != (byte)'r' || header[2] != (byte)'2' || header[3] != (byte)'4')
            throw new InvalidDataException("Файл CRX має некоректний заголовок.");

        var version = BitConverter.ToInt32(header, 4);
        var zipOffset = version switch
        {
            2 => 16 + BitConverter.ToInt32(header, 8) + BitConverter.ToInt32(header, 12),
            3 => 12 + BitConverter.ToInt32(header, 8),
            _ => throw new InvalidDataException($"Непідтримувана версія CRX: {version}.")
        };
        input.Position = zipOffset;
        var payload = new MemoryStream();
        await input.CopyToAsync(payload);
        payload.Position = 0;
        return new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: false);
    }

    public string? GetOptionsPage(string extensionId) => _optionsPages.TryGetValue(extensionId, out var page) ? page : null;
    public string? GetPopupPage(string extensionId) => _uiMetadata.TryGetValue(extensionId, out var metadata) ? metadata.PopupPage : null;
    public string? GetIconPath(string extensionId) => _uiMetadata.TryGetValue(extensionId, out var metadata) ? metadata.IconPath : null;
    public bool IsPinned(CoreWebView2BrowserExtension extension) => _pinnedIds.Contains(extension.Id);

    public void SetPinned(CoreWebView2BrowserExtension extension, bool pinned)
    {
        if (pinned) _pinnedIds.Add(extension.Id); else _pinnedIds.Remove(extension.Id);
        SavePinnedIds();
        RebuildPinned();
    }

    public async Task SetEnabledAsync(CoreWebView2BrowserExtension extension, bool enabled)
    {
        await extension.EnableAsync(enabled);
        RebuildPinned();
    }

    public async Task RemoveAsync(CoreWebView2BrowserExtension extension)
    {
        await extension.RemoveAsync();
        Extensions.Remove(extension);
        _optionsPages.Remove(extension.Id);
        _uiMetadata.Remove(extension.Id);
        _pinnedIds.Remove(extension.Id);
        SaveOptionsPages();
        SaveUiMetadata();
        SavePinnedIds();
        RebuildPinned();
    }

    private void RebuildPinned()
    {
        PinnedExtensions.Clear();
        foreach (var extension in Extensions)
        {
            if (!_pinnedIds.Contains(extension.Id) || !extension.IsEnabled) continue;
            _uiMetadata.TryGetValue(extension.Id, out var metadata);
            PinnedExtensions.Add(new PinnedExtensionItem
            {
                Extension = extension,
                Name = extension.Name,
                IconPath = metadata?.IconPath,
                PopupPage = metadata?.PopupPage
            });
        }
        PinnedExtensionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static async Task<JsonElement> ReadManifestAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.Clone();
    }

    private static string? ReadOptionsPage(JsonElement manifest)
    {
        if (manifest.TryGetProperty("options_ui", out var optionsUi) && optionsUi.ValueKind == JsonValueKind.Object && optionsUi.TryGetProperty("page", out var modernPage) && modernPage.ValueKind == JsonValueKind.String)
            return NormalizeRelativePage(modernPage.GetString());
        if (manifest.TryGetProperty("options_page", out var legacyPage) && legacyPage.ValueKind == JsonValueKind.String)
            return NormalizeRelativePage(legacyPage.GetString());
        return null;
    }

    private static ExtensionUiMetadata ReadUiMetadata(JsonElement manifest, string folderPath)
    {
        JsonElement action = default;
        if (manifest.TryGetProperty("action", out var modernAction) && modernAction.ValueKind == JsonValueKind.Object) action = modernAction;
        else if (manifest.TryGetProperty("browser_action", out var legacyAction) && legacyAction.ValueKind == JsonValueKind.Object) action = legacyAction;

        string? popup = null;
        if (action.ValueKind == JsonValueKind.Object && action.TryGetProperty("default_popup", out var popupElement) && popupElement.ValueKind == JsonValueKind.String)
            popup = NormalizeRelativePage(popupElement.GetString());

        string? icon = null;
        if (action.ValueKind == JsonValueKind.Object && action.TryGetProperty("default_icon", out var iconElement))
        {
            var iconPage = iconElement.ValueKind == JsonValueKind.String
                ? iconElement.GetString()
                : iconElement.ValueKind == JsonValueKind.Object
                    ? iconElement.EnumerateObject().OrderByDescending(item => ParseIconSize(item.Name)).Select(item => item.Value.GetString()).FirstOrDefault()
                    : null;
            var normalized = NormalizeRelativePage(iconPage);
            if (normalized is not null)
            {
                var fullPath = Path.GetFullPath(Path.Combine(folderPath, normalized));
                if (File.Exists(fullPath)) icon = fullPath;
            }
        }
        return new ExtensionUiMetadata(popup, icon);
    }

    private static int ParseIconSize(string key) => int.TryParse(key, out var size) ? size : 0;

    private static string? NormalizeRelativePage(string? page)
    {
        if (string.IsNullOrWhiteSpace(page)) return null;
        var normalized = page.Trim().Replace('\\', '/').TrimStart('/');
        return normalized.Contains("..", StringComparison.Ordinal) ? null : normalized;
    }

    private Dictionary<string, string?> LoadOptionsPages() => LoadJson(_metadataPath, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
    private Dictionary<string, ExtensionUiMetadata> LoadUiMetadata() => LoadJson(_uiMetadataPath, new Dictionary<string, ExtensionUiMetadata>(StringComparer.OrdinalIgnoreCase));
    private HashSet<string> LoadPinnedIds() => LoadJson(_pinnedPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static T LoadJson<T>(string path, T fallback)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? fallback : fallback; }
        catch { return fallback; }
    }

    private static void SaveJson<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private void SaveOptionsPages() => SaveJson(_metadataPath, _optionsPages);
    private void SaveUiMetadata() => SaveJson(_uiMetadataPath, _uiMetadata);
    private void SavePinnedIds() => SaveJson(_pinnedPath, _pinnedIds);

    public sealed record ExtensionUiMetadata(string? PopupPage, string? IconPath);
}
