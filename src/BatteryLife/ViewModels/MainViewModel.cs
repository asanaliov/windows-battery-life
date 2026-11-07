using System.IO;
using System.Windows;
using System.Windows.Media;
using BatteryLife.Models;
using BatteryLife.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace BatteryLife.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private static readonly SolidColorBrush GoodBrush = new(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly SolidColorBrush FairBrush = new(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush PoorBrush = new(Color.FromRgb(0xEF, 0x44, 0x44));

    private readonly BatteryReportService _service = new();
    private readonly UpdateService _updateService = new();

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private string? errorMessage;

    [ObservableProperty] private string deviceName = "";
    [ObservableProperty] private string lastScanText = "";

    [ObservableProperty] private double healthPercent;
    [ObservableProperty] private string healthText = "";
    [ObservableProperty] private Brush healthBrush = PoorBrush;

    // Shown when the raw reported health exceeds 100% (e.g. Surface firmware quirk).
    [ObservableProperty] private bool healthWasClamped;

    [ObservableProperty] private string cycleCountText = "";
    [ObservableProperty] private string designCapacityText = "";
    [ObservableProperty] private string fullChargeCapacityText = "";

    [ObservableProperty] private string fullChargeActiveText = "";
    [ObservableProperty] private string fullChargeStandbyText = "";
    [ObservableProperty] private string designActiveText = "";
    [ObservableProperty] private string designStandbyText = "";

    [ObservableProperty] private bool hasHistory;

    private IReadOnlyList<CapacityHistoryEntry> _history = [];
    private DateTime _historyAsOf;

    // Update banner
    [ObservableProperty] private bool updateAvailable;
    [ObservableProperty] private string updateVersionText = "";
    [ObservableProperty] private string updateStatusText = "A new release is available.";
    [ObservableProperty] private bool isUpdating;
    [ObservableProperty] private bool lastUpdateFailed;
    private UpdateInfo? _update;

    public ISeries[] HealthSeries { get; private set; } = [];
    public ISeries[] CycleSeries { get; private set; } = [];
    public Axis[] HealthXAxes { get; private set; } = [];
    public Axis[] HealthYAxes { get; private set; } = [];
    public Axis[] CycleXAxes { get; private set; } = [];
    public Axis[] CycleYAxes { get; private set; } = [];

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
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
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
                $"Download and install {UpdateVersionText} now? The app will restart when the update is ready.",
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
            UpdateStatusText = "Downloading update...";
            var progress = new Progress<double>(value =>
                UpdateStatusText = $"Downloading update... {value:P0}");
            downloadedUpdate = await _updateService.DownloadUpdateAsync(_update, progress);

            UpdateStatusText = "Installing update and restarting...";
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
        DeviceName = string.IsNullOrWhiteSpace(report.System.ProductName)
            ? report.System.ComputerName
            : report.System.ProductName;
        LastScanText = $"Report generated {report.ScanTime:g}";

        // Some laptop firmware (e.g. Microsoft Surface) can report a FullChargeCapacity
        // slightly higher than DesignCapacity, resulting in a raw health above 100%.
        // We clamp to 100 and show a note so the user understands what happened.
        var rawHealth = report.Battery.DesignCapacityWh > 0
            ? Math.Round(report.Battery.FullChargeCapacityWh / report.Battery.DesignCapacityWh * 100, 1)
            : 0;
        HealthWasClamped = rawHealth > 100;
        HealthPercent = Math.Min(rawHealth, 100.0);

        HealthText = $"{HealthPercent:0.#}%";
        HealthBrush = HealthPercent switch
        {
            >= 80 => GoodBrush,
            >= 50 => FairBrush,
            _ => PoorBrush,
        };

        CycleCountText = report.Battery.CycleCount >= 0 ? report.Battery.CycleCount.ToString() : "—";
        DesignCapacityText = $"{report.Battery.DesignCapacityWh:0.0} Wh";
        FullChargeCapacityText = $"{report.Battery.FullChargeCapacityWh:0.0} Wh";

        FullChargeActiveText = FormatDuration(report.FullChargeEstimate.ActiveRuntime);
        FullChargeStandbyText = FormatDuration(report.FullChargeEstimate.ConnectedStandbyRuntime);
        DesignActiveText = FormatDuration(report.DesignEstimate.ActiveRuntime);
        DesignStandbyText = FormatDuration(report.DesignEstimate.ConnectedStandbyRuntime);

        _history = report.History
            .Where(entry =>
                entry.StartDate != DateTime.MinValue &&
                entry.EndDate != DateTime.MinValue &&
                entry.EndDate >= entry.StartDate &&
                entry.StartDate <= report.ScanTime &&
                entry.EndDate <= report.ScanTime)
            .ToArray();
        _historyAsOf = _history.Count > 0
            ? _history.Max(entry => entry.EndDate)
            : report.ScanTime;
        HasHistory = _history.Count > 1;
        UpdateHistoryCharts();
    }

    private void UpdateHistoryCharts()
    {
        var history = _history.Where(entry => entry.EndDate <= _historyAsOf).ToArray();
        var cycleHistory = history.Where(entry => entry.CycleCount >= 0).ToArray();
        HasHistory = history.Length > 1;

        if (HasHistory)
        {
            var labels = history.Select(h => h.EndDate.ToString("MMM d, yyyy")).ToArray();

            var healthValues = history
                .Select(h => h.DesignCapacityWh > 0
                    ? Math.Min(Math.Round(h.FullChargeCapacityWh / h.DesignCapacityWh * 100, 1), 100.0)
                    : 0.0)
                .ToArray();

            HealthSeries =
            [
                new LineSeries<double>
                {
                    Name = "Battery health (%)",
                    Values = healthValues,
                    GeometrySize = 5,
                    Fill = null,
                    Stroke = new SolidColorPaint(SKColors.MediumSeaGreen, 2),
                    GeometryStroke = new SolidColorPaint(SKColors.MediumSeaGreen, 2),
                },
            ];
            HealthXAxes = [new Axis { Labels = labels, LabelsRotation = 45, TextSize = 11 }];
            HealthYAxes = [new Axis { Name = "%", TextSize = 11, NameTextSize = 11, MinLimit = 0, MaxLimit = 100 }];
            CycleSeries =
            [
                new LineSeries<int>
                {
                    Name = "Charge cycles",
                    Values = cycleHistory.Select(h => h.CycleCount).ToArray(),
                    GeometrySize = 5,
                    Fill = null,
                    Stroke = new SolidColorPaint(SKColors.DarkOrange, 2),
                    GeometryStroke = new SolidColorPaint(SKColors.DarkOrange, 2),
                },
            ];
            CycleXAxes =
            [
                new Axis
                {
                    Labels = cycleHistory.Select(h => h.EndDate.ToString("MMM d, yyyy")).ToArray(),
                    LabelsRotation = 45,
                    TextSize = 11,
                },
            ];
            CycleYAxes = [new Axis { Name = "Cycles", TextSize = 11, NameTextSize = 11, MinLimit = 0 }];

        }
        else
        {
            HealthSeries = [];
            CycleSeries = [];
            HealthXAxes = [];
            HealthYAxes = [];
            CycleXAxes = [];
            CycleYAxes = [];
        }

        OnPropertyChanged(nameof(HealthSeries));
        OnPropertyChanged(nameof(CycleSeries));
        OnPropertyChanged(nameof(HealthXAxes));
        OnPropertyChanged(nameof(HealthYAxes));
        OnPropertyChanged(nameof(CycleXAxes));
        OnPropertyChanged(nameof(CycleYAxes));
    }

    private static string FormatDuration(TimeSpan? ts)
    {
        if (ts is null) return "—";
        var t = ts.Value;
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{Math.Max(0, (int)t.TotalMinutes)}m";
    }
}
