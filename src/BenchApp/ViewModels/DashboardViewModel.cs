using System.Collections.Generic;

namespace BenchApp.ViewModels;

/// <summary>A small summary card. <see cref="Progress"/> (0..100) adds a progress bar when set.</summary>
public sealed record Tile(string Label, string Value, string Caption, double? Progress);

/// <summary>One line of the hero legend. <see cref="StyleKey"/> picks the colour in the view's styles.</summary>
public sealed record FlowStat(string Label, string Value, string StyleKey);

/// <summary>One smart device. <see cref="StyleKey"/> picks the icon and colours in the view's styles.</summary>
public sealed record Device(string Name, string Status, string Power, string StyleKey, bool IsOn);

/// <summary>
/// Static, in-memory data for a fictional home-energy screen. Pure data: no I/O, no randomness and nothing that
/// knows about brushes or geometry, so every run builds exactly the same visual tree and any difference between
/// runtimes comes from the runtime, not from the app. How it looks is decided by the styles and resources.
/// </summary>
public sealed class DashboardViewModel
{
    public string Greeting => "Good afternoon, Mara";
    public string DateLine => "Thursday, 12 Nov";
    public string Initial => "M";

    public string HeroLabel => "ENERGY TODAY";
    public string HeroNote => "Peak 5.8 kW at 13:10";
    public double GaugeFraction => 0.82;
    public string SelfPoweredValue => "82";
    public string SelfPoweredCaption => "self-powered";
    public IReadOnlyList<FlowStat> FlowStats { get; } =
    [
        new FlowStat("Solar", "18.4 kWh", "solar"),
        new FlowStat("Home", "12.1 kWh", "home"),
        new FlowStat("To grid", "6.3 kWh", "grid"),
    ];

    public IReadOnlyList<string> Ranges { get; } = ["Day", "Week", "Month", "Year"];

    public string ChartTitle => "Power flow";
    public string ChartSubtitle => "Solar and household, in kW";

    /// <summary>Hourly kW from 06:00 to 21:00.</summary>
    public IReadOnlyList<double> SolarKw { get; } =
        [0.1, 0.6, 1.5, 2.8, 4.1, 5.2, 5.8, 5.6, 4.7, 3.5, 2.2, 1.1, 0.4, 0.1, 0.0, 0.0];

    /// <summary>Hourly kW from 06:00 to 21:00.</summary>
    public IReadOnlyList<double> HomeKw { get; } =
        [0.7, 1.5, 1.9, 1.4, 1.2, 1.6, 2.2, 2.4, 1.8, 1.7, 2.4, 3.1, 3.2, 2.6, 1.6, 0.9];

    public double ChartMaximum => 6.6;

    /// <summary>Where 13:40 falls between 06:00 (0) and 21:00 (1).</summary>
    public double NowPosition => (13 + 40 / 60.0 - 6) / 15;

    public IReadOnlyList<string> HourLabels { get; } = ["6:00", "9:00", "12:00", "15:00", "18:00", "21:00"];

    public IReadOnlyList<Tile> Tiles { get; } =
    [
        new Tile("Battery", "74%", "Charging", 74),
        new Tile("Grid export", "6.3", "kWh exported", null),
        new Tile("Tariff", "€0.18", "per kWh now", null),
    ];

    public string DevicesTitle => "Smart devices";
    public string DevicesAction => "Manage";
    public IReadOnlyList<Device> Devices { get; } =
    [
        new Device("EV charger", "Charging, 62% full", "7.4 kW", "ev", true),
        new Device("Heat pump", "Eco mode, 21.5 °C", "1.2 kW", "heat", true),
        new Device("Dishwasher", "Starts at 14:00", "Scheduled", "dishwasher", true),
        new Device("Pool pump", "Paused, surplus is low", "0 kW", "pool", false),
        new Device("Water heater", "Boost until 15:30", "2.0 kW", "water", true),
    ];

    public string TipTitle => "Best run time: 13:00";
    public string TipBody => "Solar covers 94% of a dishwasher cycle.";
    public string TipAction => "Schedule";

    public static DashboardViewModel CreateSample() => new();
}
