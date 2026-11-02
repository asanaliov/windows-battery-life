using System.Diagnostics;
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

    // Trend labels shown above the charts.
    [ObservableProperty] private string capacityTrendText = "";
    [ObservableProperty] private string cycleTrendText = "";

    // Update banner
    [ObservableProperty] private bool updateAvailable;
    [ObservableProperty] private string updateVersionText = "";
    [ObservableProperty] private string updateUrl = "";

    public ISeries[] CapacitySeries { get; private set; } = [];
    public ISeries[] HealthSeries { get; private set; } = [];
    public ISeries[] CycleSeries { get; private set; } = [];
    public Axis[] CapacityXAxes { get; private set; } = [];
    public Axis[] CapacityYAxes { get; private set; } = [];
    public Axis[] HealthXAxes { get; private set; } = [];
    public Axis[] HealthYAxes { get; private set; } = [];
    public Axis[] CycleXAxes { get; private set; } = [];
    public Axis[] CycleYAxes { get; private set; } = [];

    public MainViewModel()
    {
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
    private void OpenUpdateUrl()
    {
        if (!string.IsNullOrEmpty(UpdateUrl))
        {
            Process.Start(new ProcessStartInfo(UpdateUrl) { UseShellExecute = true });
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
                UpdateUrl = result.Url;
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

        CycleCountText = report.Battery.CycleCount.ToString();
        DesignCapacityText = $"{report.Battery.DesignCapacityWh:0.0} Wh";
        FullChargeCapacityText = $"{report.Battery.FullChargeCapacityWh:0.0} Wh";

        FullChargeActiveText = FormatDuration(report.FullChargeEstimate.ActiveRuntime);
        FullChargeStandbyText = FormatDuration(report.FullChargeEstimate.ConnectedStandbyRuntime);
        DesignActiveText = FormatDuration(report.DesignEstimate.ActiveRuntime);
        DesignStandbyText = FormatDuration(report.DesignEstimate.ConnectedStandbyRuntime);

        HasHistory = report.History.Count > 1;
        if (HasHistory)
        {
            var labels = report.History.Select(h => h.StartDate.ToString("MMM d")).ToArray();

            // --- Capacity chart: full charge vs. design capacity ---
            CapacitySeries =
            [
                new LineSeries<double>
                {
                    Name = "Full charge capacity (Wh)",
                    Values = report.History.Select(h => h.FullChargeCapacityWh).ToArray(),
                    GeometrySize = 5,
                    Fill = null,
                    Stroke = new SolidColorPaint(SKColors.DodgerBlue, 2),
                    GeometryStroke = new SolidColorPaint(SKColors.DodgerBlue, 2),
                },
                new LineSeries<double>
                {
                    Name = "Design capacity (Wh)",
                    Values = report.History.Select(h => h.DesignCapacityWh).ToArray(),
                    GeometrySize = 0,
                    Fill = null,
                    Stroke = new SolidColorPaint(new SKColor(0xD1, 0xD5, 0xDB), 2) { PathEffect = new DashEffect([6, 4]) },
                },
            ];
            CapacityXAxes = [new Axis { Labels = labels, LabelsRotation = 45, TextSize = 11 }];
            CapacityYAxes = [new Axis { Name = "Wh", TextSize = 11, NameTextSize = 11 }];

            // --- Health chart: battery health % over time ---
            var healthValues = report.History
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
            HealthYAxes =
            [
                new Axis
                {
                    Name = "%",
                    TextSize = 11,
                    NameTextSize = 11,
                    MinLimit = 0,
                    MaxLimit = 100,
                },
            ];

            // --- Trend labels ---
            CapacityTrendText = BuildCapacityTrend(report.History);
            CycleTrendText = BuildCycleTrend(report.History);

            // --- Cycle count chart ---
            CycleSeries =
            [
                new LineSeries<int>
                {
                    Name = "Charge cycles",
                    Values = report.History.Select(h => h.CycleCount).ToArray(),
                    GeometrySize = 5,
                    Fill = null,
                    Stroke = new SolidColorPaint(SKColors.DarkOrange, 2),
                    GeometryStroke = new SolidColorPaint(SKColors.DarkOrange, 2),
                },
            ];
            CycleXAxes = [new Axis { Labels = labels, LabelsRotation = 45, TextSize = 11 }];
            CycleYAxes = [new Axis { Name = "Cycles", TextSize = 11, NameTextSize = 11, MinLimit = 0 }];
        }
        else
        {
            CapacitySeries = [];
            HealthSeries = [];
            CycleSeries = [];
            CapacityXAxes = [];
            CapacityYAxes = [];
            HealthXAxes = [];
            HealthYAxes = [];
            CycleXAxes = [];
            CycleYAxes = [];
            CapacityTrendText = "";
            CycleTrendText = "";
        }

        OnPropertyChanged(nameof(CapacitySeries));
        OnPropertyChanged(nameof(HealthSeries));
        OnPropertyChanged(nameof(CycleSeries));
        OnPropertyChanged(nameof(CapacityXAxes));
        OnPropertyChanged(nameof(CapacityYAxes));
        OnPropertyChanged(nameof(HealthXAxes));
        OnPropertyChanged(nameof(HealthYAxes));
        OnPropertyChanged(nameof(CycleXAxes));
        OnPropertyChanged(nameof(CycleYAxes));
    }

    // Returns a short human-readable description of the capacity trend.
    private static string BuildCapacityTrend(IReadOnlyList<CapacityHistoryEntry> history)
    {
        if (history.Count < 2) return "";

        var first = history[0].FullChargeCapacityWh;
        var last = history[^1].FullChargeCapacityWh;
        if (first <= 0) return "";

        var changePct = (last - first) / first * 100;
        return changePct switch
        {
            < -10 => $"Declining fast ({changePct:+0.#;-0.#}% over period) - consider battery service",
            < -3  => $"Gradual decline ({changePct:+0.#;-0.#}% over period) - normal aging",
            _     => $"Stable ({changePct:+0.#;-0.#}% over period)",
        };
    }

    // Returns a short human-readable description of the cycle count trend.
    private static string BuildCycleTrend(IReadOnlyList<CapacityHistoryEntry> history)
    {
        if (history.Count < 2) return "";

        var first = history[0].CycleCount;
        var last = history[^1].CycleCount;
        var delta = last - first;
        if (delta <= 0) return "";

        // Estimate how many days span the history.
        var days = (history[^1].EndDate - history[0].StartDate).TotalDays;
        if (days > 0)
        {
            var perDay = delta / days;
            return $"+{delta} cycles over period (~{perDay:0.#}/day)";
        }
        return $"+{delta} cycles over period";
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
