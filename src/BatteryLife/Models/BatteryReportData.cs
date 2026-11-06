namespace BatteryLife.Models;

public record SystemInfo(string ComputerName, string Manufacturer, string ProductName, bool ConnectedStandby);

/// <summary>
/// The device's battery. On devices with several batteries the capacities are summed
/// and <see cref="BatteryCount"/> says how many were combined.
/// </summary>
public record BatteryInfo(
    string Id,
    string Manufacturer,
    string Chemistry,
    double DesignCapacityWh,
    double FullChargeCapacityWh,
    int CycleCount,
    int BatteryCount);

public record RuntimeEstimate(double CapacityWh, TimeSpan? ActiveRuntime, TimeSpan? ConnectedStandbyRuntime);

public record CapacityHistoryEntry(
    DateTime StartDate,
    DateTime EndDate,
    double DesignCapacityWh,
    double FullChargeCapacityWh,
    int CycleCount,
    TimeSpan? EstimatedDesignStandby,
    TimeSpan? EstimatedFullChargeStandby);

public record BatteryReportData(
    DateTime ScanTime,
    SystemInfo System,
    BatteryInfo Battery,
    RuntimeEstimate DesignEstimate,
    RuntimeEstimate FullChargeEstimate,
    IReadOnlyList<CapacityHistoryEntry> History);
