using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using ComCon.Core.Presentation;

namespace ComCon.UI;

internal static class ProductLinks
{
    public static string Version => ProductInfo.FormatVersion(typeof(ProductLinks).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static void OpenUrl(Window owner, string url) => Open(owner, url, url);

    public static void OpenHelp(Window owner) => OpenBundledFile(owner, ProductInfo.HelpRelativePath, "Help guide");

    public static void OpenBundledFile(Window owner, string relativePath, string description)
    {
        var path = ProductInfo.ResolveBundledFile(AppContext.BaseDirectory, relativePath);
        if (!File.Exists(path))
        {
            MessageBox.Show(owner,
                $"The {description} was not found at:\n{path}\n\nReinstall or re-extract the ComCon package to restore it.",
                "ComCon - file not found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Open(owner, path, description);
    }

    private static void Open(Window owner, string target, string description)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException or FileNotFoundException)
        {
            MessageBox.Show(owner, $"Could not open {description}:\n{exception.Message}",
                "ComCon", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
