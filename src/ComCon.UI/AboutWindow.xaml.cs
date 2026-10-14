using System.Windows;
using ComCon.Core.Presentation;

namespace ComCon.UI;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {ProductLinks.Version}";
        AttributionText.Text = $"Created by {ProductInfo.Attribution}. {ProductInfo.Copyright}.";
        LicenseText.Text = $"Released under the {ProductInfo.LicenseName}. Bundled third-party components are listed in the notices.";
        SafetyText.Text = ProductInfo.SafetyText;
        DonationText.Text = ProductInfo.DonationText;
    }

    private void OpenLicense(object sender, RoutedEventArgs args) =>
        ProductLinks.OpenBundledFile(this, ProductInfo.LicenseFileName, "license file");

    private void OpenNotices(object sender, RoutedEventArgs args) =>
        ProductLinks.OpenBundledFile(this, ProductInfo.NoticesFileName, "third-party notices");

    private void OpenHelp(object sender, RoutedEventArgs args) => ProductLinks.OpenHelp(this);

    private void OpenRepository(object sender, RoutedEventArgs args) => ProductLinks.OpenUrl(this, ProductInfo.RepositoryUrl);

    private void OpenIssues(object sender, RoutedEventArgs args) => ProductLinks.OpenUrl(this, ProductInfo.IssuesUrl);

    private void OpenKoFi(object sender, RoutedEventArgs args) => ProductLinks.OpenUrl(this, ProductInfo.KoFiUrl);
}
