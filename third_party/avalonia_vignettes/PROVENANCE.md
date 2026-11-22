# Provenance

This folder is a trimmed copy of <https://github.com/jsuarezruiz/avalonia_vignettes>, taken at commit
`45adc7a6399a6de6319da1a8a19b65dcceaacd8a` (2026-09-24, "Added yml to publish samples"). The vignette is the
**Sparkle Party Particles** screen, an Avalonia port of
[gskinner's Flutter Vignette `sparkle_party`](https://github.com/gskinnerTeam/flutter_vignettes/tree/master/vignettes/sparkle_party).

It is used as the render-heavy scenario of this benchmark: thousands of particles are simulated in managed code
every frame and handed to Skia in a single `DrawVertices` call, so the managed hot loop is what differs between
runtimes while the native drawing stays the same.

## What was copied

| Path here | Upstream path |
|---|---|
| `LICENSE`, `THIRD-PARTY-NOTICES.md`, `LICENSES/` | same |
| `Directory.Build.props` | same (one line changed, see below) |
| `vignettes/_hosts/Vignette.Core.props` | same |
| `vignettes/_shared/` | same, whole project |
| `vignettes/sparkle_party/SparkleParty/` | same, whole project (code and assets) |

Not copied: the desktop, iOS and Android hosts, the browser showcase, the other vignettes, README images and
upstream CI. The Android head used for benchmarking lives in `src/BenchApp.Sparkle.Android` and subclasses
`SparkleParty.App` instead of modifying it.

## Local changes

1. `Directory.Build.props`: `AvaloniaVersion` 12.1.0 -> 12.1.6, to match the rest of this repo.
2. `Directory.Packages.props` (new file): switches central package management off for this subtree, because the
   vignette projects pin versions on each `PackageReference`.
3. `vignettes/sparkle_party/SparkleParty/Effects/Rnd.cs`: `Random.Shared` -> `new Random(20260101)`, so the
   simulated particles are identical on every run and under every runtime.

Everything else is byte-for-byte upstream. To refresh, re-copy from the upstream commit you want and re-apply the
three changes above.

## Licences

The code is MIT, copyright Javier Suárez. The vignettes are ports of gskinner's MIT-licensed originals, so the
gskinner copyright and permission notice in `THIRD-PARTY-NOTICES.md` has to stay with any copy. The bundled fonts
(Titillium Web, Inter) are covered by `LICENSES/fonts`.
