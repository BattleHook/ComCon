using System.ComponentModel;
using System.Windows;
using ComCon.Core.Models;
using ComCon.Core.Presentation;

namespace ComCon.UI;

public partial class BridgeStartDialog : Window
{
    private readonly AppSettings saved;
    public IReadOnlyList<BridgeStartRow> Rows { get; }
    public IReadOnlyList<int> SelectedProfileIds { get; private set; } = [];

    public BridgeStartDialog(AppSettings saved, IReadOnlyList<BridgeStartRow> rows)
    {
        this.saved = saved;
        Rows = rows;
        InitializeComponent();
        DataContext = this;
        foreach (var row in Rows) row.PropertyChanged += SelectionChanged;
        Closed += (_, _) =>
        {
            foreach (var row in Rows) row.PropertyChanged -= SelectionChanged;
        };
        UpdateSelection();
    }

    private void SelectionChanged(object? sender, PropertyChangedEventArgs args) => UpdateSelection();

    private void UpdateSelection()
    {
        StartSelectedButton.IsEnabled = Rows.Any(row => row.CanStart && row.IsSelected);
        RawWarning.Visibility = Rows.Any(row => row.IsSelected && row.SavedMode == "RAW")
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartSelected(object sender, RoutedEventArgs args)
    {
        var ids = Rows.Where(row => row.CanStart && row.IsSelected).Select(row => row.ProfileId).ToArray();
        try { BridgeStartSelection.ValidateSelectedPorts(saved, ids); }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(this, exception.Message, "Selected port conflict",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SelectedProfileIds = ids;
        DialogResult = true;
    }
}
