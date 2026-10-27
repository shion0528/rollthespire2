using System.Diagnostics;
using System.Text.Json;
using Environment = System.Environment;
using Godot;
using RolltheSpire2.Bootstrap;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Search.Compilation;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Runtime;
using RolltheSpire2.Search.Semantics;
using RolltheSpire2.Ui.Persistence;
using RolltheSpire2.Ui.Shell;

namespace RolltheSpire2.Bootstrap
{
    internal static partial class MainMenuPatchInstaller
    {
        private static bool _gpuProfilingActualStarted;
        static partial void ActualSearchBenchmarkRequested(Node mainMenu)
        {
            if (!_gpuProfilingActualStarted && Environment.GetEnvironmentVariable("RT2_OPTIMIZATION_UI_CHECK") == "1")
            {
                _gpuProfilingActualStarted = true;
                mainMenu.GetTree().Root.CallDeferred(Node.MethodName.AddChild,
                    new RolltheSpire2.Diagnostics.GpuProfiling.OptimizationUiProbe());
                return;
            }
            if (_gpuProfilingActualStarted || Environment.GetEnvironmentVariable("RT2_ACTUAL_SEARCH_AB") != "1") return;
            _gpuProfilingActualStarted = true;
            // Keep the cancellation owner alive if the user leaves the menu.
            // The watched menu is checked by the root-owned benchmark each frame.
            mainMenu.GetTree().Root.CallDeferred(Node.MethodName.AddChild,
                new RolltheSpire2.Diagnostics.GpuProfiling.ActualSearchBenchmark { WatchedMainMenu = mainMenu });
        }
    }
}

namespace RolltheSpire2.Diagnostics.GpuProfiling
{
    // Compiled only into the explicit diagnostic artifact. Executes the saved
    // authored query through the real compiler/session in the actual game,
    // without changing the UI draft, cursor, results, or player preferences.
    internal sealed partial class ActualSearchBenchmark : Node
    {
        internal Node? WatchedMainMenu { get; init; }
        private sealed record Variant(string Name, bool Prefix, bool Indirect, bool Profile,
            bool StaticDrawGate = false, bool InternalOrder = false, bool GroupOutput = true);
        private sealed record Job(Variant Variant, int Round, bool Warmup);
        private readonly Queue<Job> _jobs = new();
        private readonly List<object> _records = [];
        private readonly List<string> _candidates = [];
        private readonly Dictionary<int, List<(ulong End, string[] Candidates)>> _referenceCandidates = new();
        private ProductionSearchSession? _session;
        private Task? _dispose;
        private Job? _job;
        private CompiledSearch? _compiled;
        private string _start = "";
        private long _count;
        private int _targetMatchCount = int.MaxValue;
        private double _seconds;
        private string _preset = "";
        private string _presetId = "";
        private string? _requestedPreset;
        private bool _singleOptimizationMode;
        private bool _finalVerificationMode;
        private bool _internalOrderMode;
        private bool _groupOutputMode;
        private bool _cancelRequested;
        private double _delay = 2;
        private Stopwatch? _watch;
        private bool _initialized, _done;
        private Exception? _failure;
        private readonly Dictionary<string, string?> _oldEnvironment = new(StringComparer.Ordinal);

        public override void _Ready() => ProcessMode = ProcessModeEnum.Always;

        public override void _ExitTree()
        {
            if (_done) return;
            try { _session?.Cancel(); }
            catch (Exception ex) { RuntimeLog.Fault("actualSearchBenchmarkExitCancelFailed=true", ex); }
        }

