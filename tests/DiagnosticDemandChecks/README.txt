Offline full-output snapshot parity for the R1 diagnostic-demand guard.
Run from the repository root. Build/run this test host only; never deploys a Mod.
AssemblyName is ProductionSearchHost to use the existing friend-test boundary.

1. Build the host against the baseline DLL, with its own output directory:
dotnet build tests/DiagnosticDemandChecks/DiagnosticDemandChecks.csproj -c Release -m:1 "-p:Rt2AssemblyPath=C:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2/mods/RolltheSpire2/RolltheSpire2.dll" -o .build-evidence/three-rounds-20261027/round01/diagnostic-host-baseline

2. Run it in a separate process:
dotnet .build-evidence/three-rounds-20261027/round01/diagnostic-host-baseline/ProductionSearchHost.dll .build-evidence/broad-original-comparison/scenarios/random-complex-v1 .build-evidence/three-rounds-20261027/round01/diagnostic-baseline.snapshot.json

3. Build the same source against a separately built candidate DLL:
dotnet build tests/DiagnosticDemandChecks/DiagnosticDemandChecks.csproj -c Release -m:1 -o .build-evidence/three-rounds-20261027/round01/diagnostic-host-candidate

4. Run that separate process:
dotnet .build-evidence/three-rounds-20261027/round01/diagnostic-host-candidate/ProductionSearchHost.dll .build-evidence/broad-original-comparison/scenarios/random-complex-v1 .build-evidence/three-rounds-20261027/round01/diagnostic-candidate.snapshot.json

5. Both processes must pass. Compare the two snapshot files byte-for-byte and bind
the actual runtime DLL SHA256 reported by each process to the expected DLL identity.
Only Context/Request RequestId and request-id diagnostic entries are removed.
Only the injected Exception diagnostic stack is normalized: its full first line,
including System.InvalidOperationException and the fixed failure message, remains.
All actual normal full-document, projected Authority, requests, Exact acceptance,
evidence and witness payloads remain in the snapshots. Each snapshot is about 124 MB.

This checks same-flag old/new parity, not false-vs-true document equality, throughput
or ordinary-search policy routing. Root separately runs ExactWorldProjectionChecks
for the ordinary Boss policy and explicit-diagnostic preservation.
