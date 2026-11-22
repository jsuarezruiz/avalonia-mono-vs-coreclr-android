# The bench tool

`tools/Bench` is a small C# command line. It builds the test apps, measures their cold start on a device and turns the
result into a report and a side-by-side video. It also works on any two Android apps, not only the ones in this repo:
give it two APKs, or entries like `net10/mono-aot-profiled`. The two apps are installed side by side, so their package
ids must differ.

Run it with `dotnet run --project tools/Bench -c Release -- <command>`, or build it once with
`dotnet build tools/Bench -c Release` and call `tools/Bench/bin/Release/net10.0/bench`. The examples below just say
`bench`. The unit tests run with `dotnet test tools/Bench.Tests`.

## Commands

| Command | What it does |
|---|---|
| `build [variant...]` | Builds this repo's apps with the right SDK for each track, then opens every APK to check it has the runtime its variant says (`libmonosgen-2.0.so` or `libcoreclr.so`) |
| `compare <left> <right>` | Installs both apps, warms them up, does 30 interleaved cold starts each, records 7 launches each, then writes the report and the video |
| `run [app...]` | Only the measuring, for any number of apps. With none named it uses every built entry |
| `frames [app...]` | Frame pacing for the sparkle app: four effects, idle and touch |
| `record <left> <right>` | Records launches and renders the video. `--stats` takes the numbers from a full session |
| `render <session>` | Rebuilds the report and video from a session folder |
| `stats <csv or folder>...` | Summarises startup and frame CSVs: medians, p95 and a bootstrap interval against a baseline |
| `devices` | Lists connected devices (emulators show their AVD name) |

`bench --help` lists every option. The ones you will use most are `--runs`, `--takes`, `--serial`, `--out`,
`--device-label` and `--strict`.

The `net11` track builds with the SDK pinned in `eng/tracks/net11/global.json`. If a `.dotnet11` folder exists at the
repo root (an isolated SDK install, ignored by git), it uses that one instead. For an x64 emulator add
`--rid android-x64`; the default is `android-arm64`.

## How a launch is measured

Every run starts a new process: `am force-stop`, a 3 second pause, then `am start -W`. Each run records the system's
`TotalTime`, the app's own `fully_drawn` mark when it logs one (the apps here do, under the `BENCH` log tag), Android's
"Fully drawn" time for apps that call `ReportFullyDrawn`, memory (PSS) and the host's load. The comparison uses the best
number both apps have and never mixes sources: the app's mark, otherwise Android's time, otherwise `TotalTime`.

The app's marks are milliseconds since the process was forked. `fully_drawn` is one frame after the first frame that
contains the main view, so it means the screen has been rendered. The other marks, in order, are `application_ctor`,
`framework_init`, `view_loaded` and `first_frame`.

"Cold" means a new process. The APK is already installed and has been launched once, and the OS file cache is not
dropped (that needs root, `--drop-caches`). It is what a user sees when reopening an app, not the first launch after an
install or a reboot. [`methodology.md`](methodology.md) has the details and the limits.

For frame pacing, the sparkle app drives itself. Per effect and phase (idle, then touch) it logs the mean, p50, p95, p99
and max gap between frames, the number of slow frames (longer than 1.5 refresh intervals), GC counts and allocated
memory, plus the refresh rate. A healthy 60 Hz app sits near 16.7 ms.

## The video

`compare` and `record` make a 1920x1080 video. Both phones start together, each with a running clock and a progress bar
that stop when its screen is finished, and a result at the end. The video shows a measurement, it isn't the
measurement. A few things to know when reading it:

- The two sides are recorded one after the other on the same device, alternating, then lined up on the launch.
  Recording two phones at once would make them compete for the CPU.
- Each side plays the recorded run closest to the median of the 30, never the best one.
- The clock stops at that run's own measurement, the same one the statistics use. The system's splash animation, about
  0.4 s, plays after that and isn't counted.
- The result ("N% less time") comes from the statistics with a 95% interval, not from the clip. If the interval reaches
  zero it says "no clear difference".
- The phone's name appears under the title. `--device-label "POCO F5"` overrides what the phone reports.

`bench` prints NOT READY TO PUBLISH, and writes the reasons to `compare.json`, when it ran on an emulator, had fewer
than 30 runs, ran on a busy host, hit the resolver stall, found the metric and `TotalTime` disagreeing, found an interval
that reaches zero, or picked a clip whose finishing order contradicts the statistics. With `--strict` that becomes exit
code 2.

A session lands in `results/bench/<time>_<device>/`: `runs.csv`, `takes.csv`, `report.md`, `compare.json`, the video and
the chosen clips. Videos and clips are ignored by git, so attach the video to a post or a release. Commit `runs.csv` next
to any video you share.

## Using a real phone

1. Turn on developer options (Settings, About phone, tap the OS version seven times) and switch on USB debugging. On
   Xiaomi, POCO and Redmi phones also switch on Install via USB, or `adb install` is refused. The menu names vary a bit
   between HyperOS and MIUI. "USB debugging (Security settings)" isn't needed: `bench` goes to the home screen by
   starting the Home activity, which needs no input permission.
2. Connect it by USB, set the USB mode to File transfer and accept the "Allow USB debugging?" prompt. `bench devices`
   should list it.
3. Switch on Stay awake in Developer options and keep the phone unlocked for the whole run. Use airplane mode, a fixed
   brightness and no battery saver, and leave the computer idle. `bench` checks that the screen is on and that Stay
   awake is on, and stops with the fix if not. It can't unlock the phone.
4. The video shows each phone's home screen for half a second before the app opens, and the status bar all the way
   through. `bench` blurs the home screen by default (`--show-home` turns that off) but not the status bar, so turn on
   Do Not Disturb and clear your notifications first.
5. Run `bench compare ... --serial <id> --strict`. The 30 runs per app take about ten minutes, then it records the clips.

## Build notes

The SDK is pinned per track: 10.0.100 for `net10` and 11.0.100 for `net11`, rolling forward within the feature band.
`bench build` builds from the track's folder because `dotnet` picks its SDK from the working directory. The root
`global.json` covers everything else and pins 10.0.100.

`Directory.Packages.props` pins Avalonia 12.1.6. The copied vignette code pins the same version in
`third_party/avalonia_vignettes/Directory.Build.props`, so change both together. CoreCLR on Android is flagged
experimental on .NET 10 and prints build warning `XA1040`; it isn't on .NET 11. Mono builds fail on .NET 11
(`NETSDK1242`), so that track only builds `coreclr`.
