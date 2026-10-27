R3 offline complete-output checks. Build this host only after Root builds the
candidate Mod DLL. The host never builds/deploys the Mod or opens the game.

From the repository root:
dotnet build tests/NeowOpeningReuseChecks/NeowOpeningReuseChecks.csproj -c Release -m:1 -o .build-evidence/three-rounds-20261027/round03/test-host
dotnet .build-evidence/three-rounds-20261027/round03/test-host/ProductionSearchHost.dll .build-evidence/broad-original-comparison/scenarios/random-complex-v1 .build-evidence/three-rounds-20261027/round03/neow-opening-checks.json

The old-path control uses the same DLL and the internal Evaluate overload with
forceRecomputeOpening=true. Normal production defaults to false. All normal
request, authority, full document, warnings, diagnostics, Exact evidence/witness,
reward cards/potions, RNG traces and route fingerprints are compared. Only
per-call RequestId properties and request-id diagnostic entries are removed.

The original complete RC04.witness.json query is preserved and must still Match.
Neutral reward positives/negatives use the frozen Silent character's captured
pool; they are not labeled as authored F02 Ironclad/Havoc. Root separately checks
that exact F02 runtime context in game validation/performance runs.

Actual reuse is proven by the reward document holding the exact captured choices
reference, while the freshly recomputed control holds a distinct reference.
Before/after serialization checks include choices, effect groups, continuations,
public RewardsRng state and internal Niche/Potion RNG snapshots. Cheap validation,
two reward predictions and reward validation must not alter those inputs.

Cases include diagnostics true/false, bound/RootOnly, complex-Bones true/false,
prepared reward pools true/false, strict pinned and neutral routes, optimization
off, source/delegate/owner/detection/context mismatches, same-root identity change,
compatibility/precision mismatch, trusted-input rejection, injected errors and
cancellation. Synthetic players/slot changes without an ordered-party capture
are compared as the original explicit error, not treated as valid party authority.

Harness metadata entry: ProductionExactSearchEvaluator.CanReuseNeowOpening(plan).
Revision field: ProductionExactSearchEvaluator.NeowOpeningReuseRevision, value
Beta111SameCandidateNeowOpeningV1. Reflection can record this once per scenario;
there are no per-root counters, new environment variables, or user settings.
