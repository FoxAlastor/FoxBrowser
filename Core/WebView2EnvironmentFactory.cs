using System.IO;
using System.Security;
using Microsoft.Web.WebView2.Core;

namespace FoxBrowser.Core;

/// <summary>
/// Creates every WebView2 environment with one consistent set of options and
/// chooses the portable profile when the application directory is writable.
/// </summary>
public sealed class WebView2EnvironmentFactory
{
    private const string ProductFolderName = "FoxBrowser";
    private const string ProfileFolderName = "Profile";
    private const string LegacyProfileFolderName = "WebView2";
    private const string ProtectedFolderNoticeMarker = "portable-profile-fallback-notified";

    private readonly string _applicationDataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolderName);
    private bool _shouldShowProtectedFolderNotice;

    public string PortableProfilePath => Path.Combine(AppContext.BaseDirectory, ProfileFolderName);
    public string LegacyProfilePath => Path.Combine(_applicationDataRoot, LegacyProfileFolderName);
    public bool UsingLegacyProfile { get; private set; }
    public string? MigrationError { get; private set; }

    /// <summary>
    /// Creates a WebView2 environment. An override is used only by startup
    /// recovery, which must always receive its own temporary profile.
    /// </summary>
    public async Task<CoreWebView2Environment> CreateAsync(string? userDataFolderOverride = null)
    {
        var userDataFolder = userDataFolderOverride ?? SelectPersistentProfile();
        Directory.CreateDirectory(userDataFolder);

        return await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder,
            options: CreateWebViewOptions());
    }

    /// <summary>
    /// Returns a startup notice once for the protected-installation fallback,
    /// and returns a migration error when one occurred during this startup.
    /// </summary>
    public string? TakeStartupNotice()
    {
        if (_shouldShowProtectedFolderNotice)
        {
            _shouldShowProtectedFolderNotice = false;
            return "FoxBrowser встановлено в захищену папку — профіль зберігається " +
                   "у стандартному місці Windows, а не поруч із застосунком. Для " +
                   "портативного профілю перенесіть FoxBrowser у папку з правами запису " +
                   "(наприклад, Робочий стіл або Документи).";
        }

        if (MigrationError is not null)
            return $"Не вдалося перенести профіль FoxBrowser у папку поруч із застосунком: {MigrationError}\n\n" +
                   "Запуск продовжено зі стандартним профілем Windows.";

        return null;
    }

    private string SelectPersistentProfile()
    {
        UsingLegacyProfile = false;
        MigrationError = null;

        if (!CanWriteToApplicationDirectory())
        {
            UsingLegacyProfile = true;
            MarkProtectedFolderNotice();
            return LegacyProfilePath;
        }

        if (!Directory.Exists(PortableProfilePath) && Directory.Exists(LegacyProfilePath))
        {
            try
            {
                CopyDirectoryContents(LegacyProfilePath, PortableProfilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                MigrationError = ex.Message;
                TryDeleteDirectory(PortableProfilePath);
                UsingLegacyProfile = true;
                return LegacyProfilePath;
            }
        }

        return PortableProfilePath;
    }

    private static CoreWebView2EnvironmentOptions CreateWebViewOptions() => new()
    {
        AreBrowserExtensionsEnabled = true
    };

    private static bool CanWriteToApplicationDirectory()
    {
        var testFile = Path.Combine(AppContext.BaseDirectory, $".foxbrowser-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(testFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.WriteByte(0);
            File.Delete(testFile);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            TryDeleteFile(testFile);
            return false;
        }
        catch (IOException)
        {
            TryDeleteFile(testFile);
            return false;
        }
        catch (SecurityException)
        {
            TryDeleteFile(testFile);
            return false;
        }
    }

    private void MarkProtectedFolderNotice()
    {
        try
        {
            Directory.CreateDirectory(_applicationDataRoot);
            var marker = ProtectedFolderNoticeMarkerPath();
            if (!File.Exists(marker))
            {
                File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
                _shouldShowProtectedFolderNotice = true;
            }
        }
        catch
        {
            // The fallback profile remains valid even if the notice marker cannot be saved.
        }
    }

    private string ProtectedFolderNoticeMarkerPath() => Path.Combine(_applicationDataRoot, ProtectedFolderNoticeMarker);

    private static void CopyDirectoryContents(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationFile = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(file, destinationFile, overwrite: false);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A partial migration must not prevent fallback to the legacy profile.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }
}
