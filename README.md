# Windows Battery Life

A small native Windows (WPF/.NET) desktop app that shows your battery's health, charge cycles, and battery life estimates at a glance — no more manually running `powercfg /batteryreport` and digging through an HTML file.

Under the hood it runs `powercfg /batteryreport /xml`, parses the report, and displays:

- Battery health (design capacity vs. current full-charge capacity, degradation %)
- Cycle count
- Estimated battery life at design capacity vs. current capacity
- Capacity and cycle count history over time (chart)

## Requirements

- Windows 10/11
- .NET 8 SDK (or later) to build

## Running

```
dotnet run --project src/BatteryLife
```