        public override void _Process(double delta)
        {
            if (_done) return;
            if (_failure is not null) { FinishFailureWhenSafe(); return; }
            try
            {
                if (WatchedMainMenu is { } watchedMenu && (!GodotObject.IsInstanceValid(watchedMenu)
                    || watchedMenu.IsQueuedForDeletion() || !watchedMenu.IsInsideTree()))
                    throw new InvalidOperationException("ActualSearchBenchmark.BenchmarkMenuLeft");
                if (_session is { } session)
                {
                    Drain(session);
                    if (!_job!.Warmup && !_cancelRequested && session.GetProgress().ElapsedSeconds >= (_job.Variant.Profile ? 5 : _seconds))
                    { _cancelRequested = true; session.Cancel(); }
                    if (!session.Completion.IsCompleted) return;
                    session.Completion.GetAwaiter().GetResult();
                    Drain(session);
                    _watch!.Stop();
                    var progress = session.GetProgress();
                    long expected = _job!.Warmup ? Math.Min(_count, 1L << 35) : _count;
                    if ((_job.Warmup ? progress.State != SearchRunState.Completed || progress.ScannedCount != expected
                        : progress.State != SearchRunState.Cancelled || progress.ScannedCount <= 0) || progress.FailureCode.Length != 0)
                        throw new InvalidDataException("ActualSearchBenchmark.Incomplete:" + RuntimeLog.SafeJson(progress));
                    ulong startOrdinal = 0;
                    if (!VisibleSeedCandidateCodec.TryParseOrdinal(ModRuntime.Snapshot.Profile, _start, out startOrdinal, out _, out _)
                        || (_job.Warmup && session.GetSafeNextOrdinal() != checked(startOrdinal + (ulong)expected)))
                        throw new InvalidDataException("ActualSearchBenchmark.CursorMismatch");
                    if (!_job.Warmup)
                    {
                        string[] candidates = _candidates.Order(StringComparer.Ordinal).ToArray();
                        ulong safeEnd = session.GetSafeNextOrdinal();
                        if (!_referenceCandidates.TryGetValue(_job.Round, out var references))
                            _referenceCandidates[_job.Round] = references = [];
                        string[] InCommonRange(string[] values, ulong end) => values.Where(seed =>
                        {
                            if (!VisibleSeedCandidateCodec.TryParseOrdinal(ModRuntime.Snapshot.Profile, seed, out var ordinal, out _, out _))
                                throw new InvalidDataException("ActualSearchBenchmark.CandidateSeedInvalid");
                            return ordinal < end;
                        }).ToArray();
                        foreach (var reference in references)
                        {
                            ulong commonEnd = Math.Min(reference.End, safeEnd);
                            if (!InCommonRange(reference.Candidates, commonEnd).AsSpan().SequenceEqual(InCommonRange(candidates, commonEnd)))
                                throw new InvalidDataException("ActualSearchBenchmark.CommonRangeOutputMismatch");
                        }
                        references.Add((safeEnd, candidates));
                        var record = new { variant = _job.Variant.Name, round = _job.Round, roots = progress.ScannedCount,
                            elapsedSeconds = progress.ElapsedSeconds, wholeWallSeconds = _watch.Elapsed.TotalSeconds,
                            requestedSeconds = _job.Variant.Profile ? 5 : _seconds,
                            rootsPerSecond = progress.ScannedCount / progress.ElapsedSeconds, matches = progress.MatchCount,
                            prefixHash = _job.Variant.Prefix, countedDispatch = _job.Variant.Indirect,
                            profiling = _job.Variant.Profile, start = _start, actualGameQuery = true,
                            staticDrawGateRequested = _job.Variant.StaticDrawGate,
                            internalOrderRequested = _job.Variant.InternalOrder,
                            groupOutputRequested = _job.Variant.GroupOutput,
                            gridStrideRequested = false,
                            effectiveEnvironment = VariantEnvironment(),
                            executionPlan = session.ExecutionPlan.PlanId,
                            physicalStages = session.ExecutionPlan.Stages.Select(stage => new
                            {
                                family = stage.Family.FamilyId,
                                inputMode = stage.CompactAbi1Input ? "CompactAbi1" : "Dense",
                                physicalRevision = stage.Family.ResolveConditionPerformance(stage.CompactAbi1Input).PhysicalImplementationRevision
                            }).ToArray(),
                            queryFingerprint = _compiled!.SemanticFingerprint, cursor = session.GetSafeNextOrdinal() };
                        _records.Add(record);
                        RuntimeLog.Info("actualSearchBenchmarkRound=true;recordJson=" + RuntimeLog.SafeJson(record));
                        WriteReport("RUNNING");
                    }
                    _dispose = session.DisposeAsync().AsTask();
                    _session = null;
                    _delay = .3;
                    return;
                }
                if (_dispose is { } disposing)
                {
                    if (!disposing.IsCompleted) return;
                    disposing.GetAwaiter().GetResult();
                    _dispose = null;
                }
                if ((_delay -= delta) > 0) return;
                if (!_initialized) Initialize();
                if (_jobs.Count == 0) { Complete(); return; }
                Start(_jobs.Dequeue());
            }
            catch (Exception ex)
            {
                _failure = ex;
                try { _session?.Cancel(); }
                catch (Exception cancel) { _failure = new AggregateException(ex, cancel); }
            }
        }

