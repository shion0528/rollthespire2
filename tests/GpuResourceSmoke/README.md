# GPU dispatch, shared prefix caches and A/B

resource.9 adds exact static-pool interval checks and shared cache checks for
real N/party/composite, R/Rfull, S atomic/stable, C/E/A/T, W and EW executors.
Fixtures compare full nonzero results to legacy paths and canonical CPU oracles,
cover sparse/private inputs, relocated windows, ZZFZ/final seeds, cancellation,
bad ordinals and larger-root-range fallback. W/EW fixtures cover VariantOnly,
not progression/boss/encounter semantic admission. E/A/T numerical fixtures do
not validate capture of live game authority. FullTarget T and direct small-capsule
Rfull are included; other Rfull shapes retain the common hash proof but are not
claimed as full semantic fixture coverage.

`RT2_GPU_SHARED_WORLD_ONLY=1` runs the focused W/EW suite. Shared tests are included
by default; use `-p:EnableSharedPrefixTests=false` only when building the host
against an archived pre-expansion trial DLL. The rejected small-grid code and
tests live only in ignored local evidence, not the production or source package.

resource.8 adds `PrefixCacheScopeChecks`: public/private outputs with and without
the Leafy pre-gate are compared against the legacy hash and a CPU oracle, including
nonzero outputs, the `ZZFZ` suffix, prefix carry, the final seed, header rejection
and cancellation/reuse. Set `RT2_GPU_PREFIX_SCOPE_ONLY=1` for this focused suite.
Private compact input is covered by the new shared table. Pause/resume persistence and safe-cursor
checks are in the independent `tests/CheckpointResumeChecks` host.

This standalone Godot 4.5.1 Mono project exercises the production executor without opening or changing a game session. It uses `ProductionSearchHost` as its assembly name for the existing friend-assembly access.

Build the mod to an isolated output with mod copying disabled, then build this project with `-p:Rt2AssemblyPath=<absolute RolltheSpire2.dll path>`. Do not use a project reference that rebuilds and installs the mod implicitly. The host needs the game's reference assemblies discovered by `Sts2PathDiscovery.props`.

Run the main Godot executable with `--path <this directory> --rendering-method mobile --rendering-driver d3d12` (or `vulkan`). On Windows, the separate console launcher can fail when its path contains spaces; the main executable supports `--log-file <path>` for output. A GPU-backed driver is required: `--headless` and Compatibility do not provide a real RenderingDevice. The test window starts minimized. Exit code 0 plus `RT2_GPU_SMOKE {"phase":"complete","result":"PASS"}` is success. No benchmark result is a calibration record.

Coverage includes argument counts 0/1/511/512/513, exact capacities and overflow, preservation of the header, production direct/indirect ordinal and checkpoint parity, compact transport after staged initialization, cancellation and oversized input, and both checkpoint overflow failures. The benchmark alternates direct/indirect order over full 16,777,216-candidate synthetic Leafy/fixed-pair batches, excludes two warmups, and reports completed host canonical, dispatch/sync, and readback times. Set `RT2_GPU_SMOKE_BATCHES` (2–512, default 32) for a longer sample.

resource.6 defaults to capacity dispatch with the prefix-hash table on the admitted single-player public Dense staged Leafy path. Only `RT2_NEOW_INDIRECT_DISPATCH=1` opts into counted dispatch, and `RT2_NEOW_DIRECT_DISPATCH=1` overrides that choice. With no indirect override, `RT2_NEOW_LEGACY_PREFIX_HASH=1` alone selects the old-hash/capacity resource.4-equivalent control in the same DLL, not an independent old binary. To test old-hash/counted dispatch, set both LEGACY_PREFIX_HASH and INDIRECT_DISPATCH to 1 and leave DIRECT unset. `RT2_NEOW_SINGLE_WINDOW=1` disables paired batching. The modes and initialization-only fallback have distinct physical revisions. Execution/validation errors never trigger a retry. The historical resource.5 actual `try` round favored the PrefixOnly path now selected by default; this is not a fresh resource.6 binary measurement.

