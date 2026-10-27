using System.Diagnostics;
using RolltheSpire2.Presentation.Ui1;
using RolltheSpire2.Search.Compilation;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Runtime;
using RolltheSpire2.Search.Selectivity;
using RolltheSpire2.Search.Semantics;

namespace RolltheSpire2.Ui.Shell;

// Captured/compiled authority enters here on the main thread. Everything below
// is numerical: no model capture, Godot controls, GPU dispatch or live RunState.
internal sealed record WorkbenchProbabilityPreview(
    JointSelectivityResult Joint,
    SearchProbabilityQuickView? View,
    FamilySearchEtaProjectionV1? Eta,
    IReadOnlyDictionary<string, (double? Cpu, double? Gpu)> Costs,
    JointSelectivityResult? Map,
    IReadOnlyDictionary<EventResultSearchCondition, (SearchSelectivityEstimate Estimate, double? Cpu, double? Gpu)> Events,
    IReadOnlyList<string> Issues)
{
    internal static WorkbenchProbabilityPreview Compute(CompiledSearch compiled, int target, int workers, bool cpuOnly,
        SearchResourceBudget? resources = null, SearchOptimizationSettings? optimizations = null)
    {
        optimizations ??= SearchOptimizationSettings.AllEnabled;
        using var optimizationScope = SearchOptimizationScope.Enter(optimizations);
        resources = resources?.Normalize();
        if (resources is not null) workers = resources.ResolveCpuWorkers(System.Environment.ProcessorCount);
        cpuOnly |= resources?.GpuPercent == 0;
        var input = SearchSelectivityInput.From(compiled);
        var joint = JointSelectivityEstimator.EstimateQuery(input);
        var costs = new Dictionary<string, (double? Cpu, double? Gpu)>();
        var events = new Dictionary<EventResultSearchCondition, (SearchSelectivityEstimate, double?, double?)>();
        var issues = new List<string>();
        if (compiled.Context.Party is not null) return new(joint, null, null, costs, null, events, issues);
        var view = SearchProbabilityPresentationBuilder.Build(compiled, joint);
        var q = compiled.NormalizedQuery;
        FamilySearchEtaProjectionV1? eta = null;
        try
        {
            var execution = ExactSearchExecutionRequestFactory.Compile(compiled,
                new SearchRunOptions("000000000000", long.MaxValue, target, workers)
                { Resources = resources, Optimizations = optimizations });
            if (execution.Success && execution.Plan is { } request)
            {
                request = ExactWorldProjectionPolicy.ApplyOrdinarySearchPolicy(request);
                var selected = FamilyExecutionCoordinator.Plan(request, cpuOnly ? false : null);
                eta = FamilySearchEtaProjectorV1.Project(request, selected);
                var calibration = GpuCostCalibration.Capture();
                foreach (var family in FamilyExecutionCoordinator.CreateRegisteredFamilies(request, cpuOnly ? false : null))
                {
                    var cpu = family.CpuRealizations.Select(f => f.QuotePhysicalWork(new(false, false, 1 << 20)))
                        .Where(c => c is { NanosecondsPerInput: > 0 }).OrderBy(c => c!.NanosecondsPerInput).FirstOrDefault();
                    var gpu = !cpuOnly && family.ConditionPerformance.UsesGpu ? family.QuotePhysicalWork(new(false, false, 1 << 20)) : null;
                    if (gpu is not null) gpu = calibration.Local(family, gpu, new(false, false, 1 << 20));
                    costs[family.FamilyId] = (cpu?.NanosecondsPerInput, gpu?.NanosecondsPerInput);
                }
            }
        }
        catch (Exception ex) { issues.Add("workbenchCostUnavailable=" + ex.Message); }
        JointSelectivityResult? map = q.StandardMaps.Count == 0 ? null : JointSelectivityEstimator.EstimateQuery(
            SearchSelectivityInput.From(SearchCompiler.Compile(SearchQuery.Empty with { StandardMaps = q.StandardMaps }, compiled.Context)));
        foreach (var c in q.EventResultConditions)
        {
            var estimate = EventResultProbabilityEstimator.EstimateSingle(input, c);
            double? cpuNs = null, gpuNs = null;
            try
            {
                var single = SearchCompiler.Compile(q with { EventResultConditions = [c] }, compiled.Context);
                var request = ExactSearchExecutionRequestFactory.Compile(single,
                    new SearchRunOptions("000000000000", 1 << 20, 1, workers)
                    { Resources = resources, Optimizations = optimizations });
                if (request.Plan is { } plan && EventResultFamily.TryCreate(
                        ExactWorldProjectionPolicy.ApplyOrdinarySearchPolicy(plan), out var family) && family is not null)
                {
                    var cpu = family.CpuRealizations.Select(f => f.QuotePhysicalWork(new(false, false, 1 << 20))).FirstOrDefault(v => v is not null);
                    var gpu = !cpuOnly && family.ConditionPerformance.UsesGpu ? family.QuotePhysicalWork(new(false, false, 1 << 20)) : null;
                    if (gpu is not null) gpu = GpuCostCalibration.Capture().Local(family, gpu, new(false, false, 1 << 20));
                    cpuNs = cpu?.NanosecondsPerInput; gpuNs = gpu?.NanosecondsPerInput;
                }
            }
            catch (Exception ex) { issues.Add("workbenchEventQuoteUnavailable=" + ex.Message); }
            events[c] = (estimate, cpuNs, gpuNs);
        }
        return new(joint, view, eta, costs, map, events, issues);
    }
}

// One running estimate plus the newest requested snapshot. Rapid edits replace
// the pending request; an old completion can never overwrite the current query.
internal sealed class WorkbenchProbabilityPreviewQueue
{
    internal sealed record Request(string Key, CompiledSearch Compiled, int Target, int Workers, bool CpuOnly,
        SearchResourceBudget? Resources = null, SearchOptimizationSettings? Optimizations = null);
    internal sealed record Completion(Request Request, WorkbenchProbabilityPreview? Preview, Exception? Error, double Milliseconds);
    private readonly Func<Request, WorkbenchProbabilityPreview> _compute;
    private Request? _latest;
    private Task<Completion>? _running;

    internal WorkbenchProbabilityPreviewQueue(Func<Request, WorkbenchProbabilityPreview>? compute = null) =>
        _compute = compute ?? (r => WorkbenchProbabilityPreview.Compute(r.Compiled, r.Target, r.Workers, r.CpuOnly,
            r.Resources, r.Optimizations));

    internal void Submit(Request request)
    {
        _latest = request;
        if (_running is null) Start(request);
    }

    internal void Invalidate() => _latest = null;

    internal bool TryTake(out Completion? completion)
    {
        completion = null;
        if (_running is not { IsCompleted: true }) return false;
        var finished = _running.GetAwaiter().GetResult();
        _running = null;
        if (ReferenceEquals(finished.Request, _latest)) completion = finished;
        else if (_latest is { } next) Start(next);
        return completion is not null;
    }

    private void Start(Request request)
    {
        _running = Task.Run(() =>
        {
            long start = Stopwatch.GetTimestamp();
            try { return new Completion(request, _compute(request), null, Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
            catch (Exception ex) { return new Completion(request, null, ex, Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
        });
    }
}
