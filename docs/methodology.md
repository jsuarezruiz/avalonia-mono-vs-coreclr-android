# Methodology

The goal is numbers someone else can reproduce and disagree with. This page lists what is controlled, what is
not, and what to report alongside any result.

## Design decisions

**Only the runtime changes.** Variants differ solely by the properties in `eng/Variants.props`. Trimming mode,
ABI, Avalonia version, fonts, theme and app code are identical. If you add a variant, add it there and nowhere
else.

**Interleaved, rotating order.** All variants are installed side by side (one package id each) and the runner
cycles through them every round, rotating the starting variant. Thermal throttling, background jobs and battery
drift then affect every variant alike, instead of penalising whichever ran last.

**Cold start means a fresh process, not an empty page cache.** Each measured launch is preceded by
`am force-stop`, so a new process is forked and the runtime boots from scratch. The APK is already installed and
has been launched once (the warm-up), which absorbs first-launch extraction and caching. The OS page cache is
*not* dropped unless `--drop-caches` works on your device (it needs root). That matches what users experience
when they reopen an app, not the first launch after install. State which one you measured.

**Two independent clocks.** `am start -W` (`TotalTime`) is measured by the system. The `fully_drawn` mark is
measured inside the app, from process fork to one frame after the dashboard is first rendered. If the two
disagree on direction, treat the result with suspicion.

**Medians and percentiles, never single runs.** `bench stats` reports the median and p95 per variant and a 95%
bootstrap interval for the difference of medians against the baseline. A delta whose interval crosses zero is not
a result.

**The APK proves the variant.** `bench build` fails if the APK contains the wrong runtime library, so a build-property
typo can't silently benchmark Mono against Mono.

**Frame pacing is driven by the app, not by adb.** The sparkle app plays the four effects itself, using the same
public `Show` and `Touch` calls the vignette's own capture tool uses, and a synthetic pointer that circles the
screen on a fixed 3 s period. Nothing is injected over adb, so input latency and `adb shell input` jitter can't
leak into the numbers. The particle simulation uses a fixed random seed, so every run and every runtime simulates
the same particles.

**Frame intervals are the spacing of rendered frames.** They come from the timestamps `RequestAnimationFrame`
hands out, so a UI thread that can't keep up shows up as long intervals. They depend on the display: read them
against the `refresh_hz` column, and don't compare a 60 Hz device with a 120 Hz one.

**Frame runs include the runtime warming up.** Each run is a fresh process, and the first effect plays while the
runtime is still tiering up (CoreCLR) or falling back to the JIT for code the AOT step didn't cover (Mono). That
is deliberate: it is part of what a user sees. If you want steady state only, compare the later effects.

## Threats to validity

- **A busy host.** The emulator shares the host's CPUs, so anything else running changes every timing. Each row records
  the host's 1-minute load average and CPU count (`host_load`, `host_cpus`), and `bench stats` warns when the load
  exceeded the CPU count. Interleaving the entries spreads drift evenly, but it cannot rescue a saturated machine.

- **Display refresh rate.** Intervals scale with it, and some devices change it dynamically. Pin the refresh rate
  in developer options where you can.
- **GC counters.** `gc0`..`gc2` and `alloc_mb` come from `System.GC`. They are comparable between runtimes as
  trends, not as exact accounting, since the two garbage collectors are different implementations.
- **A particle demo is a stress test, not an app.** It shows how the runtimes handle a hot managed loop feeding a
  native renderer. It says little about layout-heavy business UI; that is what the dashboard is for.

- **Emulators.** They share the host CPU and storage, so absolute numbers (and sometimes the ranking) don't
  transfer to phones. `bench` flags emulator runs and `bench stats` prints a warning for them.
- **One app, one screen.** A dashboard with a handful of controls says little about a 300-view enterprise app.
  CoreCLR vs Mono differences can grow or shrink with app size, generic instantiations and reflection use.
- **Thermals and battery.** Charge the device, close other apps, keep the room temperature stable, and use the same
  cooldown for every variant. Prefer airplane mode and a fixed screen brightness.
- **Runtime maturity.** CoreCLR on Android was experimental in .NET 10. Results describe a snapshot of the
  SDK, workload and Avalonia versions recorded in the CSV's session, not the runtime in general.
- **Package size** is for a single-ABI, trimmed, debug-signed APK. Store-delivered sizes (AAB, split APKs,
  multiple ABIs) will differ.
- **Memory** is one `dumpsys meminfo` sample a second after `fully_drawn`; it is a coarse indicator, not a
  profile.

## What to report with any claim

1. Device model, Android version, chipset and whether it was an emulator (all in the CSV)
2. SDK and workload versions for each track: `dotnet --info` and `dotnet workload list`
   (the `net11` track needs the host fix from dotnet/android#12863, see repros/coreclr-android-pinvoke-stall)
3. Avalonia version (see `Directory.Packages.props`)
4. Number of runs, warm-up count, cooldown, whether caches were dropped
5. The raw CSVs under `results/raw/`, committed next to the claim
6. The full `bench stats` output (or `report.md`), not just the headline percentage

## Phrasing

"X% faster" is ambiguous. Prefer "median time to fully drawn fell from A ms to B ms (-N%, 95% CI [lo, hi]) on
DEVICE, n=RUNS". The delta column in `bench stats` is defined as `(median(variant) - median(baseline)) /
median(baseline)`, so a negative value means the variant is faster.

## Before you share a comparison

A checklist. `bench` checks the mechanical items for a video and prints what is missing.

1. **Real phone, quiet machine.** Not an emulator. Host idle, device charged, airplane mode, fixed brightness, the
   same cooldown before every run. Two devices (a mid-range and a flagship) are better than one.
2. **At least 30 interleaved cold starts per entry**, one warm-up discarded. Report the median, p95 and the 95%
   bootstrap interval from `bench stats`, not a mean of a handful of runs: startup times are skewed and one slow run
   drags a mean. If the interval reaches zero, the result is "no clear difference".
3. **Both clocks agree.** The app's `fully_drawn` mark and the system's `am start -W` TotalTime must point the same way.
4. **No cherry-picking.** Publish the raw CSVs and the commit, list any excluded runs and why, and check that the
   `native_lib_timeouts` column is 0.
5. **Say exactly what is compared.** Mono is not available on .NET 11, so the pair is ".NET 10 Mono (AOT, profiled,
   the default) vs .NET 11 CoreCLR (ReadyToRun, the default)". Frame it as what you get by upgrading, not as
   "CoreCLR is faster than Mono". Same Avalonia version and same app on both sides, with the SDK and workload versions
   printed in the post.
6. **Report the trade-off.** Package size is part of the story (a CoreCLR APK is larger than a profiled-AOT Mono one).
   This repo measures startup, memory and frame pacing; claim nothing you did not measure.
7. **A video illustrates, the statistics are the claim.** Show the run closest to the median, label it, and print the
   median, interval, n and device next to it.
8. **Wording.** "Median time to fully drawn fell from A to B (-N%, 95% CI [lo, hi]) on DEVICE, n=30", not "N% faster".
9. **Date and versions.** Results are a snapshot of the SDK, workload and Avalonia versions recorded in the CSV.
