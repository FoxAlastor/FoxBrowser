using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FoxBrowser.Core;

public sealed record UpdateInfo(
    Version Latest,
    string Tag,
    string Title,
    string Notes,
    string PageUrl,
    string? AssetName,
    string? AssetUrl,
    long AssetSize,
    string? Sha256);

public sealed record UpdateCheckResult(UpdateInfo Info, bool IsNewer, string? Notice = null);

public sealed class UpdateServiceException : Exception
{
    public UpdateServiceException(string message) : base(message) { }
}

public sealed class UpdateService
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/FoxAlastor/FoxBrowser/releases/latest";
    private const string AssetPrefix = "https://github.com/FoxAlastor/FoxBrowser/releases/download/";
    private const long MaxExpandedZipBytes = 1L * 1024 * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();
    private static readonly string[] UserDataFileNames =
    {
        "settings.json", "session.json", "blocked-elements.json", "extension-options.json",
        "extension-ui.json", "pinned-extensions.json", "startup-trace.on", "startup-trace.log", "custom_icon.ico"
    };

    public static Version CurrentVersion => NormalizeVersion(typeof(FoxBrowser.App).Assembly.GetName().Version ?? new Version(0, 0, 0, 0));

    public async Task<UpdateCheckResult> CheckLatestAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        HttpResponseMessage response;
        try
        {
            response = await Client.GetAsync(ReleasesApiUrl, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateServiceException("Час очікування GitHub вичерпано.");
        }
        catch (HttpRequestException)
        {
            throw new UpdateServiceException("Не вдалося підключитися до GitHub.");
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == (HttpStatusCode)429)
                throw new UpdateServiceException("GitHub тимчасово обмежив запити. Спробуйте пізніше.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new UpdateServiceException("Реліз FoxBrowser не знайдено на GitHub.");
            if (!response.IsSuccessStatusCode)
                throw new UpdateServiceException($"GitHub повернув помилку {(int)response.StatusCode}.");
            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
                return ParseRelease(document.RootElement);
            }
            catch (JsonException)
            {
                throw new UpdateServiceException("GitHub повернув пошкоджені дані релізу.");
            }
        }
    }

    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(info.AssetUrl) || !info.AssetUrl.StartsWith(AssetPrefix, StringComparison.Ordinal))
            throw new UpdateServiceException("Адреса архіву оновлення не є довіреною адресою GitHub.");

        var updateDirectory = Path.Combine(AppContext.BaseDirectory, "update");
        try
        {
            if (Directory.Exists(updateDirectory)) Directory.Delete(updateDirectory, recursive: true);
            Directory.CreateDirectory(updateDirectory);
            var probe = Path.Combine(updateDirectory, ".write-test");
            await File.WriteAllTextAsync(probe, string.Empty, cancellationToken);
            File.Delete(probe);
        }
        catch (UnauthorizedAccessException)
        {
            throw new UpdateServiceException("Немає прав на запис у теку програми, оновіть вручну.");
        }
        catch (IOException ex)
        {
            throw new UpdateServiceException($"Не вдалося підготувати теку оновлення: {ex.Message}");
        }

        var packagePath = Path.Combine(updateDirectory, "package.zip");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await Client.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new UpdateServiceException($"Не вдалося завантажити архів: HTTP {(int)response.StatusCode}.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = File.Create(packagePath))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    idle.CancelAfter(TimeSpan.FromSeconds(30));
                    int read;
                    try { read = await input.ReadAsync(buffer.AsMemory(), idle.Token); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new UpdateServiceException("З'єднання перервано.");
                    }
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    total += read;
                    var expected = info.AssetSize > 0 ? info.AssetSize : response.Content.Headers.ContentLength ?? 0;
                    progress?.Report(expected > 0 ? Math.Clamp((double)total / expected, 0, 1) : 0);
                }
            }

            var actualLength = new FileInfo(packagePath).Length;
            if (info.AssetSize > 0 && actualLength != info.AssetSize)
                throw new UpdateServiceException("Розмір завантаженого архіву не збігається з даними GitHub.");
            if (!string.IsNullOrWhiteSpace(info.Sha256))
            {
                await using var hashStream = File.OpenRead(packagePath);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken));
                if (!hash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateServiceException("Хеш завантаженого архіву не збігається з даними GitHub.");
            }

            var extracted = Path.Combine(updateDirectory, "extracted");
            Directory.CreateDirectory(extracted);
            ValidateArchive(packagePath, extracted);
            ZipFile.ExtractToDirectory(packagePath, extracted, overwriteFiles: true);
            var roots = Directory.GetFiles(extracted, "FoxBrowser.exe", SearchOption.AllDirectories);
            if (roots.Length == 0) throw new UpdateServiceException("Архів не схожий на збірку FoxBrowser.");
            var minimumDepth = roots.Min(path => path[extracted.Length..].Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar));
            var candidates = roots.Where(path => path[extracted.Length..].Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar) == minimumDepth).ToArray();
            if (candidates.Length != 1) throw new UpdateServiceException("Архів неоднозначний: знайдено кілька FoxBrowser.exe.");
            var scriptPath = Path.Combine(updateDirectory, "apply-update.cmd");
            await File.WriteAllTextAsync(scriptPath, BuildUpdateScript(), new UTF8Encoding(false), cancellationToken);
            progress?.Report(1);
            return scriptPath;
        }
        catch
        {
            try { if (File.Exists(packagePath)) File.Delete(packagePath); } catch { }
            throw;
        }
    }

    public static bool StartInstaller(string scriptPath, string updateDirectory)
    {
        var extracted = Path.Combine(updateDirectory, "extracted");
        var roots = Directory.Exists(extracted) ? Directory.GetFiles(extracted, "FoxBrowser.exe", SearchOption.AllDirectories) : Array.Empty<string>();
        if (roots.Length == 0) return false;
        var minimumDepth = roots.Min(path => path[extracted.Length..].Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar));
        var candidates = roots.Where(path => path[extracted.Length..].Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar) == minimumDepth).ToArray();
        if (candidates.Length != 1) return false;
        var sourceRoot = Path.GetDirectoryName(candidates[0])!;
        var profile = new WebView2EnvironmentFactory().PortableProfilePath;
        var extensions = WebView2EnvironmentFactory.PortableExtensionsPath;
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            Arguments = "/d /c \"\"" + scriptPath + "\"\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        psi.Environment["FOX_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.Environment["FOX_EXE"] = "FoxBrowser.exe";
        psi.Environment["FOX_APPDIR"] = AppContext.BaseDirectory.TrimEnd('\\');
        psi.Environment["FOX_SRC"] = sourceRoot.TrimEnd('\\');
        psi.Environment["FOX_UPDATE"] = updateDirectory.TrimEnd('\\');
        psi.Environment["FOX_PROFILE"] = profile.TrimEnd('\\');
        psi.Environment["FOX_EXT"] = extensions.TrimEnd('\\');
        return System.Diagnostics.Process.Start(psi) is not null;
    }

    private static UpdateCheckResult ParseRelease(JsonElement root)
    {
        var tag = GetString(root, "tag_name")?.Trim() ?? string.Empty;
        var parsed = ParseVersion(tag);
        if (parsed is null) throw new UpdateServiceException($"Не вдалося розпізнати версію релізу «{tag}».");
        var pageUrl = GetString(root, "html_url") ?? "https://github.com/FoxAlastor/FoxBrowser/releases";
        var assets = new List<(string Name, string Url, long Size, string? Digest)>();
        if (root.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetArray.EnumerateArray())
            {
                var name = GetString(asset, "name");
                var url = GetString(asset, "browser_download_url");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                var digest = GetString(asset, "digest");
                assets.Add((name, url, asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var length) ? length : 0, digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null));
            }
        }
        var preferred = assets.Where(x => x.Name.Contains("FoxBrowser", StringComparison.OrdinalIgnoreCase)).ToList();
        var selected = preferred.Count == 1 ? preferred[0] : assets.Count == 1 ? assets[0] : default;
        var ambiguous = assets.Count == 0 || (preferred.Count > 1) || (preferred.Count == 0 && assets.Count > 1);
        var info = new UpdateInfo(parsed, tag, GetString(root, "name") ?? tag, GetString(root, "body") ?? string.Empty, pageUrl, ambiguous ? null : selected.Name, ambiguous ? null : selected.Url, ambiguous ? 0 : selected.Size, ambiguous ? null : selected.Digest);
        return new UpdateCheckResult(info, NormalizeVersion(CurrentVersion).CompareTo(parsed) < 0, ambiguous ? "У релізі немає однозначного portable ZIP-архіву." : null);
    }

    private static void ValidateArchive(string packagePath, string extracted)
    {
        long expanded = 0;
        var root = Path.GetFullPath(extracted).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(packagePath);
        foreach (var entry in archive.Entries)
        {
            expanded += entry.Length;
            if (expanded > MaxExpandedZipBytes) throw new UpdateServiceException("Архів пошкоджено: його розмір після розпакування завеликий.");
            var target = Path.GetFullPath(Path.Combine(extracted, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new UpdateServiceException("Архів пошкоджено.");
        }
    }

    private static Version? ParseVersion(string tag)
    {
        var value = tag.Trim().TrimStart('v', 'V');
        var cut = value.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) value = value[..cut];
        var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (parts.Count < 2) parts.Add("0");
        return Version.TryParse(string.Join('.', parts), out var version) ? NormalizeVersion(version) : null;
    }

    private static Version NormalizeVersion(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    private static string? GetString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FoxBrowser-Updater", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static string BuildUpdateScript()
    {
        var exclusions = string.Join(" ", UserDataFileNames.Select(name => "/XF \"" + name + "\""));
        return string.Join(Environment.NewLine, new[]
        {
            "@echo off",
            "setlocal",
            "set OK=1",
            "set /a tries=0",
            "",
            ":wait",
            "tasklist /FI \"PID eq %FOX_PID%\" /FI \"IMAGENAME eq %FOX_EXE%\" /NH 2>nul | find /I \"%FOX_EXE%\" >nul",
            "if errorlevel 1 goto copy",
            "set /a tries+=1",
            "if %tries% GEQ 60 goto timeout",
            "ping -n 2 127.0.0.1 >nul",
            "goto wait",
            "",
            ":timeout",
            "set OK=0",
            ">\"%FOX_UPDATE%\\update-failed.txt\" echo old process did not exit within 60 s",
            "goto launch",
            "",
            ":copy",
            "ping -n 3 127.0.0.1 >nul",
            $"robocopy \"%FOX_SRC%\" \"%FOX_APPDIR%\" /E /R:3 /W:1 {exclusions} /XD \"VPN\" \"Profile\" \"Extensions\" \"update\" >nul",
            "set RC=%ERRORLEVEL%",
            "if %RC% GEQ 8 (",
            "  set OK=0",
            ">\"%FOX_UPDATE%\\update-failed.txt\" echo robocopy exit code %RC%",
            ")",
            "",
            ":launch",
            "cd /d \"%FOX_APPDIR%\"",
            "start \"\" \"%FOX_APPDIR%\\%FOX_EXE%\"",
            "if \"%OK%\"==\"0\" exit /b 1",
            "cd /d \"%SystemRoot%\"",
            "(goto) 2>nul & rd /s /q \"%FOX_UPDATE%\""
        });
    }
}