The host also checks two-window batching against two completed single-window calls, including a one-candidate second window, a partial second window, full second-window payloads, 64-bit seed bases, and switching back to a single tail. Its `windowBenchmark` compares two single submissions/header reads against two independent workspaces with one submission/header-bank read. Production only admits batching for a single-player public Dense staged Leafy Neow path at a 100% GPU budget; the coordinator may then select a 33,554,432-candidate logical window. Each physical window retains the original 16,777,216 bound. Set `RT2_NEOW_SINGLE_WINDOW=1` before launching to retain single-window transport for production A/B.

The header checks exercise the production readback state machine with injected registration and delivery behavior, then a real local RenderingDevice. They reject missing, repeated, premature, stale, wrong-thread and wrong-length callbacks, as well as empty/truncated/oversized synchronous reads. They check independent data snapshots, registration rejection and unavailable-API fallback without replaying compute, unexpected registration failure after synchronization, cancellation during Sync followed by safe reuse, and the synchronous A/B control. Real-RD checks repeat 32/48/64-byte downloads and require completion during the existing Submit/Sync, exact data and submitted-work accounting. Executor parity covers single, paired, sequential-allocation-fallback and empty windows, cancellation before submission, and reuse afterward. Separate startup checks ensure a 1% GPU budget does not turn CPU-only compilation/setup into a GPU cooldown.

The Leafy gate probe checks 197,122 cases in each of the legacy and optimized macro modes against the canonical CPU RNG and against each other. It uses production shader helpers, exhausts pool lengths 0–65,536 in each position, adds 65,536 deterministic random cases, and covers root/salt overflow, duplicate targets/pool IDs and mapping bounds through `uint.MaxValue`. It compares raw 64-bit first/second draws, exact mantissa-to-index mapping and gate results, rather than relying only on final sparse survivors.

The prefix probe uses the production CPU table builder and GLSL helpers: 156 batches, 16,860 eight-root probes / 134,880 lanes in both macro modes, plus 135,594 CPU checks. It compares cached and legacy GPU hashes with canonical CPU xxHash64, including unaligned starts, all fourteen table rows, suffix carries, partial tails, empty ranges and final `ZZZZZZZZZZZZ`. Each 16M workspace needs at most fourteen four-word rows plus a four-word header: 240 bytes. Define `RT2_PREFIX_HASH_CPU_ONLY` when linking `PrefixHashChecks.cs` into a plain CPU host to call `PrefixHashCpuChecks.Run()` without Godot.

Production now registers eligible header downloads before submission; the owner-thread callback only saves data, and the same Sync completes the transfer before validation. `RT2_NEOW_SYNC_HEADER=1` forces the old post-Sync header read, and `RT2_NEOW_LEGACY_LEAFY_GATE=1` selects the old Leafy gate. These controls are independent and have separate physical revisions. A rejected registration or unavailable API can disable queued downloads for that executor before the explicit submission; execution or result-validation errors fail closed and never replay a submitted search window. Nonzero candidate payloads still use synchronous reads after the validated header reveals their size, in both header modes. Queued headers do not remove that payload download, sorting or validation cost.

## Four-way benchmark

Set these variables in the process that launches the isolated host:

```powershell
$env:RT2_GPU_FOUR_WAY_BENCH = '1'
$env:RT2_GPU_AB_BATCHES = '1024'
$env:RT2_GPU_AB_ROUNDS = '5'
$env:RT2_GPU_AB_ZERO_TERMINAL = '1'
```

This mode selects the synthetic benchmark instead of the standard GPU check suite; unset `RT2_GPU_FOUR_WAY_BENCH` to run those checks. Batches are clamped to 32–8,192 (default 512), and rounds to 3–9 (default 5). The benchmark explicitly sets each executor's options:

| Variant | Prefix hash | Dispatch | Leafy gate | Header | Windows |
| --- | --- | --- | --- | --- | --- |
| `Resource4PathControl` | Legacy | Capacity | Optimized | Pre-submit | Paired |
| `PrefixOnly` | Cached | Capacity | Optimized | Pre-submit | Paired |
| `CountedDispatchOnly` | Legacy | Counted | Optimized | Pre-submit | Paired |
| `Resource5` | Cached | Counted | Optimized | Pre-submit | Paired |
| `UpstreamGpuGeometryEquivalent` | Legacy | Capacity | Legacy | Post-Sync | Two sequential singles |

