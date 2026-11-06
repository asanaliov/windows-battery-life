using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using BatteryLife.Models;

namespace BatteryLife.Services;

/// <summary>
/// Runs `powercfg /batteryreport` and parses the resulting XML into strongly-typed data.
/// </summary>
public class BatteryReportService
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/battery/2012";
    private static readonly TimeSpan MaximumPlausibleStandby = TimeSpan.FromDays(90);

    public async Task<BatteryReportData> GetReportAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"battery-report-{Guid.NewGuid():N}.xml");
        try
        {
            await RunPowercfgAsync(path);
            return ParseReport(path);
        }
        finally
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }
    }

    private static async Task RunPowercfgAsync(string outputPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powercfg",
            Arguments = $"/batteryreport /xml /output \"{outputPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start powercfg.exe.");

        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stderr = await stderrTask;

        if (process.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new InvalidOperationException(
                "Windows couldn't generate a battery report. This usually means this PC has no battery. " +
                $"(powercfg exit code {process.ExitCode}) {stderr.Trim()}".TrimEnd());
        }
    }

    private static BatteryReportData ParseReport(string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root ?? throw new InvalidOperationException("Battery report was empty.");

        var scanTime = ParseDateTime(root.Element(Ns + "ReportInformation")?.Element(Ns + "LocalScanTime")?.Value)
            ?? DateTime.Now;

        var sysEl = root.Element(Ns + "SystemInformation");
        var system = new SystemInfo(
            sysEl?.Element(Ns + "ComputerName")?.Value ?? "This PC",
            sysEl?.Element(Ns + "SystemManufacturer")?.Value ?? "",
            sysEl?.Element(Ns + "SystemProductName")?.Value ?? "",
            sysEl?.Element(Ns + "ConnectedStandby")?.Value == "1");

        var batteryEls = root.Element(Ns + "Batteries")?.Elements(Ns + "Battery").ToList() ?? [];
        if (batteryEls.Count == 0)
        {
            throw new InvalidOperationException("No battery was found on this device.");
        }

        var first = batteryEls[0];
        var battery = new BatteryInfo(
            first.Element(Ns + "Id")?.Value ?? "Battery",
            first.Element(Ns + "Manufacturer")?.Value?.Trim() ?? "Unknown",
            first.Element(Ns + "Chemistry")?.Value ?? "Unknown",
            batteryEls.Sum(b => ParseMilliwattHoursToWh(b.Element(Ns + "DesignCapacity")?.Value)),
            batteryEls.Sum(b => ParseMilliwattHoursToWh(b.Element(Ns + "FullChargeCapacity")?.Value)),
            batteryEls.Max(b => ParseCycleCount(b.Element(Ns + "CycleCount")?.Value)),
            batteryEls.Count);

        var history = (root.Element(Ns + "History")?.Elements(Ns + "HistoryEntry") ?? Enumerable.Empty<XElement>())
            .Select(ParseHistoryEntry)
            .OrderBy(h => h.StartDate)
            .ToList();

        var runtimeEl = root.Element(Ns + "RuntimeEstimates");
        var designEstimate = ParseRuntimeEstimate(
            runtimeEl?.Element(Ns + "DesignCapacity"), history, h => h.EstimatedDesignStandby);
        var fullChargeEstimate = ParseRuntimeEstimate(
            runtimeEl?.Element(Ns + "FullChargeCapacity"), history, h => h.EstimatedFullChargeStandby);

        return new BatteryReportData(scanTime, system, battery, designEstimate, fullChargeEstimate, history);
    }

    private static RuntimeEstimate ParseRuntimeEstimate(
        XElement? el,
        IReadOnlyList<CapacityHistoryEntry> history,
        Func<CapacityHistoryEntry, TimeSpan?> historicalStandby)
    {
        if (el is null)
        {
            return new RuntimeEstimate(0, null, null);
        }

        // The report's headline standby estimate can be wildly wrong (over a decade has been
        // seen on real hardware), while the per-period estimates in its history stay sensible.
        var standby = ParseDuration(el.Element(Ns + "ConnectedStandbyRuntime")?.Value);
        if (standby is not null && !IsPlausibleStandby(standby.Value))
        {
            standby = history.Select(historicalStandby).LastOrDefault(s => s is not null && IsPlausibleStandby(s.Value));
        }

        return new RuntimeEstimate(
            ParseMilliwattHoursToWh(el.Element(Ns + "Capacity")?.Value),
            ParseDuration(el.Element(Ns + "ActiveRuntime")?.Value),
            standby);
    }

    private static bool IsPlausibleStandby(TimeSpan standby) =>
        standby > TimeSpan.Zero && standby <= MaximumPlausibleStandby;

    private static CapacityHistoryEntry ParseHistoryEntry(XElement el)
    {
        return new CapacityHistoryEntry(
            ParseDateTime(el.Attribute("LocalStartDate")?.Value ?? el.Attribute("StartDate")?.Value) ?? DateTime.MinValue,
            ParseDateTime(el.Attribute("LocalEndDate")?.Value ?? el.Attribute("EndDate")?.Value) ?? DateTime.MinValue,
            ParseMilliwattHoursToWh(el.Attribute("DesignCapacity")?.Value),
            ParseMilliwattHoursToWh(el.Attribute("FullChargeCapacity")?.Value),
            ParseCycleCount(el.Attribute("CycleCount")?.Value),
            ParseDuration(el.Attribute("EstimatedDesignCsTime")?.Value),
            ParseDuration(el.Attribute("EstimatedFullChargeCsTime")?.Value));
    }

    private static DateTime? ParseDateTime(string? raw) =>
        DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt : null;

    private static double ParseMilliwattHoursToWh(string? raw) =>
        double.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var mwh) ? mwh / 1000.0 : 0;

    private static int ParseCycleCount(string? raw) =>
        int.TryParse(raw, out var value) && value >= 0 ? value : -1;

    private static TimeSpan? ParseDuration(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return XmlConvert.ToTimeSpan(raw); }
        catch (FormatException) { return null; }
    }
}
