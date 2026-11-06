using System.IO;
using System.Windows;
using BatteryLife.Controls;
using BatteryLife.Models;
using BatteryLife.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatteryLife.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const string AvailableStatusText = "Installs in the background and restarts the app.";
    private const string NoValue = "—";

    private static readonly string[] PlaceholderProductNames =
        ["To be filled by O.E.M.", "System Product Name", "Default string", "Not Applicable"];

    private readonly BatteryReportService _service = new();
    private readonly UpdateService _updateService = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowErrorBanner), nameof(ShowEmptyState), nameof(ShowHistoryHint))]
    private bool hasReport;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowErrorBanner), nameof(ShowEmptyState))]
    private bool hasError;

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string? errorMessage;

    [ObservableProperty] private string deviceName = "Battery Life";
    [ObservableProperty] private string lastScanText = "";

    [ObservableProperty] private double healthPercent;
    [ObservableProperty] private string healthText = "";
    [ObservableProperty] private string healthLevel = "";
    [ObservableProperty] private string capacityText = "";

    // Set when the raw reported health exceeds 100% (a firmware quirk on some laptops).
    [ObservableProperty] private bool healthWasClamped;
    [ObservableProperty] private string batteryCountText = "";

    [ObservableProperty] private string cycleCountText = "";
    [ObservableProperty] private string fullChargeActiveText = "";
    [ObservableProperty] private string fullChargeStandbyText = "";
    [ObservableProperty] private string designActiveText = "";
    [ObservableProperty] private string designStandbyText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHistoryHint))]
    private bool hasHistory;

    [ObservableProperty] private bool hasCycleHistory;
    [ObservableProperty] private IReadOnlyList<ChartPoint> healthPoints = [];
    [ObservableProperty] private IReadOnlyList<ChartPoint> cyclePoints = [];
    [ObservableProperty] private double healthAxisMinimum;

    // Update banner
    [ObservableProperty] private bool updateAvailable;
    [ObservableProperty] private string updateVersionText = "";
    [ObservableProperty] private string updateStatusText = AvailableStatusText;
    [ObservableProperty] private bool isUpdating;
    [ObservableProperty] private bool lastUpdateFailed;
    private UpdateInfo? _update;

    public bool ShowErrorBanner => HasError && HasReport;
    public bool ShowEmptyState => HasError && !HasReport;
    public bool ShowHistoryHint => HasReport && !HasHistory;

    public MainViewModel()
    {
        LastUpdateFailed = Environment.GetCommandLineArgs().Contains(UpdateService.UpdateFailedArgument);

        _ = RefreshAsync();
        _ = CheckForUpdateAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        HasError = false;
        ErrorMessage = null;

        try
        {
            var report = await _service.GetReportAsync();
            Apply(report);
            HasReport = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            HasError = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_update is null || IsUpdating)
        {
            return;
        }

        if (MessageBox.Show(
                $"Download and install {_update.Version} now? The app will restart when the update is ready.",
                "Install update",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        IsUpdating = true;
        string? downloadedUpdate = null;
        try
        {
            UpdateStatusText = "Downloading...";
            var progress = new Progress<double>(value => UpdateStatusText = $"Downloading... {value:P0}");
            downloadedUpdate = await _updateService.DownloadUpdateAsync(_update, progress);

            UpdateStatusText = "Installing and restarting...";
            _updateService.LaunchUpdater(downloadedUpdate);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"Update failed: {ex.Message}";
            if (downloadedUpdate is not null && File.Exists(downloadedUpdate))
            {
                File.Delete(downloadedUpdate);
            }
        }
        finally
        {
            IsUpdating = false;
        }
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var result = await _updateService.CheckForUpdateAsync();
            if (result is not null)
            {
                UpdateVersionText = $"Update available: {result.Version}";
                _update = result;
                UpdateAvailable = true;
            }
        }
        catch
        {
            // Silently ignore - update check is best-effort.
        }
    }

    private void Apply(BatteryReportData report)
    {
        var battery = report.Battery;

        DeviceName = CleanDeviceName(report.System.ProductName, report.System.ComputerName);
        LastScanText = $"Updated {report.ScanTime:g}";

        var rawHealth = battery.DesignCapacityWh > 0
            ? Math.Round(battery.FullChargeCapacityWh / battery.DesignCapacityWh * 100, 1)
            : 0;
        HealthWasClamped = rawHealth > 100;
        HealthPercent = Math.Min(rawHealth, 100.0);
        HealthText = battery.DesignCapacityWh > 0 ? $"{HealthPercent:0.#}%" : NoValue;
        HealthLevel = HealthPercent switch
        {
            <= 0 => "Unknown",
            >= 80 => "Good",
            >= 50 => "Fair",
            _ => "Poor",
        };
        CapacityText = $"Holds {battery.FullChargeCapacityWh:0.0} of {battery.DesignCapacityWh:0.0} Wh when full";
        BatteryCountText = battery.BatteryCount > 1 ? $"Combined across {battery.BatteryCount} batteries" : "";

        CycleCountText = battery.CycleCount >= 0 ? battery.CycleCount.ToString() : NoValue;

        FullChargeActiveText = FormatDuration(report.FullChargeEstimate.ActiveRuntime);
        FullChargeStandbyText = FormatDuration(report.FullChargeEstimate.ConnectedStandbyRuntime);
        DesignActiveText = FormatWhenNew(report.DesignEstimate.ActiveRuntime);
        DesignStandbyText = FormatWhenNew(report.DesignEstimate.ConnectedStandbyRuntime);

        UpdateHistory(report);
    }

    private void UpdateHistory(BatteryReportData report)
    {
        var history = report.History
            .Where(entry =>
                entry.StartDate != DateTime.MinValue &&
                entry.EndDate >= entry.StartDate &&
                entry.EndDate <= report.ScanTime)
            .ToArray();

        // Zero capacities and cycle counts mean Windows has no reading for that period.
        var health = history
            .Where(h => h.DesignCapacityWh > 0 && h.FullChargeCapacityWh > 0)
            .Select(h => new ChartPoint(h.EndDate, ToHealth(h.FullChargeCapacityWh, h.DesignCapacityWh)))
            .ToList();
        var cycles = history
            .Where(h => h.CycleCount > 0)
            .Select(h => new ChartPoint(h.EndDate, h.CycleCount))
            .ToList();

        // History stops at the last full period, so finish the lines at today's reading.
        var battery = report.Battery;
        if (battery.DesignCapacityWh > 0 && battery.FullChargeCapacityWh > 0)
        {
            health.Add(new ChartPoint(report.ScanTime, ToHealth(battery.FullChargeCapacityWh, battery.DesignCapacityWh)));
        }
        if (battery.CycleCount > 0)
        {
            cycles.Add(new ChartPoint(report.ScanTime, battery.CycleCount));
        }

        // Zoom the axis to the data, since a 0-100 scale flattens real wear into a line.
        HealthAxisMinimum = health.Count > 0
            ? Math.Max(0, Math.Floor((health.Min(p => p.Value) - 5) / 5) * 5)
            : 0;
        HealthPoints = health;
        CyclePoints = cycles;
        HasHistory = health.Count > 1;
        HasCycleHistory = cycles.Count > 1;
    }

    private static double ToHealth(double fullChargeWh, double designWh) =>
        Math.Min(Math.Round(fullChargeWh / designWh * 100, 1), 100.0);

    /// <summary>Turns "Vivobook_ASUSLaptop X1505VA_X1505VA" into "Vivobook ASUSLaptop X1505VA".</summary>
    private static string CleanDeviceName(string productName, string computerName)
    {
        if (string.IsNullOrWhiteSpace(productName) ||
            PlaceholderProductNames.Contains(productName.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return computerName;
        }

        var words = productName.Split([' ', '_'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string FormatWhenNew(TimeSpan? ts)
    {
        var text = FormatDuration(ts);
        return text == NoValue ? "" : $"{text} when new";
    }

    private static string FormatDuration(TimeSpan? ts)
    {
        if (ts is not { } t || t <= TimeSpan.Zero) return NoValue;
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{(int)t.TotalMinutes}m";
    }
}
