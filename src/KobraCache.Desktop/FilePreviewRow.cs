using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using KobraCache.Core.Models;

namespace KobraCache.Desktop;

public sealed class FilePreviewRow : INotifyPropertyChanged
{
    private bool _isSelected;

    public FilePreviewRow(DeletePreviewItem item)
    {
        Item = item;
        _isSelected = item.IsEligible;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DeletePreviewItem Item { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsEligible => Item.IsEligible;

    public bool CanManualDelete => Item.Reason.Equals("No reliable file date.", StringComparison.OrdinalIgnoreCase);

    public string StorageTargetText => Item.File.StorageTarget switch
    {
        StorageTarget.LocalCache => "Local",
        StorageTarget.Usb => "USB",
        StorageTarget.Cloud => "Cloud",
        _ => Item.File.StorageTarget.ToString()
    };

    public string FileName => Item.File.FileName;

    public string DateText => Item.File.BestDate?.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) ?? "";

    public string SizeText => Item.File.SizeBytes is { } bytes ? FormatBytes(bytes) : "";

    public string EligibilityText => Item.IsEligible ? "Eligible" : Item.Reason;

    public bool CanDeleteWhenSelected => IsEligible || CanManualDelete;

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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