        private void FinishFailureWhenSafe()
        {
            // The game must outlive an in-flight local-device Sync. A report
            // error cannot bypass normal session completion and resource release.
            if (_session is { } session)
            {
                if (!session.Completion.IsCompleted) return;
                try { session.Completion.GetAwaiter().GetResult(); }
                catch (Exception ex) { _failure = new AggregateException(_failure!, ex); }
                try { _dispose = session.DisposeAsync().AsTask(); }
                catch (Exception ex) { _failure = new AggregateException(_failure!, ex); }
                _session = null;
            }
            if (_dispose is { } disposing)
            {
                if (!disposing.IsCompleted) return;
                try { disposing.GetAwaiter().GetResult(); }
                catch (Exception ex) { _failure = new AggregateException(_failure!, ex); }
                _dispose = null;
            }
            _done = true;
            try
            {
                RuntimeLog.Fault("actualSearchBenchmark=true;result=FAIL", _failure!);
                try { WriteReport("FAIL", _failure!.ToString()); }
                catch (Exception ex) { RuntimeLog.Fault("actualSearchBenchmarkReportFailed=true", ex); }
            }
            finally
            {
                try { RestoreEnvironment(); }
                finally { OperationalFileLog.Flush(); GetTree().Quit(2); }
            }
        }

