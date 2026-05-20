using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace FlaUI_Test_Recorder;

public partial class ProcessPickerWindow : Window
{
    private List<ProcessListItem> _allProcesses = [];

    public int? SelectedProcessId { get; private set; }

    public ProcessPickerWindow()
    {
        InitializeComponent();
        PreviewKeyDown += ProcessPickerWindow_PreviewKeyDown;
        LoadProcesses();
    }

    private void ProcessPickerWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        SearchTextBox.Focus();
        SearchTextBox.SelectAll();
        e.Handled = true;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        LoadProcesses();
    }

    private void SearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ApplyProcessFilter();
    }

    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        SelectCurrentProcess();
    }

    private void ProcessesDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        SelectCurrentProcess();
    }

    private void SelectCurrentProcess()
    {
        if (ProcessesDataGrid.SelectedItem is not ProcessListItem selected)
        {
            MessageBox.Show("Select a process first.", "Process Required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedProcessId = selected.Id;
        DialogResult = true;
    }

    private void LoadProcesses()
    {
        IEnumerable<ProcessListItem> items;
        try
        {
            items = Process
                .GetProcesses()
                .Where(p =>
                {
                    try
                    {
                        return !string.IsNullOrWhiteSpace(p.ProcessName);
                    }
                    catch
                    {
                        return false;
                    }
                })
                .Select(p => new ProcessListItem
                {
                    Id = p.Id,
                    ProcessName = SafeGetProcessName(p),
                    MainWindowTitle = SafeGetMainWindowTitle(p)
                })
                .OrderBy(i => i.ProcessName)
                .ThenBy(i => i.Id)
                .ToList();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to enumerate processes.\n{ex.Message}", "Process List Error", MessageBoxButton.OK, MessageBoxImage.Error);
            items = [];
        }

        _allProcesses = items.ToList();
        ApplyProcessFilter();
    }

    private void ApplyProcessFilter()
    {
        var searchText = SearchTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(searchText))
        {
            ProcessesDataGrid.ItemsSource = _allProcesses;
            return;
        }

        var filtered = _allProcesses
            .Where(p =>
                p.ProcessName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                p.MainWindowTitle.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                p.Id.ToString().Contains(searchText, StringComparison.OrdinalIgnoreCase))
            .ToList();

        ProcessesDataGrid.ItemsSource = filtered;
    }

    private static string SafeGetProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch
        {
            return "<unknown>";
        }
    }

    private static string SafeGetMainWindowTitle(Process process)
    {
        try
        {
            return string.IsNullOrWhiteSpace(process.MainWindowTitle) ? "-" : process.MainWindowTitle;
        }
        catch
        {
            return "-";
        }
    }

    private sealed class ProcessListItem
    {
        public required int Id { get; init; }
        public required string ProcessName { get; init; }
        public required string MainWindowTitle { get; init; }
    }
}
