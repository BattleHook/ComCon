using System.Windows;
using ComCon.Core.Presentation;

namespace ComCon.UI;

public partial class WelcomeWindow : Window
{
    public WelcomeWindow()
    {
        InitializeComponent();
        SafetyText.Text = ProductInfo.SafetyText;
        DonationText.Text = ProductInfo.DonationText;
    }

    private void OpenHelp(object sender, RoutedEventArgs args) => ProductLinks.OpenHelp(this);

    private void OpenKoFi(object sender, RoutedEventArgs args) => ProductLinks.OpenUrl(this, ProductInfo.KoFiUrl);
}