        private void Initialize()
        {
            // Match the real workbench's hardware/pricing initialization before
            // automatic allocation. Without this, reference-backend quotes may
            // be unavailable and the benchmark silently chooses another route.
            WorkspaceShell.InitializeSearchEvidenceOnMainThread();
            foreach (string name in new[] { "RT2_NEOW_LEGACY_PREFIX_HASH", "RT2_NEOW_INDIRECT_DISPATCH", "RT2_NEOW_DIRECT_DISPATCH", "RT2_GPU_PROFILE",
                "RT2_NEOW_STATIC_DRAW_GATE", "RT2_NEOW_GRID_STRIDE", "RT2_NEOW_GRID_STRIDE_STAGE1_GROUPS", "RT2_NEOW_GRID_STRIDE_STAGE2_GROUPS",
                "RT2_SHARED_PREFIX_HASH", "RT2_N_INTERNAL_ORDER", "RT2_N_GROUP_OUTPUT" })
                _oldEnvironment[name] = Environment.GetEnvironmentVariable(name);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            options.Converters.Add(new SearchWorkspacePersistence.ModelKeyJsonConverter());
            string state = Path.Combine(OS.GetUserDataDir(), "RolltheSpire2", "state");
            string? querySource = Environment.GetEnvironmentVariable("RT2_ACTUAL_AB_SOURCE");
            _requestedPreset = Environment.GetEnvironmentVariable("RT2_ACTUAL_AB_PRESET");
            bool useWorkbench = querySource is "workbench" or "last-run";
            SearchPresetDefinition? savedPreset;
            if (_requestedPreset is not null)
            {
                // An explicit preset is authoritative, including when an older
                // runner still supplies SOURCE=last-run. Never silently use a
                // different query after a missing, ambiguous, or blank name.
                var matches = new SearchPresetStore(state).GetPresets().Where(p =>
                    string.Equals(p.Title, _requestedPreset, StringComparison.Ordinal)).ToArray();
                if (string.IsNullOrWhiteSpace(_requestedPreset) || matches.Length != 1)
                    throw new InvalidDataException("ActualSearchBenchmark.PresetNameMustResolveExactlyOnce:" + _requestedPreset + ":" + matches.Length);
                savedPreset = matches[0];
            }
            else
                savedPreset = useWorkbench ? null : new SearchPresetStore(state).GetPresets().SingleOrDefault(p =>
                    string.Equals(p.Title, "try", StringComparison.OrdinalIgnoreCase));
            WorkbenchSearchDraft draft;
            if (savedPreset is not null)
            {
                draft = (savedPreset.Workbench ?? throw new InvalidDataException("ActualSearchBenchmark.PresetUnresolved"))
                    .BindPresetIntent(ModRuntime.Snapshot, _ => throw new InvalidOperationException("ActualSearchBenchmark.MultiplayerNotRequested"));
                _preset = savedPreset.Title;
                _presetId = savedPreset.Id;
            }
            else if (querySource == "last-run")
            {
                draft = (JsonSerializer.Deserialize<SearchWorkspaceDocument>(File.ReadAllText(Path.Combine(state, "search_workspace.json")), options)?.ResultDraft
                    ?? throw new InvalidDataException("ActualSearchBenchmark.MissingLastRun")).WithoutCapturedAuthority();
                _preset = "LatestSearch";
            }
            else
            {
                draft = (JsonSerializer.Deserialize<WorkbenchSearchDraft>(File.ReadAllText(Path.Combine(state, "query_workbench.json")), options)
                    ?? throw new InvalidDataException("ActualSearchBenchmark.MissingDraft")).WithoutCapturedAuthority();
                _preset = "CurrentWorkbench";
            }
            _compiled = draft.Compile(ModRuntime.Snapshot, out _);
            string? expectedFingerprint = Environment.GetEnvironmentVariable("RT2_ACTUAL_AB_EXPECT_FINGERPRINT");
            if (expectedFingerprint is not null && expectedFingerprint != _compiled.SemanticFingerprint)
                throw new InvalidDataException("ActualSearchBenchmark.LatestQueryChanged");
            _start = Environment.GetEnvironmentVariable("RT2_ACTUAL_AB_START") ?? "9KT7EQEZ0000";
            _count = long.TryParse(Environment.GetEnvironmentVariable("RT2_ACTUAL_AB_ROOTS"), out long requested)
                ? Math.Clamp(requested, 1L << 30, long.MaxValue) : 1L << 43;
            _targetMatchCount = int.TryParse(Environment.GetEnvironmentVariable("RT2_ACTUAL_AB_MATCH_TARGET"), out int target)
                ? Math.Clamp(target, 1, int.MaxValue) : int.MaxValue;
            _seconds = double.TryParse(Environment.GetEnvironmentVariable("RT2_ACTUAL_AB_SECONDS"), out var seconds)
                ? Math.Clamp(seconds, 1, 600) : 30;
            int rounds = int.TryParse(Environment.GetEnvironmentVariable("RT2_ACTUAL_AB_ROUNDS"), out int requestedRounds)
                ? Math.Clamp(requestedRounds, 1, 7) : 1;
            if (Environment.GetEnvironmentVariable("RT2_ACTUAL_AB_SINGLE_OPTIMIZATIONS") == "1")
                throw new NotSupportedException("The rejected grid-stride experiment is archived with its frozen trial DLL and source; it is absent from the release.");
            _singleOptimizationMode = false;
            _finalVerificationMode = Environment.GetEnvironmentVariable("RT2_ACTUAL_AB_FINAL_VERIFY") == "1";
            _internalOrderMode = Environment.GetEnvironmentVariable("RT2_ACTUAL_AB_INTERNAL_ORDER") == "1";
            _groupOutputMode = Environment.GetEnvironmentVariable("RT2_ACTUAL_AB_GROUP_OUTPUT") == "1";
            bool cacheScope = Environment.GetEnvironmentVariable("RT2_ACTUAL_AB_CACHE_SCOPE") == "1";
            Variant[] variants = _groupOutputMode
                ? [new("GroupOutputOff", true, false, false, true, true, false),
                    new("GroupOutputOn", true, false, false, true, true, true)]
                : _internalOrderMode
                ? [new("InternalOrderOff", true, false, false, true, false),
                    new("InternalOrderOn", true, false, false, true, true)]
                : _finalVerificationMode
                ? [new("Resource11Final", true, false, false, true, true)]
                : cacheScope
                ? [new("Resource6NrEquivalent", false, false, false), new("Resource8ExpandedPrefix", true, false, false)]
                : [new("Resource4PathControl", false, false, false), new("PrefixOnly", true, false, false),
                    new("CountedDispatchOnly", false, true, false), new("Resource5", true, true, false)];
            if (_singleOptimizationMode) { _seconds = 30; rounds = 3; }
            bool profileOnly = !_singleOptimizationMode && Environment.GetEnvironmentVariable("RT2_ACTUAL_AB_PROFILE_ONLY") == "1";
            foreach (var variant in profileOnly ? variants.Where(v => v.Name == "PrefixOnly") : variants)
                _jobs.Enqueue(new(variant, -1, true));
            for (int round = 0; !profileOnly && round < rounds; round++)
            {
                // Each single-factor variant occupies each position once. All
                // measured sessions restart from the same complete ordinal.
                IEnumerable<Variant> order = _singleOptimizationMode
                    ? variants.Skip(round % variants.Length).Concat(variants.Take(round % variants.Length))
                    : (round & 1) == 0 ? variants : variants.Reverse();
                foreach (var variant in order) _jobs.Enqueue(new(variant, round, false));
            }
            if (!_singleOptimizationMode && (profileOnly || Environment.GetEnvironmentVariable("RT2_ACTUAL_AB_PROFILE") == "1"))
                _jobs.Enqueue(new(new("PrefixOnlyProfileDiagnostic", true, false, true), rounds, false));
            _initialized = true;
            RuntimeLog.Info("actualSearchBenchmark=true;phase=Initialized;actualGameQuery=true;productionSession=true;" +
                "resource4EquivalentControl=" + (!_singleOptimizationMode && !cacheScope && !_internalOrderMode && !_finalVerificationMode && !_groupOutputMode) + ";warmupsExcluded=true;alternatingOrder=" + (!profileOnly && rounds > 1) +
                ";singleOptimizationMode=" + _singleOptimizationMode + ";profileOnly=" + profileOnly + ";userCursorUntouched=true;" +
                "internalOrderMode=" + _internalOrderMode + ";" +
                "groupOutputMode=" + _groupOutputMode + ";" +
                "preset=" + _preset + ";presetId=" + _presetId + ";secondsPerVariant=" + _seconds + ";reportPath=" + ReportPath);
        }

