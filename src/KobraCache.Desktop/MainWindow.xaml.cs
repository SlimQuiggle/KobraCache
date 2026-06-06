using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using KobraCache.Core.Cloud;
using KobraCache.Core.Models;
using KobraCache.Core.Services;
using KobraCache.Core.Slicer;
using KobraCache.Core.Transports;

namespace KobraCache.Desktop;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<PrinterRow> _printers = [];
    private readonly ObservableCollection<FilePreviewRow> _filePreviewRows = [];
    private readonly AppSettingsService _settingsService = new();
    private readonly ManualPrinterService _manualPrinterService = new();
    private readonly SlicerConfigImporter _slicerImporter = new();
    private readonly RetentionFilter _retentionFilter = new();
    private readonly AnycubicCloudClient _cloudClient = new();
    private readonly LanMqttPrinterClient _lanClient = new();
    private AppSettings _settings = new();
    private bool _isLoaded;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    public ObservableCollection<PrinterRow> Printers => _printers;

    public ObservableCollection<FilePreviewRow> FilePreviewRows => _filePreviewRows;

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded)
        {
            return;
        }

        _isLoaded = true;
        await RunUiTaskAsync(LoadSettingsAsync);
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        await SaveSettingsAsync();
    }

    private async void AddManualIp_Click(object sender, RoutedEventArgs e)
    {
        await RunUiTaskAsync(async () =>
        {
            var printer = _manualPrinterService.AddManualIp(ManualIpTextBox.Text);
            UpsertPrinter(printer);
            ManualIpTextBox.Clear();
            await SaveSettingsAsync();
            SetStatus($"Added {printer.IpAddress}.");
        });
    }

    private async void ImportSlicerLan_Click(object sender, RoutedEventArgs e)
    {
        await RunUiTaskAsync(async () =>
        {
            var import = await _slicerImporter.ImportAsync();
            foreach (var printer in import.LanPrinters)
            {
                UpsertPrinter(printer);
            }

            SetStatus(import.LanPrinters.Count == 0
                ? $"No Slicer LAN printers found in {import.ConfigPath}."
                : $"Imported {import.LanPrinters.Count} Slicer LAN printer(s).");
        });
    }

    private async void ImportSlicerCloud_Click(object sender, RoutedEventArgs e)
    {
        await RunUiTaskAsync(async () =>
        {
            if (CloudOptInCheckBox.IsChecked != true)
            {
                MessageBox.Show(this, "Enable cloud token import first.", "KobraCache", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var import = await _slicerImporter.ImportAsync();
            if (string.IsNullOrWhiteSpace(import.CloudAccessToken))
            {
                SetStatus($"No Slicer cloud token found in {import.ConfigPath}.");
                return;
            }

            var cloudPrinters = await _cloudClient.ListPrintersAsync(import.CloudAccessToken);
            foreach (var printer in cloudPrinters)
            {
                UpsertPrinter(printer);
            }

            SetStatus(cloudPrinters.Count == 0
                ? "Cloud import completed with no printers returned."
                : $"Imported {cloudPrinters.Count} cloud printer(s).");
        });
    }

    private async void RefreshStatus_Click(object sender, RoutedEventArgs e)
    {
        await RunUiTaskAsync(async () =>
        {
            var selected = GetSelectedPrinterRow();
            if (selected is null)
            {
                SetStatus("Select a printer first.");
                return;
            }

            selected.Status = await ResolveStatusAsync(selected.Printer);
            RefreshSelectedPrinterDetails();
            SetStatus($"{selected.Name} status: {selected.StatusText}.");
        });
    }

    private void PrintersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _filePreviewRows.Clear();
        DeleteSelectedButton.IsEnabled = false;
        PreviewSummaryText.Text = "No preview loaded";
        RefreshSelectedPrinterDetails();
    }

    private void RetentionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CustomCutoffDatePicker.IsEnabled = GetRetentionPreset() == RetentionPreset.CustomDate;
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        await RunUiTaskAsync(PreviewSelectedPrinterAsync);
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        await RunUiTaskAsync(DeleteSelectedFilesAsync);
    }

    private async Task LoadSettingsAsync()
    {
        _settings = await _settingsService.LoadAsync();
        LocalTargetCheckBox.IsChecked = _settings.IncludeLocalCache;
        UsbTargetCheckBox.IsChecked = _settings.IncludeUsb;
        CloudTargetCheckBox.IsChecked = _settings.IncludeCloud;
        CustomCutoffDatePicker.SelectedDate = (_settings.CustomCutoffDate ?? DateOnly.FromDateTime(DateTime.Today.AddDays(-30))).ToDateTime(TimeOnly.MinValue);
        SelectRetentionPreset(_settings.RetentionPreset);

        foreach (var ipAddress in _settings.ManualIpAddresses.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                UpsertPrinter(_manualPrinterService.AddManualIp(ipAddress));
            }
            catch (ArgumentException)
            {
                // Ignore stale settings entries that no longer parse as IP addresses.
            }
        }

        SetStatus(_printers.Count == 0 ? "Ready." : $"Loaded {_printers.Count} saved printer(s).");
    }

    private async Task SaveSettingsAsync()
    {
        var manualIps = _printers
            .Select(row => row.Printer.IpAddress)
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ip => ip, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _settings = new AppSettings
        {
            ManualIpAddresses = manualIps,
            RetentionPreset = GetRetentionPreset(),
            CustomCutoffDate = CustomCutoffDatePicker.SelectedDate is { } date ? DateOnly.FromDateTime(date) : null,
            IncludeLocalCache = LocalTargetCheckBox.IsChecked == true,
            IncludeUsb = UsbTargetCheckBox.IsChecked == true,
            IncludeCloud = CloudTargetCheckBox.IsChecked == true
        };

        await _settingsService.SaveAsync(_settings);
    }

    private async Task PreviewSelectedPrinterAsync()
    {
        var selected = GetSelectedPrinterRow();
        if (selected is null)
        {
            SetStatus("Select a printer first.");
            return;
        }

        selected.Status = await ResolveStatusAsync(selected.Printer);
        var files = await LoadFilesAsync(selected.Printer);
        var policy = GetRetentionPolicy();
        var preview = _retentionFilter.CreatePreview(selected.Printer, selected.Status, files, policy);

        _filePreviewRows.Clear();
        foreach (var item in preview.Items)
        {
            _filePreviewRows.Add(new FilePreviewRow(item));
        }

        DeleteSelectedButton.IsEnabled = _filePreviewRows.Any(row => row.IsSelected && row.CanDeleteWhenSelected);
        PreviewSummaryText.Text = BuildPreviewSummary(preview);
        RefreshSelectedPrinterDetails();
        await SaveSettingsAsync();
    }

    private async Task DeleteSelectedFilesAsync()
    {
        var selected = GetSelectedPrinterRow();
        if (selected is null)
        {
            SetStatus("Select a printer first.");
            return;
        }

        FilesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        FilesGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var selectedRows = _filePreviewRows
            .Where(row => row.IsSelected && row.CanDeleteWhenSelected)
            .ToArray();

        if (selectedRows.Length == 0)
        {
            SetStatus("No eligible files selected.");
            return;
        }

        var status = await ResolveStatusAsync(selected.Printer);
        selected.Status = status;
        if (status != PrinterRuntimeStatus.Idle)
        {
            MessageBox.Show(this, "Deletion is blocked because the printer is not confirmed idle.", "KobraCache", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus($"Deletion blocked. {selected.Name} status: {status}.");
            RefreshSelectedPrinterDetails();
            return;
        }

        var totalBytes = selectedRows.Sum(row => row.Item.File.SizeBytes ?? 0);
        var confirmText = $"Delete {selectedRows.Length} file(s) from {selected.Name}?";
        if (totalBytes > 0)
        {
            confirmText += Environment.NewLine + $"Estimated size: {FormatBytes(totalBytes)}";
        }

        var confirm = MessageBox.Show(this, confirmText, "Confirm deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            SetStatus("Deletion canceled.");
            return;
        }

        var failures = new List<string>();
        foreach (var row in selectedRows)
        {
            try
            {
                await DeleteFileAsync(selected.Printer, row.Item.File);
            }
            catch (Exception ex)
            {
                failures.Add($"{row.FileName}: {ex.Message}");
            }
        }

        await PreviewSelectedPrinterAsync();
        if (failures.Count == 0)
        {
            SetStatus($"Deleted {selectedRows.Length} file(s), then refreshed the file list.");
        }
        else
        {
            SetStatus($"Deleted with {failures.Count} failure(s).");
            MessageBox.Show(this, string.Join(Environment.NewLine, failures), "Deletion failures", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task<IReadOnlyList<PrinterCacheFile>> LoadFilesAsync(PrinterIdentity printer)
    {
        var files = new List<PrinterCacheFile>();
        var skippedTargets = new List<string>();

        if (LocalTargetCheckBox.IsChecked == true)
        {
            if (printer.HasLanCredentials)
            {
                files.AddRange(await _lanClient.ListFilesAsync(printer, StorageTarget.LocalCache));
            }
            else
            {
                skippedTargets.Add("local cache");
            }
        }

        if (UsbTargetCheckBox.IsChecked == true)
        {
            if (printer.HasLanCredentials)
            {
                files.AddRange(await _lanClient.ListFilesAsync(printer, StorageTarget.Usb));
            }
            else
            {
                skippedTargets.Add("USB");
            }
        }

        if (CloudTargetCheckBox.IsChecked == true)
        {
            if (HasCloudCapability(printer))
            {
                files.AddRange(await _cloudClient.ListFilesAsync(printer, StorageTarget.Cloud));
            }
            else
            {
                skippedTargets.Add("cloud");
            }
        }

        if (skippedTargets.Count > 0 && files.Count == 0)
        {
            SetStatus($"Skipped {string.Join(", ", skippedTargets)}. No credentials are available for the selected target(s).");
        }

        return files;
    }

    private async Task DeleteFileAsync(PrinterIdentity printer, PrinterCacheFile file)
    {
        IPrinterTransport transport = file.StorageTarget == StorageTarget.Cloud
            ? _cloudClient
            : _lanClient;

        await transport.DeleteFileAsync(printer, file);
    }

    private async Task<PrinterRuntimeStatus> ResolveStatusAsync(PrinterIdentity printer)
    {
        try
        {
            if (HasCloudCapability(printer))
            {
                var cloudStatus = await _cloudClient.GetStatusAsync(printer);
                if (cloudStatus != PrinterRuntimeStatus.Unknown)
                {
                    return cloudStatus;
                }
            }

            if (printer.HasLanCredentials)
            {
                return await _lanClient.GetStatusAsync(printer);
            }

            return await ProbeManualPrinterAsync(printer);
        }
        catch
        {
            return PrinterRuntimeStatus.Unknown;
        }
    }

    private static async Task<PrinterRuntimeStatus> ProbeManualPrinterAsync(PrinterIdentity printer)
    {
        if (!printer.HasValidIp || string.IsNullOrWhiteSpace(printer.IpAddress))
        {
            return PrinterRuntimeStatus.Unknown;
        }

        using var client = new TcpClient();
        var connectTask = client.ConnectAsync(printer.IpAddress, 9883);
        var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(2)));
        if (completed != connectTask)
        {
            return PrinterRuntimeStatus.Offline;
        }

        return connectTask.IsCompletedSuccessfully ? PrinterRuntimeStatus.Unknown : PrinterRuntimeStatus.Offline;
    }

    private void UpsertPrinter(PrinterIdentity printer)
    {
        var existing = _printers.FirstOrDefault(row => SamePrinter(row.Printer, printer));
        if (existing is null)
        {
            _printers.Add(new PrinterRow(printer));
        }
        else
        {
            existing.Merge(printer);
        }
    }

    private static bool SamePrinter(PrinterIdentity left, PrinterIdentity right)
    {
        if (!string.IsNullOrWhiteSpace(left.IpAddress) &&
            !string.IsNullOrWhiteSpace(right.IpAddress) &&
            left.IpAddress.Equals(right.IpAddress, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(left.DeviceId) &&
            !string.IsNullOrWhiteSpace(right.DeviceId) &&
            left.DeviceId.Equals(right.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(left.CloudPrinterId) &&
            !string.IsNullOrWhiteSpace(right.CloudPrinterId) &&
            left.CloudPrinterId.Equals(right.CloudPrinterId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return left.Key.Equals(right.Key, StringComparison.OrdinalIgnoreCase);
    }

    private PrinterRow? GetSelectedPrinterRow()
    {
        return PrintersGrid.SelectedItem as PrinterRow;
    }

    private void RefreshSelectedPrinterDetails()
    {
        if (GetSelectedPrinterRow() is not { } selected)
        {
            SelectedPrinterText.Text = "No printer selected";
            SelectedPrinterDetailText.Text = "";
            return;
        }

        SelectedPrinterText.Text = $"{selected.Name} | {selected.StatusText}";
        SelectedPrinterDetailText.Text = selected.DetailText;
    }

    private RetentionPolicy GetRetentionPolicy()
    {
        return new RetentionPolicy
        {
            Preset = GetRetentionPreset(),
            CustomCutoffDate = CustomCutoffDatePicker.SelectedDate is { } date ? DateOnly.FromDateTime(date) : null,
            IncludeUndatedManualSelections = true
        };
    }

    private RetentionPreset GetRetentionPreset()
    {
        if (RetentionComboBox.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<RetentionPreset>(item.Tag?.ToString(), out var preset))
        {
            return preset;
        }

        return RetentionPreset.Days30;
    }

    private void SelectRetentionPreset(RetentionPreset preset)
    {
        foreach (var item in RetentionComboBox.Items.OfType<ComboBoxItem>())
        {
            if (Enum.TryParse<RetentionPreset>(item.Tag?.ToString(), out var candidate) && candidate == preset)
            {
                RetentionComboBox.SelectedItem = item;
                return;
            }
        }

        RetentionComboBox.SelectedIndex = 0;
    }

    private static string BuildPreviewSummary(DeletePreview preview)
    {
        var selectedBytes = FormatBytes(preview.EligibleBytes);
        var cutoff = preview.Cutoff.LocalDateTime.ToString("d", CultureInfo.CurrentCulture);
        return $"{preview.Items.Count} file(s), {preview.EligibleCount} selected by retention, {selectedBytes}, cutoff {cutoff}";
    }

    private async Task RunUiTaskAsync(Func<Task> task)
    {
        try
        {
            SetBusy(true);
            await task();
        }
        catch (ArgumentException ex)
        {
            SetStatus(ex.Message);
            MessageBox.Show(this, ex.Message, "KobraCache", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            MessageBox.Show(this, ex.Message, "KobraCache", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool isBusy)
    {
        Cursor = isBusy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void SetStatus(string message)
    {
        HeaderStatusText.Text = message;
        FooterStatusText.Text = message;
    }

    private static bool HasCloudCapability(PrinterIdentity printer)
    {
        return !string.IsNullOrWhiteSpace(printer.CloudAccessToken) &&
               (!string.IsNullOrWhiteSpace(printer.CloudPrinterId) || !string.IsNullOrWhiteSpace(printer.CloudKey));
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : $"{size:0.0} {units[unit]}";
    }
}
