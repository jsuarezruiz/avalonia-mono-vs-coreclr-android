# Cold-start comparison

- **Device:** POCO F7 Ultra (24122RKC7G, API 36, arm64-v8a, physical device)
- **Metric:** the app's own fully-drawn mark; both apps log the BENCH fully_drawn mark
- **Runs:** 30 and 30, cold starts interleaved in a rotating order

| App | n | median (fully drawn, ms) | 95% CI | p95 | TotalTime median | PSS (MB) | APK (MB) |
|---|---|---|---|---|---|---|---|
| Mono | 30 | 692 | 683 to 699 | 718 | 705 | 154 | 15.8 |
| CoreCLR | 30 | 294 | 286 to 299 | 329 | 315 | 180 | 21.6 |

**Result:** CoreCLR took 57.6% less time than Mono (median, 95% CI 56.7% to 58.8%), 0.40 s sooner.

`less time` is (slower - faster) / slower, on the medians.

Checks passed: real device, quiet host, enough runs, the metric agrees with TotalTime.
