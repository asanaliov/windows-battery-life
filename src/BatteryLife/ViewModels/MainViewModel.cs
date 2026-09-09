using System.Windows.Media;
using BatteryLife.Models;
using BatteryLife.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;

namespace BatteryLife.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private static readonly SolidColorBrush GoodBrush = new(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly SolidColorBrush FairBrush = new(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush PoorBrush = new(Color.FromRgb(0xEF, 0x44, 0x44));

    private readonly BatteryReportService _service = new();

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private string? errorMessage;

    [ObservableProperty] private string deviceName = "";
    [ObservableProperty] private string lastScanText = "";

    [ObservableProperty] private double healthPercent;
    [ObservableProperty] private string healthText = "";
    [ObservableProperty] private Brush healthBrush = PoorBrush;

    [ObservableProperty] private string cycleCountText = "";
    [ObservableProperty] private string designCapacityText = "";
    [ObservableProperty] private string fullChargeCapacityText = "";

    [ObservableProperty] private string fullChargeActiveText = "";
    [ObservableProperty] private string fullChargeStandbyText = "";
    [ObservableProperty] private string designActiveText = "";
    [ObservableProperty] private string designStandbyText = "";

    [ObservableProperty] private bool hasHistory;

    public ISeries[] CapacitySeries { get; private set; } = [];
    public ISeries[] CycleSeries { get; private set; } = [];
    public Axis[] HistoryXAxes { get; private set; } = [];

    public MainViewModel()
    {
        _ = RefreshAsync();
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

    private void Apply(BatteryReportData report)
    {
        DeviceName = string.IsNullOrWhiteSpace(report.System.ProductName)
            ? report.System.ComputerName
            : report.System.ProductName;
        LastScanText = $"Report generated {report.ScanTime:g}";

        HealthPercent = report.Battery.DesignCapacityWh > 0
            ? Math.Round(report.Battery.FullChargeCapacityWh / report.Battery.DesignCapacityWh * 100, 1)
            : 0;
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

            CapacitySeries =
            [
                new LineSeries<double>
                {
                    Name = "Full charge capacity (Wh)",
                    Values = report.History.Select(h => h.FullChargeCapacityWh).ToArray(),
                    GeometrySize = 4,
                    Fill = null,
                },
                new LineSeries<double>
                {
                    Name = "Design capacity (Wh)",
                    Values = report.History.Select(h => h.DesignCapacityWh).ToArray(),
                    GeometrySize = 0,
                    Fill = null,
                },
            ];
            CycleSeries =
            [
                new LineSeries<int>
                {
                    Name = "Cycle count",
                    Values = report.History.Select(h => h.CycleCount).ToArray(),
                    GeometrySize = 4,
                    Fill = null,
                },
            ];
            HistoryXAxes = [new Axis { Labels = labels, LabelsRotation = 45 }];
        }
        else
        {
            CapacitySeries = [];
            CycleSeries = [];
            HistoryXAxes = [];
        }

        OnPropertyChanged(nameof(CapacitySeries));
        OnPropertyChanged(nameof(CycleSeries));
        OnPropertyChanged(nameof(HistoryXAxes));
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