The fifth variant is an additional geometry control, not a full upstream-release comparison. All variants use the same synthetic Leafy/fixed-pair 64-pool fixture, not an authored or recorded game query. `RT2_GPU_AB_ZERO_TERMINAL=1` adds an artificial empty final curse projection and asserts zero output; it isolates the pre-gate and status transport from candidate payload downloads. This zero-output terminal projection is not evidence of a real query's selectivity or throughput. Unset that variable to retain the default fixture's candidate payload and its synchronous download overhead.

Each variant receives 16 warmup batches before timing. Within each measured round, every variant searches identical seed ranges; order rotates and reverses between rounds, and every batch's candidate ordinals must match. Reports include each round's completed host wall time, total wall time, median round time, throughput derived from that median, and `speedupVsResource4Control`, the ratio against `Resource4PathControl`. The measured wall includes recording, submission, waiting, header/payload retrieval, canonicalization and comparison work. It is not a GPU kernel timestamp. Moving header transfer into Sync changes the timing buckets: a lower `readbackMs` alone does not prove a faster kernel or a faster search. Keep synthetic fixture modes separate from actual queries and record backend and concurrent GPU activity.

## Actual-game query comparison

The real query runner lives in `Diagnostics/GpuProfiling/ActualSearchBenchmark.cs`, not the isolated synthetic host. Build the main project with `EnableGpuProfiling=true` as documented in `BUILDING.md`. Its DLL uses the separate `.build-evidence/gpu-profiling/bin/Release` output; retain the formal DLL for restoration, and do not ship the diagnostic DLL. The formal build excludes this runner and its hooks entirely.

Launch the actual game with the diagnostic DLL and these environment settings:

```powershell
$env:RT2_ACTUAL_SEARCH_AB = '1'
$env:RT2_ACTUAL_AB_START = '9KT7EQEZ0000'
$env:RT2_ACTUAL_AB_SECONDS = '30'
$env:RT2_ACTUAL_AB_ROUNDS = '1'
$env:RT2_ACTUAL_AB_REPORT = 'D:\records\resource5-actual-try.json'
```

It reads the saved preset named `try`, falling back to the current workbench if absent, and uses the real compiler and ProductionSearchSession. It does not save its cursor or candidate results back to the user's workbench. The root-count cap is configurable with `RT2_ACTUAL_AB_ROOTS` (2^30–2^43; default 2^43). Each run is cancelled after the requested time and drains/disposes normally; reports contain actual elapsed time, processed roots, outputs and safe cursor. Warmups are excluded. A single round follows control → prefix → counted → both; additional rounds reverse this order on odd rounds. Compare overlapping searched ranges, rather than assuming different-speed runs scan equal counts.

The four throughput runs explicitly disable sampling and set both dispatch variables for each job, saving and restoring their original values. Clearing DIRECT alone does not enable indirect dispatch in resource.6. Set `RT2_ACTUAL_AB_PROFILE=1` to append a separate five-second sampled diagnostic. To run only its warmup and five-second diagnostic, set `RT2_ACTUAL_AB_PROFILE_ONLY=1`; this skips all four 30-second measurements. The resource.6 profile-only target is the default prefix-only/capacity path, not the historical resource.5 both-enabled path. `RT2_GPU_PROFILE=1` is the general runtime sampling switch in the diagnostic build; the runner sets it for each job. Remove the actual-runner environment overrides before ordinary play. The default report is `<user-data>/RolltheSpire2/logs/actual-search-ab.json` if `RT2_ACTUAL_AB_REPORT` is unset.

The recorded `try` comparison used one fixed-order round, the same minimized game limited to 30 FPS, and the same diagnostic DLL with sampling off. All four runs found zero matches. The initial extra five-second GPU timestamp figures had a unit-conversion bug and are invalid. Corrected D3D12/Vulkan checks verify nanoseconds-to-milliseconds conversion and reject seven invalid interval cases. The separate corrected actual-game run lasted 5.0358341 seconds, retained 17 samples with zero discarded and no disabled session, and is recorded in `.build-evidence/resource5-profile-corrected-try.runtime.log`. It never enters the throughput table. Markers cover recorded windows and transfers, not individual stages, and timing categories can overlap. See `PERFORMANCE-CHANGES.txt` for the rates and their limits.