        private void Start(Job job)
        {
            _job = job;
            _cancelRequested = false;
            Environment.SetEnvironmentVariable("RT2_NEOW_LEGACY_PREFIX_HASH", job.Variant.Prefix ? null : "1");
            Environment.SetEnvironmentVariable("RT2_NEOW_INDIRECT_DISPATCH", job.Variant.Indirect ? "1" : null);
            Environment.SetEnvironmentVariable("RT2_NEOW_DIRECT_DISPATCH", job.Variant.Indirect ? null : "1");
            Environment.SetEnvironmentVariable("RT2_GPU_PROFILE", job.Variant.Profile ? "1" : null);
            Environment.SetEnvironmentVariable("RT2_NEOW_STATIC_DRAW_GATE", job.Variant.StaticDrawGate ? "1" : "0");
            Environment.SetEnvironmentVariable("RT2_NEOW_GRID_STRIDE", "0");
            Environment.SetEnvironmentVariable("RT2_SHARED_PREFIX_HASH", _finalVerificationMode || _internalOrderMode || _groupOutputMode ? "1" : "0");
            Environment.SetEnvironmentVariable("RT2_N_INTERNAL_ORDER", job.Variant.InternalOrder ? "1" : "0");
            Environment.SetEnvironmentVariable("RT2_N_GROUP_OUTPUT", job.Variant.GroupOutput ? "1" : "0");
            if (_singleOptimizationMode)
            {
                // Hold unrelated new features and optional grid overrides fixed
                // so each measured arm changes one requested optimization only.
                Environment.SetEnvironmentVariable("RT2_SHARED_PREFIX_HASH", "0");
                Environment.SetEnvironmentVariable("RT2_NEOW_GRID_STRIDE_STAGE1_GROUPS", "1024");
                Environment.SetEnvironmentVariable("RT2_NEOW_GRID_STRIDE_STAGE2_GROUPS", "32");
            }
            _candidates.Clear();
            long count = job.Warmup ? Math.Min(_count, 1L << 35) : _count;
            var options = new SearchRunOptions(_start, count, _targetMatchCount, Environment.ProcessorCount)
                { Resources = new SearchResourceBudget(100, 100) };
            var result = ExactSearchExecutionRequestFactory.Compile(_compiled!, options);
            if (!result.Success || result.Plan is null) throw new InvalidDataException("ActualSearchBenchmark.Compile:" + result.Issue);
            _watch = Stopwatch.StartNew();
            _session = FamilyExecutionCoordinator.Start(result.Plan, failOnPhysicalRecovery: true);
            if (!_singleOptimizationMode && !job.Warmup && Environment.GetEnvironmentVariable("RT2_ACTUAL_AB_REQUIRE_PRIVATE_NR") == "1" &&
                !_session.ExecutionPlan.PlanId.Contains("AutomaticPrivateSerial.NR", StringComparison.Ordinal))
                throw new InvalidDataException("ActualSearchBenchmark.ExpectedPrivateNr:" + _session.ExecutionPlan.PlanId);
            RuntimeLog.Info("actualSearchBenchmarkStarted=true;variant=" + job.Variant.Name + ";round=" + job.Round + ";warmup=" + job.Warmup +
                ";environmentJson=" + RuntimeLog.SafeJson(VariantEnvironment()) + ";executionPlan=" + _session.ExecutionPlan.PlanId);
        }

