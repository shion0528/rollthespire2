# Checkpoint and resume checks

This host references an already built Mod DLL. It has no project reference to the
Mod, creates no GPU device, and never deploys files or reads the player's state.
Every persistence fixture is created in a new temporary directory and removed
afterward. Build and output files remain inside this test project.

Run after building the intended production candidate separately:

```powershell
dotnet build tests/CheckpointResumeChecks/CheckpointResumeChecks.csproj -c Release -p:Rt2AssemblyPath="<absolute path to RolltheSpire2.dll>"
dotnet tests/CheckpointResumeChecks/bin/Release/net9.0/ProductionSearchHost.dll
```

The default DLL path is the repository's existing Release output. The final JSON
line includes the SHA-256 of the DLL actually loaded.

Coverage:

- Save/reload the exact `12345678ZZFZ` checkpoint without alignment or rewriting.
- Check its 613 remaining seeds, carry through `ZZZZ` to the next prefix, and
  compare its cached root hash to the production profile hash.
- Cancel a real production session while its first Family batch is unfinished;
  verify that saving and reopening cannot advance beyond the safe cursor.
- Scan 614 roots across a prefix boundary with contiguous, non-overlapping batches.
- Commit the final seed-space sentinel only after completion; repeated polling and
  reopening must preserve one wrap and the next seed `000000000000`.
- Preserve an existing unbound cursor, then bind it to query and environment.
  Same-query reload resumes exactly, changed query/environment starts at the saved
  origin, budget changes preserve the binding, and stale session drains are ignored.

These are lifecycle and persistence checks. GPU numerical parity is covered by
the separate GPU smoke host.
