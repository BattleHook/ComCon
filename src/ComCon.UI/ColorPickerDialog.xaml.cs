using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ComCon.UI;

public partial class ColorPickerDialog : Window
{
    private bool updating;
    private readonly bool isTextColor;
    private readonly Color pairedColor;

    private static readonly string[] PresetPaletteBackground =
    [
        // Greens (vibrant to soft)
        "#C8E6C9", "#A5D6A7", "#81C784", "#66BB6A", "#B9F6CA", "#69F0AE", "#E6F6EA", "#DCEDC8", "#DCE775",
        // Reds/Coral/Orange (vibrant to soft)
        "#FFCDD2", "#EF9A9A", "#E57373", "#EF5350", "#FF8A80", "#FF5252", "#FDEAEA", "#FFCCBC", "#FFAB91",
        // Dark & Accent backgrounds
        "#1B4332", "#4A151B", "#2E7D32", "#C62828", "#FFE082", "#FFF9C4", "#80CBC4", "#CFD8DC"
    ];

    private static readonly string[] PresetPaletteText =
    [
        // Blacks, Grays, Whites
        "#000000", "#111111", "#222222", "#333333", "#555555", "#777777", "#AAAAAA", "#E0E0E0", "#FFFFFF",
        // Deep tones for light backgrounds
        "#0F5132", "#1B4332", "#2E7D32", "#842029", "#4A151B", "#C62828", "#0D47A1", "#B71C1C",
        // Light tones for dark backgrounds
        "#D8F3DC", "#FFD6D6", "#FFF9C4", "#FFE082", "#E1BEE7", "#BBDEFB", "#80CBC4", "#B2DFDB"
    ];

    public ColorPickerDialog(
        string initialHex,
        string title = "Choose row highlight color",
        bool isTextColor = false,
        string? pairedColorHex = null,
        string? sampleText = null)
    {
        InitializeComponent();
        Title = title;
        this.isTextColor = isTextColor;
        this.pairedColor = UiColorPreferences.ParseColor(pairedColorHex, isTextColor ? Color.FromRgb(200, 230, 201) : Colors.Black);

        if (!string.IsNullOrEmpty(sampleText))
        {
            PreviewText.Text = sampleText;
        }

        BuildSwatches();
        var initial = UiColorPreferences.ParseColor(initialHex, isTextColor ? Colors.Black : Color.FromRgb(200, 230, 201));
        SelectedColor = initial;
        UpdateUiFromColor(initial);
    }

    public Color SelectedColor { get; private set; }
    public string SelectedColorHex => $"#{SelectedColor.R:X2}{SelectedColor.G:X2}{SelectedColor.B:X2}";

    private void BuildSwatches()
    {
        var palette = isTextColor ? PresetPaletteText : PresetPaletteBackground;
        foreach (var hex in palette)
        {
            var color = UiColorPreferences.ParseColor(hex, Colors.Transparent);
            var border = new Border
            {
                Width = 28,
                Height = 24,
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(100, 0, 0, 0)),
                Background = new SolidColorBrush(color),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = hex
            };
            border.MouseLeftButtonDown += (s, e) =>
            {
                SelectedColor = color;
                UpdateUiFromColor(color);
            };
            SwatchesPanel.Children.Add(border);
        }
    }

    private void UpdatePreview(Color color)
    {
        if (isTextColor)
        {
            // Row background is the paired background color
            PreviewBorder.Background = new SolidColorBrush(pairedColor);
            // Text color dynamically previews the selected text color
            var textBrush = new SolidColorBrush(color);
            PreviewLabel.Foreground = textBrush;
            PreviewText.Foreground = textBrush;
        }
        else
        {
            // Row background dynamically previews the selected background color
            PreviewBorder.Background = new SolidColorBrush(color);
            // Text color is the paired text color
            var textBrush = new SolidColorBrush(pairedColor);
            PreviewLabel.Foreground = textBrush;
            PreviewText.Foreground = textBrush;
        }
    }

    private void UpdateUiFromColor(Color color)
    {
        updating = true;
        try
        {
            RedSlider.Value = color.R;
            GreenSlider.Value = color.G;
            BlueSlider.Value = color.B;
            RedValueText.Text = color.R.ToString(CultureInfo.InvariantCulture);
            GreenValueText.Text = color.G.ToString(CultureInfo.InvariantCulture);
            BlueValueText.Text = color.B.ToString(CultureInfo.InvariantCulture);
            HexTextBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            UpdatePreview(color);
        }
        finally
        {
            updating = false;
        }
    }

    private void SliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updating) return;
        var r = (byte)Math.Clamp((int)RedSlider.Value, 0, 255);
        var g = (byte)Math.Clamp((int)GreenSlider.Value, 0, 255);
        var b = (byte)Math.Clamp((int)BlueSlider.Value, 0, 255);
        SelectedColor = Color.FromRgb(r, g, b);
        RedValueText.Text = r.ToString(CultureInfo.InvariantCulture);
        GreenValueText.Text = g.ToString(CultureInfo.InvariantCulture);
        BlueValueText.Text = b.ToString(CultureInfo.InvariantCulture);
        HexTextBox.Text = $"#{r:X2}{g:X2}{b:X2}";
        UpdatePreview(SelectedColor);
    }

    private void HexTextBoxChanged(object sender, TextChangedEventArgs e)
    {
        if (updating) return;
        var text = HexTextBox.Text.Trim();
        if ((text.Length == 7 && text.StartsWith('#')) || text.Length == 6)
        {
            var parsed = UiColorPreferences.ParseColor(text, Colors.Transparent);
            if (parsed != Colors.Transparent)
            {
                SelectedColor = parsed;
                updating = true;
                try
                {
                    RedSlider.Value = parsed.R;
                    GreenSlider.Value = parsed.G;
                    BlueSlider.Value = parsed.B;
                    RedValueText.Text = parsed.R.ToString(CultureInfo.InvariantCulture);
                    GreenValueText.Text = parsed.G.ToString(CultureInfo.InvariantCulture);
                    BlueValueText.Text = parsed.B.ToString(CultureInfo.InvariantCulture);
                    UpdatePreview(SelectedColor);
                }
                finally
                {
                    updating = false;
                }
            }
        }
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