        private static object VariantEnvironment() => new
        {
            legacyPrefixHash = Environment.GetEnvironmentVariable("RT2_NEOW_LEGACY_PREFIX_HASH"),
            countedDispatch = Environment.GetEnvironmentVariable("RT2_NEOW_INDIRECT_DISPATCH"),
            directDispatch = Environment.GetEnvironmentVariable("RT2_NEOW_DIRECT_DISPATCH"),
            staticDrawGate = Environment.GetEnvironmentVariable("RT2_NEOW_STATIC_DRAW_GATE"),
            internalOrder = Environment.GetEnvironmentVariable("RT2_N_INTERNAL_ORDER"),
            groupOutput = Environment.GetEnvironmentVariable("RT2_N_GROUP_OUTPUT"),
            gridStride = Environment.GetEnvironmentVariable("RT2_NEOW_GRID_STRIDE"),
            gridStrideStage1Groups = Environment.GetEnvironmentVariable("RT2_NEOW_GRID_STRIDE_STAGE1_GROUPS"),
            gridStrideStage2Groups = Environment.GetEnvironmentVariable("RT2_NEOW_GRID_STRIDE_STAGE2_GROUPS"),
            sharedPrefixHash = Environment.GetEnvironmentVariable("RT2_SHARED_PREFIX_HASH"),
            gpuProfiling = Environment.GetEnvironmentVariable("RT2_GPU_PROFILE")
        };

        private void Drain(ProductionSearchSession session)
        {
            while (session.TryReadCandidate(out var candidate))
                if (candidate is not null) _candidates.Add(candidate.Seed);
        }

        private string ReportPath => Environment.GetEnvironmentVariable("RT2_ACTUAL_AB_REPORT") ??
            Path.Combine(OS.GetUserDataDir(), "RolltheSpire2", "logs", "actual-search-ab.json");

        private void WriteReport(string result, string error = "")
        {
            string path = ReportPath;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(new { result, actualGameQuery = true, start = _start,
                preset = _preset, presetId = _presetId, requestedPreset = _requestedPreset,
                singleOptimizationMode = _singleOptimizationMode, warmupsExcluded = true,
                internalOrderMode = _internalOrderMode,
                groupOutputMode = _groupOutputMode,
                maximumRootsPerRun = _count, targetMatchCount = _targetMatchCount, secondsPerRun = _seconds,
                queryFingerprint = _compiled?.SemanticFingerprint,
                control = _groupOutputMode
                    ? "Resource10SameBinaryGroupOutputOff;ExistingOptimizationsOn;NotSeparateUpstreamBinary"
                    : _internalOrderMode
                    ? "Resource9SameBinaryInternalOrderOff;AllExistingOptimizationsOn;NotSeparateUpstreamBinary"
                    : _finalVerificationMode
                    ? "FinalReleasePath;StaticDrawGateOn;SharedPrefixOn;NoPerformanceControl"
                    : Environment.GetEnvironmentVariable("RT2_ACTUAL_AB_CACHE_SCOPE") == "1"
                    ? "Resource6NrEquivalentSameBinaryPrefixDisabled;NotSeparateOriginalBinary"
                    : "Resource4EquivalentCodePath;NotSeparateOriginalBinary", records = _records, error },
                new JsonSerializerOptions { WriteIndented = true }));
        }

        private void Complete()
        {
            RestoreEnvironment();
            WriteReport("PASS");
            RuntimeLog.Info("actualSearchBenchmark=true;result=PASS;actualGameQuery=true;reportPath=" + ReportPath);
            OperationalFileLog.Flush();
            _done = true;
            GetTree().Quit();
        }

        private void RestoreEnvironment()
        {
            foreach (var pair in _oldEnvironment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }
}
