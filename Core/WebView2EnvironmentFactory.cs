using System.IO;
using Microsoft.Web.WebView2.Core;

namespace FoxBrowser.Core;

/// <summary>
/// Creates WebView2 using only the portable profile next to FoxBrowser.exe.
/// No AppData/LocalAppData fallback is used.
/// </summary>
public sealed class WebView2EnvironmentFactory
{
    private const string ProfileFolderName = "Profile";
    private const string ExtensionsFolderName = "Extensions";

    public string PortableProfilePath => Path.Combine(AppContext.BaseDirectory, ProfileFolderName);
    public static string PortableExtensionsPath => Path.Combine(AppContext.BaseDirectory, ExtensionsFolderName);

    /// <summary>
    /// Kept for startup compatibility. Portable mode never switches to a user profile.
    /// </summary>
    public string? TakeStartupNotice() => null;

    public async Task<CoreWebView2Environment> CreateAsync(string? userDataFolderOverride = null)
    {
        var userDataFolder = userDataFolderOverride ?? PortableProfilePath;
        Directory.CreateDirectory(userDataFolder);

        return await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder,
            options: CreateWebViewOptions());
    }

    private static CoreWebView2EnvironmentOptions CreateWebViewOptions() => new()
    {
        AreBrowserExtensionsEnabled = true
    };
}
