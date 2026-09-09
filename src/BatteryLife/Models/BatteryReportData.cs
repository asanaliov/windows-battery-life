namespace BatteryLife.Models;

public record SystemInfo(string ComputerName, string Manufacturer, string ProductName, bool ConnectedStandby);

public record BatteryInfo(
    string Id,
    string Manufacturer,
    string Chemistry,
    double DesignCapacityWh,
    double FullChargeCapacityWh,
    int CycleCount);

public record RuntimeEstimate(double CapacityWh, TimeSpan? ActiveRuntime, TimeSpan? ConnectedStandbyRuntime);

public record CapacityHistoryEntry(
    DateTime StartDate,
    DateTime EndDate,
    double DesignCapacityWh,
    double FullChargeCapacityWh,
    int CycleCount);

public record BatteryReportData(
    DateTime ScanTime,
    SystemInfo System,
    BatteryInfo Battery,
    RuntimeEstimate DesignEstimate,
    RuntimeEstimate FullChargeEstimate,
    IReadOnlyList<CapacityHistoryEntry> History);
