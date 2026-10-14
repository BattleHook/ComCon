using System.Windows;
using System.Windows.Media;

namespace ComCon.UI;

public partial class StylingSettingsDialog : Window
{
    private string runningHex;
    private string runningTextHex;
    private string faultHex;
    private string faultTextHex;

    public StylingSettingsDialog(
        string initialRunningHex, string initialRunningTextHex,
        string initialFaultHex, string initialFaultTextHex)
    {
        InitializeComponent();
        runningHex = initialRunningHex;
        runningTextHex = initialRunningTextHex;
        faultHex = initialFaultHex;
        faultTextHex = initialFaultTextHex;
        UpdateUi();
    }

    public string RunningColorHex => runningHex;
    public string RunningTextColorHex => runningTextHex;
    public string FaultColorHex => faultHex;
    public string FaultTextColorHex => faultTextHex;

    private void UpdateUi()
    {
        var runningColor = UiColorPreferences.ParseColor(runningHex, Color.FromRgb(200, 230, 201));
        var runningTextColor = UiColorPreferences.ParseColor(runningTextHex, Colors.Black);
        var faultColor = UiColorPreferences.ParseColor(faultHex, Color.FromRgb(255, 205, 210));
        var faultTextColor = UiColorPreferences.ParseColor(faultTextHex, Colors.Black);

        var runningBrush = new SolidColorBrush(runningColor);
        var runningTextBrush = new SolidColorBrush(runningTextColor);
        var faultBrush = new SolidColorBrush(faultColor);
        var faultTextBrush = new SolidColorBrush(faultTextColor);

        RunningSwatchBorder.Background = runningBrush;
        RunningHexText.Text = runningHex;
        RunningTextSwatchBorder.Background = runningTextBrush;
        RunningTextHexText.Text = runningTextHex;
        RunningPreviewRow.Background = runningBrush;
        RunningPreviewText.Foreground = runningTextBrush;

        FaultSwatchBorder.Background = faultBrush;
        FaultHexText.Text = faultHex;
        FaultTextSwatchBorder.Background = faultTextBrush;
        FaultTextHexText.Text = faultTextHex;
        FaultPreviewRow.Background = faultBrush;
        FaultPreviewText.Foreground = faultTextBrush;
    }

    private void ChooseRunningColor(object sender, RoutedEventArgs e)
    {
        var dialog = new ColorPickerDialog(
            runningHex,
            "Choose Running row background color",
            isTextColor: false,
            pairedColorHex: runningTextHex,
            sampleText: "Profile 1 / Running (Sample profile text)")
        { Owner = this };

        if (dialog.ShowDialog() == true)
        {
            runningHex = dialog.SelectedColorHex;
            UpdateUi();
        }
    }

    private void ChooseRunningTextColor(object sender, RoutedEventArgs e)
    {
        var dialog = new ColorPickerDialog(
            runningTextHex,
            "Choose Running row text color",
            isTextColor: true,
            pairedColorHex: runningHex,
            sampleText: "Profile 1 / Running (Sample profile text)")
        { Owner = this };

        if (dialog.ShowDialog() == true)
        {
            runningTextHex = dialog.SelectedColorHex;
            UpdateUi();
        }
    }

    private void ChooseFaultColor(object sender, RoutedEventArgs e)
    {
        var dialog = new ColorPickerDialog(
            faultHex,
            "Choose Faulted row background color",
            isTextColor: false,
            pairedColorHex: faultTextHex,
            sampleText: "Profile 2 / Faulted (Sample profile text)")
        { Owner = this };

        if (dialog.ShowDialog() == true)
        {
            faultHex = dialog.SelectedColorHex;
            UpdateUi();
        }
    }

    private void ChooseFaultTextColor(object sender, RoutedEventArgs e)
    {
        var dialog = new ColorPickerDialog(
            faultTextHex,
            "Choose Faulted row text color",
            isTextColor: true,
            pairedColorHex: faultHex,
            sampleText: "Profile 2 / Faulted (Sample profile text)")
        { Owner = this };

        if (dialog.ShowDialog() == true)
        {
            faultTextHex = dialog.SelectedColorHex;
            UpdateUi();
        }
    }

    private void ApplyWindowsTheme(object sender, RoutedEventArgs e)
    {
        runningHex = "#C8E6C9";
        runningTextHex = "#000000";
        faultHex = "#FFCDD2";
        faultTextHex = "#000000";
        UpdateUi();
    }

    private void ApplyLightTheme(object sender, RoutedEventArgs e)
    {
        runningHex = "#D1E7DD";
        runningTextHex = "#0F5132";
        faultHex = "#F8D7DA";
        faultTextHex = "#842029";
        UpdateUi();
    }

    private void ApplyDarkTheme(object sender, RoutedEventArgs e)
    {
        runningHex = "#1B4332";
        runningTextHex = "#D8F3DC";
        faultHex = "#4A151B";
        faultTextHex = "#FFD6D6";
        UpdateUi();
    }

    private void ApplyHighContrastTheme(object sender, RoutedEventArgs e)
    {
        runningHex = "#2E7D32";
        runningTextHex = "#FFFFFF";
        faultHex = "#C62828";
        faultTextHex = "#FFFFFF";
        UpdateUi();
    }

    private void ResetDefaults(object sender, RoutedEventArgs e)
    {
        ApplyWindowsTheme(sender, e);
    }

    private void OkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
