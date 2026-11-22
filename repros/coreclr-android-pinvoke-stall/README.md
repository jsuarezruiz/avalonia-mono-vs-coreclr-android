# CoreCLR on Android: a blocked thread waiting on a first P/Invoke into `android` stalls for 6 s

A background thread makes its first P/Invoke into the system library `android` (`AChoreographer_getInstance`, the
call Avalonia.Android's `ChoreographerTimer` makes). If another thread **blocks waiting for that call to return**,
the .NET for Android CoreCLR host takes 6 s, in two 3-second timeouts. If nobody blocks, the same call returns in
well under a second. Mono is unaffected. There is no Avalonia in this app.

```
W monodroid-assembly: Timeout while waiting for shared library 'libandroid.so' to load.
W monodroid-assembly: Timeout while waiting for shared library 'android' to load.
```

## Root cause

This is dotnet/android issue [#12826](https://github.com/dotnet/android/issues/12826), "[CoreCLR] [P/Invoke] Possible
deadlocks on system library calls" (filed 2026-09-18 against .NET 11 RC1, milestone .NET 11), fixed by pull request
[#12863](https://github.com/dotnet/android/pull/12863), "[CoreCLR] Avoid main-thread dispatch for unknown P/Invokes"
(merged into `main` on 2026-09-22).

The PR description explains it. The CoreCLR host treats every native library that is missing from the generated DSO
cache as a possible JNI library. On a managed worker thread, loading such a library goes through
`System.loadLibrary()` dispatched synchronously to the Android main looper. `libandroid.so` is a system library, so
it counts as "unknown". If the main thread is blocked waiting for that worker, the worker waits for the main thread
and nothing moves until the loader's 3 s timeout. Two names are tried (`libandroid.so`, then `android`), hence 2 x 3 s.
The issue's own example is `GLSurfaceView` waiting for its GL thread while that thread resolves `libGLESv2`.

### Which builds contain the fix

Checked by commit ancestry: each runtime pack's nuspec records the dotnet/android commit it was built from, and GitHub's
compare API tells whether the PR's merge commit (`863bacf`) is an ancestor of it. Only the GA row was also verified by running it (see Results).

| Runtime pack | Built | Contains the fix |
|---|---|---|
| `Microsoft.Android.Runtime.CoreCLR.36` 36.1.118 (.NET 10 servicing) | 2026-09-11 | no |
| `Microsoft.Android.Runtime.CoreCLR.37` 37.0.0-rc.1.2257 (.NET 11 RC1) | 2026-08-31 | no |
| `Microsoft.Android.Runtime.CoreCLR.37` 37.2.0-rc.2.84 (.NET 11 RC2) | 2026-09-25 | yes |
| `Microsoft.Android.Runtime.CoreCLR.37` 37.2.31 (latest stable on nuget.org) | 2026-11-06 | yes |

The 36.1.2 and 36.1.99-preview.1 packs that were actually run here predate the fix. The "Timeout while waiting for
shared library" string is still present in the fixed packs, which is expected: the loader timeout stays for JNI
libraries, the fix only stops system libraries from going through it.

## Run it

The mode picks where, and whether, the main thread blocks:

| `-p:ProbeMode=` | What the main thread does |
|---|---|
| `app` | blocks on the result inside `Application.OnCreate` |
| `activity` | blocks on the result inside `Activity.OnCreate` |
| `async` | starts the thread and carries on |

```bash
dotnet publish -c Release -r android-arm64 -p:UseMonoRuntime=false -p:ProbeMode=activity -o out
```

```bash
adb install -r out/com.companyname.ClrProbe-Signed.apk
```

```bash
adb shell am start -W -n com.companyname.ClrProbe/clrprobe.MainActivity
```

```bash
adb logcat -d -s CLRPROBE:I monodroid-assembly:W
```

Use `-p:UseMonoRuntime=true` for the Mono control. Everything is in [`MainActivity.cs`](MainActivity.cs). This project
targets `net10.0-android`; to try .NET 11, change the target framework and install that SDK and workload.

## Results

arm64 Android 34 emulator (google_apis_playstore, Apple Silicon host), .NET SDK 10.0.100, Android workload 36.1.2,
Release. Two or three cold launches per row; the numbers were stable.

| Runtime | Main thread | Time for the call to complete | Resolver timeouts | `am start -W` TotalTime |
|---|---|---|---|---|
| CoreCLR | blocks, in `Application.OnCreate` | 6.0 s | 2 | 6.3 s |
| CoreCLR | blocks, in `Activity.OnCreate` | 6.0 s | 2 | 6.3-6.5 s |
| CoreCLR | does not block | 0.07-0.3 s | 0 | 0.5-1.4 s |
| Mono | blocks, in `Application.OnCreate` | 0.015 s | 0 | 0.7-1.0 s |
| CoreCLR on **.NET 11.0.100 GA** (workload 37.2.31) | blocks, in `Activity.OnCreate` | 0.004-0.08 s | 0 | 0.8-1.6 s |

The same stall shows up in an Avalonia app on the .NET 11 Preview 1 SDK (Android workload 36.1.99-preview.1.120), which
also predates the fix. The minimal repro itself was only run on 10.0.100.

## How it reaches Avalonia apps

Avalonia 12.1.2 changed `ChoreographerTimer.cs` (PR #22045, "Raise TopLevel.Closed from managed Dispose() paths",
which removed the `_views` tracking from that file). Up to 12.1.1, `PostFrameCallbackIfNeeded()` returned early until a view had subscribed, so the
`_choreographer.Task.Result` wait happened later, after the background thread had already finished its P/Invoke.
From 12.1.2 the gate (`_views.Count == 0`) is gone, so the thread that sets `Tick` blocks on that result during app
startup, which is exactly the pattern above.

An untouched `dotnet new avalonia.xplat` Android app on CoreCLR, only the Avalonia version changed:

| Avalonia.Android | TotalTime | Resolver timeouts |
|---|---|---|
| 12.0.4, 12.0.5, 12.1.0, 12.1.1 | 0.4-1.2 s | 0 |
| 12.1.2, 12.1.6 | 6.4-6.7 s | 2 per launch |

(12.1.3 to 12.1.5 were not run; the bisect between 12.1.1 and 12.1.6 only needed 12.1.1 and 12.1.2.)

## What is not known

- **Other .NET 11 builds.** The 11.0.100 GA SDK was run (see the table; the Avalonia dashboard on it also shows no
  timeouts). RC2 and the other packs in the table were judged by ancestry only.
- **Real devices.** Only an emulator was used. The upstream PR was validated on a physical device.
- **A .NET 10 backport.** The 36.1.118 servicing pack does not contain the fix, and CoreCLR is experimental on .NET 10.

## Workarounds

- Use the .NET 11 toolchain (verified on 11.0.100) for CoreCLR on Android.
- On .NET 10 CoreCLR, use Avalonia 12.1.1 or earlier, or stay on Mono.
- Avalonia could avoid blocking its caller (wait asynchronously, or restore the gate), which would also help anyone on
  a host without the fix.
