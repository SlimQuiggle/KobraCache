using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;
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
    private readonly AppUpdateService _updateService = new();
    private readonly AnycubicCloudClient _cloudClient = new();
    private readonly LanMqttPrinterClient _lanClient = new();
    private Forms.NotifyIcon? _trayIcon;
    private AppSettings _settings = new();
    private bool _isLoaded;

    private sealed record FileLoadResult(IReadOnlyList<PrinterCacheFile> Files, IReadOnlyList<string> Messages);

    public MainWindow()
    {
        InitializeComponent();
        Title = $"KobraCache v{AppVersion.Display}";
        VersionText.Text = $"v{AppVersion.Display}";
        DataContext = this;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        InitializeTrayIcon();
        AppLogger.Info("MainWindow initialized.");
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
        AppLogger.Info("Main window closing.");
        await SaveSettingsAsync();
        _trayIcon?.Dispose();
        _trayIcon = null;
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

            AppLogger.Info("Starting Slicer cloud import.");
            var cloudPrinters = await _cloudClient.ListPrintersAsync(import.CloudAccessToken);
            foreach (var printer in cloudPrinters)
            {
                UpsertPrinter(printer);
            }

            AppLogger.Info($"Slicer cloud import returned {cloudPrinters.Count} printer(s).");
            SetStatus(cloudPrinters.Count == 0
                ? "Cloud import returned 0 printers. Open Logs for the request diagnostics."
                : $"Imported {cloudPrinters.Count} cloud printer(s).");

            if (cloudPrinters.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "Anycubic accepted the Slicer token but returned no printers. Confirm Slicer Next can see the printers while logged into this same account, then try importing again.",
                    "KobraCache",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
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

    private async void PreviewSelectedPrinter_Click(object sender, RoutedEventArgs e)
    {
        await RunUiTaskAsync(ViewSelectedPrinterFilesAsync);
    }

    private async void RemoveSelectedPrinter_Click(object sender, RoutedEventArgs e)
    {
        await RunUiTaskAsync(async () =>
        {
            var selected = GetSelectedPrinterRow();
            if (selected is null)
            {
                SetStatus("Select a printer first.");
                return;
            }

            var name = selected.Name;
            _printers.Remove(selected);
            ClearFilePreviewRows();
            PreviewSummaryText.Text = "No files loaded";
            RefreshSelectedPrinterDetails();
            await SaveSettingsAsync();
            SetStatus($"Removed {name}.");
        });
    }

    private void PrintersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ClearFilePreviewRows();
        PreviewSummaryText.Text = "No files loaded";
        RefreshSelectedPrinterDetails();
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        await RunUiTaskAsync(ViewSelectedPrinterFilesAsync);
    }

    private void SelectAllFiles_Click(object sender, RoutedEventArgs e)
    {
        var selectableRows = _filePreviewRows
            .Where(row => row.CanDeleteWhenSelected)
            .ToArray();

        if (selectableRows.Length == 0)
        {
            UpdateDeleteButtonState();
            SetStatus("No deletable files are loaded.");
            return;
        }

        var clearAll = selectableRows.All(row => row.IsSelected);
        foreach (var row in selectableRows)
        {
            row.IsSelected = !clearAll;
        }

        UpdateDeleteButtonState();
        SetStatus(clearAll
            ? $"Cleared {selectableRows.Length} file selection(s)."
            : $"Selected {selectableRows.Length} file(s).");
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        await RunUiTaskAsync(DeleteSelectedFilesAsync);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.OpenLogDirectory();
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        await RunUiTaskAsync(CheckForUpdatesAsync);
    }

    private async Task LoadSettingsAsync()
    {
        _settings = await _settingsService.LoadAsync();
        var useNewStorageDefaults = _settings.StorageTargetDefaultsVersion == 0;
        LocalTargetCheckBox.IsChecked = useNewStorageDefaults || _settings.IncludeLocalCache;
        UsbTargetCheckBox.IsChecked = !useNewStorageDefaults && _settings.IncludeUsb;
        CloudTargetCheckBox.IsChecked = !useNewStorageDefaults && _settings.IncludeCloud;

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
            StorageTargetDefaultsVersion = 1,
            IncludeLocalCache = LocalTargetCheckBox.IsChecked == true,
            IncludeUsb = UsbTargetCheckBox.IsChecked == true,
            IncludeCloud = CloudTargetCheckBox.IsChecked == true
        };

        await _settingsService.SaveAsync(_settings);
    }

    private async Task ViewSelectedPrinterFilesAsync()
    {
        var selected = GetSelectedPrinterRow();
        if (selected is null)
        {
            SetStatus("Select a printer first.");
            return;
        }

        selected.Status = await ResolveStatusAsync(selected.Printer);
        var loadResult = await LoadFilesAsync(selected.Printer);
        var items = BuildFileItems(loadResult.Files);

        ClearFilePreviewRows();
        foreach (var item in items)
        {
            AddFilePreviewRow(item);
        }

        UpdateDeleteButtonState();
        var summary = BuildFileSummary(items);
        PreviewSummaryText.Text = loadResult.Messages.Count > 0
            ? $"{summary}. {string.Join(" ", loadResult.Messages)}"
            : summary;
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

        await ViewSelectedPrinterFilesAsync();
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

    private async Task<FileLoadResult> LoadFilesAsync(PrinterIdentity printer)
    {
        var files = new List<PrinterCacheFile>();
        var messages = new List<string>();
        var skippedTargets = new List<string>();
        if (LocalTargetCheckBox.IsChecked != true &&
            UsbTargetCheckBox.IsChecked != true &&
            CloudTargetCheckBox.IsChecked != true)
        {
            messages.Add("Select at least one storage target.");
            return new FileLoadResult(files, messages);
        }

        if (LocalTargetCheckBox.IsChecked == true)
        {
            if (printer.HasLanCredentials)
            {
                files.AddRange(await _lanClient.ListFilesAsync(printer, StorageTarget.LocalCache));
            }
            else if (HasCloudPrinterCommandCapability(printer))
            {
                files.AddRange(await _cloudClient.ListFilesAsync(printer, StorageTarget.LocalCache));
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
            else if (HasCloudPrinterCommandCapability(printer))
            {
                files.AddRange(await _cloudClient.ListFilesAsync(printer, StorageTarget.Usb));
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
                var cloudFiles = await _cloudClient.ListFilesAsync(printer, StorageTarget.Cloud);
                files.AddRange(cloudFiles);
                if (cloudFiles.Count == 0)
                {
                    messages.Add("Cloud API returned 0 cloud files for this account.");
                }
            }
            else
            {
                skippedTargets.Add("cloud");
            }
        }

        if (skippedTargets.Count > 0)
        {
            var skipped = string.Join(", ", skippedTargets);
            var message = printer.Source == PrinterSource.ManualIp && !printer.HasLanCredentials && !HasCloudCapability(printer)
                ? $"Manual IPs cannot list files by themselves. Import Slicer Cloud or Slicer LAN credentials, then select the imported/matched printer. Skipped {skipped}."
                : $"Skipped {skipped}; no credentials are available for those selected target(s).";
            messages.Add(message);
            AppLogger.Warn(message);
        }

        if (files.Count == 0 && messages.Count == 0)
        {
            messages.Add("No files were returned for the selected target(s).");
        }

        if (files.Count > 0)
        {
            AppLogger.Info($"Loaded {files.Count} file(s) for {printer.NameOrAddress}.");
        }

        return new FileLoadResult(files, messages);
    }

    private async Task CheckForUpdatesAsync()
    {
        SetStatus("Checking GitHub for updates...");
        var check = await _updateService.CheckForUpdatesAsync();
        if (!check.IsUpdateAvailable || check.Release is null || check.Asset is null)
        {
            var latestText = check.Release is null ? "" : $" Latest release: {check.Release.TagName}.";
            SetStatus($"KobraCache is up to date. Current version: v{AppVersion.Display}.{latestText}");
            MessageBox.Show(
                this,
                $"KobraCache is up to date.{Environment.NewLine}{Environment.NewLine}Current version: v{AppVersion.Display}{latestText}",
                "KobraCache updates",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var release = check.Release;
        var asset = check.Asset;
        var confirm = MessageBox.Show(
            this,
            $"KobraCache {release.TagName} is available.{Environment.NewLine}{Environment.NewLine}" +
            $"Current version: v{AppVersion.Display}{Environment.NewLine}" +
            $"Download: {asset.Name}{Environment.NewLine}{Environment.NewLine}" +
            "Install it now? KobraCache will close, update itself, and relaunch.",
            "KobraCache update available",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            SetStatus($"Update {release.TagName} is available but was not installed.");
            return;
        }

        SetStatus($"Downloading {release.TagName}...");
        var prepared = await _updateService.DownloadAndPrepareAsync(release, asset);
        _updateService.StartInstallOnExit(prepared);
        SetStatus($"Installing {release.TagName}. KobraCache will restart.");
        AppLogger.Info($"Update {release.TagName} prepared. Shutting down for install.");
        await SaveSettingsAsync();
        System.Windows.Application.Current.Shutdown();
    }

    private async Task DeleteFileAsync(PrinterIdentity printer, PrinterCacheFile file)
    {
        IPrinterTransport transport = file.StorageTarget == StorageTarget.Cloud ||
                                      !printer.HasLanCredentials && HasCloudPrinterCommandCapability(printer)
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

            return PrinterRuntimeStatus.CredentialsNeeded;
        }
        catch
        {
            return PrinterRuntimeStatus.Unknown;
        }
    }

    private void InitializeTrayIcon()
    {
        try
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Show KobraCache", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
            menu.Items.Add("Open Logs", null, (_, _) => Dispatcher.Invoke(AppLogger.OpenLogDirectory));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(Close));

            _trayIcon = new Forms.NotifyIcon
            {
                Text = "KobraCache",
                Icon = LoadTrayIcon(),
                Visible = true,
                ContextMenuStrip = menu
            };
            _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
            AppLogger.Info("Tray icon initialized.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Tray icon initialization failed.", ex);
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private static Icon LoadTrayIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/KobraCache.ico", UriKind.Absolute));
        if (resource?.Stream is null)
        {
            return (Icon)SystemIcons.Application.Clone();
        }

        using var icon = new Icon(resource.Stream);
        return (Icon)icon.Clone();
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

    private void AddFilePreviewRow(DeletePreviewItem item)
    {
        var row = new FilePreviewRow(item);
        row.PropertyChanged += FilePreviewRow_PropertyChanged;
        _filePreviewRows.Add(row);
    }

    private void ClearFilePreviewRows()
    {
        foreach (var row in _filePreviewRows)
        {
            row.PropertyChanged -= FilePreviewRow_PropertyChanged;
        }

        _filePreviewRows.Clear();
        UpdateDeleteButtonState();
    }

    private void FilePreviewRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilePreviewRow.IsSelected))
        {
            UpdateDeleteButtonState();
        }
    }

    private void UpdateDeleteButtonState()
    {
        var selectableRows = _filePreviewRows.Where(row => row.CanDeleteWhenSelected).ToArray();
        var hasSelected = selectableRows.Any(row => row.IsSelected);
        DeleteSelectedButton.IsEnabled = hasSelected;
        SelectAllButton.Content = selectableRows.Length > 0 && selectableRows.All(row => row.IsSelected)
            ? "Clear All"
            : "Select All";
    }

    private static IReadOnlyList<DeletePreviewItem> BuildFileItems(IEnumerable<PrinterCacheFile> files)
    {
        return files
            .OrderBy(file => file.StorageTarget)
            .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(file => file.IsCurrentJob
                ? new DeletePreviewItem(file, false, "Current or active print file.")
                : new DeletePreviewItem(file, true, "Ready."))
            .ToArray();
    }

    private static string BuildFileSummary(IReadOnlyList<DeletePreviewItem> items)
    {
        var selectableCount = items.Count(item => item.IsEligible);
        var totalBytes = items.Sum(item => item.File.SizeBytes ?? 0);
        return $"{items.Count} file(s) loaded, {selectableCount} selectable, {FormatBytes(totalBytes)} total";
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
            AppLogger.Warn(ex.Message);
            SetStatus(ex.Message);
            MessageBox.Show(this, ex.Message, "KobraCache", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLogger.Error("UI action failed.", ex);
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
        AppLogger.Info(message);
        HeaderStatusText.Text = message;
        FooterStatusText.Text = message;
    }

    private static bool HasCloudCapability(PrinterIdentity printer)
    {
        return !string.IsNullOrWhiteSpace(printer.CloudAccessToken) &&
               (!string.IsNullOrWhiteSpace(printer.CloudPrinterId) || !string.IsNullOrWhiteSpace(printer.CloudKey));
    }

    private static bool HasCloudPrinterCommandCapability(PrinterIdentity printer)
    {
        return !string.IsNullOrWhiteSpace(printer.CloudAccessToken) &&
               !string.IsNullOrWhiteSpace(printer.CloudPrinterId) &&
               !string.IsNullOrWhiteSpace(printer.CloudKey) &&
               !string.IsNullOrWhiteSpace(printer.ModeId);
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
