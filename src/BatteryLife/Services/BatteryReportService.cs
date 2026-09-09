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
                $"powercfg couldn't generate a battery report (exit code {process.ExitCode}). " +
                "This can happen if the device has no battery. " + stderr.Trim());
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

        var batteryEl = root.Element(Ns + "Batteries")?.Elements(Ns + "Battery").FirstOrDefault()
            ?? throw new InvalidOperationException("No battery was found on this device.");

        var battery = new BatteryInfo(
            batteryEl.Element(Ns + "Id")?.Value ?? "Battery",
            batteryEl.Element(Ns + "Manufacturer")?.Value?.Trim() ?? "Unknown",
            batteryEl.Element(Ns + "Chemistry")?.Value ?? "Unknown",
            ParseMilliwattHoursToWh(batteryEl.Element(Ns + "DesignCapacity")?.Value),
            ParseMilliwattHoursToWh(batteryEl.Element(Ns + "FullChargeCapacity")?.Value),
            ParseInt(batteryEl.Element(Ns + "CycleCount")?.Value));

        var runtimeEl = root.Element(Ns + "RuntimeEstimates");
        var designEstimate = ParseRuntimeEstimate(runtimeEl?.Element(Ns + "DesignCapacity"));
        var fullChargeEstimate = ParseRuntimeEstimate(runtimeEl?.Element(Ns + "FullChargeCapacity"));

        var history = (root.Element(Ns + "History")?.Elements(Ns + "HistoryEntry") ?? Enumerable.Empty<XElement>())
            .Select(ParseHistoryEntry)
            .OrderBy(h => h.StartDate)
            .ToList();

        return new BatteryReportData(scanTime, system, battery, designEstimate, fullChargeEstimate, history);
    }

    private static RuntimeEstimate ParseRuntimeEstimate(XElement? el)
    {
        if (el is null)
        {
            return new RuntimeEstimate(0, null, null);
        }

        return new RuntimeEstimate(
            ParseMilliwattHoursToWh(el.Element(Ns + "Capacity")?.Value),
            ParseDuration(el.Element(Ns + "ActiveRuntime")?.Value),
            ParseDuration(el.Element(Ns + "ConnectedStandbyRuntime")?.Value));
    }

    private static CapacityHistoryEntry ParseHistoryEntry(XElement el)
    {
        return new CapacityHistoryEntry(
            ParseDateTime(el.Attribute("LocalStartDate")?.Value ?? el.Attribute("StartDate")?.Value) ?? DateTime.MinValue,
            ParseDateTime(el.Attribute("LocalEndDate")?.Value ?? el.Attribute("EndDate")?.Value) ?? DateTime.MinValue,
            ParseMilliwattHoursToWh(el.Attribute("DesignCapacity")?.Value),
            ParseMilliwattHoursToWh(el.Attribute("FullChargeCapacity")?.Value),
            ParseInt(el.Attribute("CycleCount")?.Value));
    }

    private static DateTime? ParseDateTime(string? raw) =>
        DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt : null;

    private static double ParseMilliwattHoursToWh(string? raw) =>
        double.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var mwh) ? mwh / 1000.0 : 0;

    private static int ParseInt(string? raw) => int.TryParse(raw, out var value) ? value : 0;

    private static TimeSpan? ParseDuration(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return XmlConvert.ToTimeSpan(raw); }
        catch (FormatException) { return null; }
    }
}
