# RolltheSpire2 performance branch

Stable version: **1.3.5-resource.17**, based on [FalseOcean/rollthespire2](https://github.com/FalseOcean/rollthespire2), under MPL-2.0. Original attribution and license are preserved.

The runtime Mod contains only the DLL and JSON manifest and uses the game's existing .NET/Godot runtime. No installer, CUDA or Python runtime is required. Target: Windows x64 and Slay the Spire 2 0.111.0. AVX2 acceleration is optional and has a scalar fallback.

See [the full Chinese release documentation](README.zh-CN.md), [optimization scope](docs/现有优化.md), [benchmark methods and hardware](docs/测试配置与方法.md), and [unambiguous preset definitions](docs/测试预设.md). Stable game IDs are preserved in the portable JSON inputs.

The historical 34-case table compares resource.15 against upstream v1.3.5, not the current resource.17. Later resource.16/resource.17 trials have separate baselines, consumer protocols and negative results. Their percentages must not be added or compounded. The current resource.17 has not undergone a fresh 34-case upstream comparison.

Production source files are unchanged from the frozen stable17 source. Only release documentation and portable benchmark material were refreshed. Pending experimental features are excluded. See BUILDING.md and LICENSE.
