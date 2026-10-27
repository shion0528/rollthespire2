using System.Diagnostics;
using RolltheSpire2.Bootstrap;
using RolltheSpire2.Search.Compilation;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Neow;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Effects.Snapshots;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.Semantics;

namespace RolltheSpire2.Search.FamilyExecution;

internal sealed partial class NeowFamily
{
    // Physical specialization of the existing Family; ABI1 remains ordinals only.
    internal sealed partial class PartyInitial : IFamilyInvocation
    {
        private readonly ExactSearchExecutionRequest _request;
        private readonly FamilyCpuExecution _cpu;
        private readonly List<NeowReplayPlan> _cpuPlans = [];
        private readonly List<(ExactSearchExecutionRequest Request, IFamilyInvocation Family)> _gpuStages = [];
        private long _inputs, _outputs, _liveInputs;
        private double _ms, _liveMs;
        private int _batches;
        private bool _compact, _privateParity;
        private readonly bool _optimize;
        private FamilyPrivateGpuChain? _chain;
        private NeowFamilyGpuExecutor? _fused;
        private readonly bool? _fuse;
        private long _fusedReadback;
        private double _fusedDispatch;
        private bool UsesFused => _optimize && _fuse != false && _gpuStages.Count > 1 &&
            _gpuStages.All(s => s.Family is NeowFamily { GpuPlan: not null }) &&
            (_fuse == true || PreferFused());
        private bool PreferFused()
        {
            // The bounded workload comparison regressed on mixed heavy effects.
            // Fuse homogeneous identity/Leafy kernels; retain private compaction for others.
            var first = ((NeowFamily)_gpuStages[0].Family).GpuPlan!;
            return first.Meta[82] is 0 or 4 && (first.Meta[56] & 2) == 0 &&
                _gpuStages.All(s => ((NeowFamily)s.Family).GpuPlan is { } p &&
                    p.Meta[82] == first.Meta[82] && p.Meta[81] == first.Meta[81] && (p.Meta[56] & 2) == 0);
        }
        private bool UsesPrivate => _optimize && _gpuStages.Count > 1 && _gpuStages.All(s => s.Family.CanBindPrivateSerial);
        internal PartyInitial(ExactSearchExecutionRequest request, bool preferGpu = false, bool optimize = true, bool? fuse = null)
        {
            using var optimizationScope = SearchOptimizationScope.Enter(request.RunOptions.Optimizations);
            _optimize = optimize; _fuse = fuse;
            _request = request;
            _cpu = new(request, FamilyId, "N.Neow.PartyEffects.Cpu.v2", workers: Math.Clamp(request.WorkerCount, 1, 32), numericalRoots: true);
            try
            {
                (int Niche, int Potions)[]? arrivals = [(0, 0)];
                foreach (var player in request.CompiledSearch.NormalizedQuery.Players)
                {
                    if (!HasConditions(player)) continue; // Unselected N is a neutral shared prefix.
                    // Initial offers are independent. Result sieves below union possible
                    // arrivals; only whole-table Exact selects a joint witness.
                    int gateStart = _gpuStages.Count;
                    var route = player.Conditions.OpeningRoute ?? (player.SelectedOption is { } selected ? new(selected.Option) : null);
                    var gates = new[] { player.Offers, player.Conditions.LegacyNeow.NeowRelics }
                        .Concat(player.Conditions.LegacyWorld.AncientOptionFilters.Where(f => f.Act == 1).Select(f => f.Keys))
                        .Where(f => !f.IsEmpty).ToArray();
                    if (gates.Length == 0 && route is not null) gates = [ModelKeySetFilter.Empty];
                    foreach (var offers in preferGpu ? gates : [])
                    {
                        // A selected route contributes only its presence in the initial offers here.
                        // In particular, selecting Bones must not invoke a player-local Bones replay.
                        var required = route is null ? offers.All : offers.All.Append(route.RouteRelicKey).Distinct().ToArray();
                        var query = SearchQuery.Empty with { LegacyNeow = SearchQuery.Empty.LegacyNeow with
                            { NeowRelics = new(offers.Any, required, offers.Ban) } };
                        var compiled = SearchCompiler.CompilePlayer(query, request.CompiledSearch.PlayerSearches[player.Slot].Context);
                        var child = ExactSearchExecutionRequestFactory.ForPlayer(request, compiled);
                        var replay = NeowReplayPlan.Compile(child, false);
                        var family = new NeowFamily(child, replay);
                        _gpuStages.Add((child, family));
                    }
                    int gateCount = _gpuStages.Count - gateStart;
                    var compiledPersonal = request.CompiledSearch.PlayerSearches[player.Slot];
                    if (player.SelectedOption is { } selectedOption)
                        compiledPersonal = SearchCompiler.CompilePlayer(player.Conditions with
                        { OpeningRoute = new(selectedOption.Option), StructuredOpeningEffects = player.Results }, compiledPersonal.Context);
                    var personal = ExactSearchExecutionRequestFactory.ForPlayer(request, compiledPersonal);
                    var effectPlan = NeowReplayPlan.Compile(personal, false);
                    var predicateArrivals = route is null && !compiledPersonal.NormalizedQuery.HasCombatRewardConstraints
                        ? new[] { (Niche: 0, Potions: 0) } : arrivals;
                    bool hasResults = effectPlan.StructuredConditions.Length > 0 || effectPlan.HasFinalCurseFastProjection;
                    bool hasPairPredicate = (effectPlan.BonesAny | effectPlan.BonesAll | effectPlan.BonesBan) != 0 ||
                        effectPlan.First != 255 || effectPlan.Second != 255;
                    // Route presence is already part of the initial-offer gates.
                    // A plain Bones selection needs no child-pair replay here.
                    bool hasPredicate = hasResults || hasPairPredicate || effectPlan.RequireBones ||
                        effectPlan.Filter.RequireSmallCapsule || effectPlan.Filter.RequireLargeCapsule;
                    bool hooksKnown = !hasResults || AuthoredOpeningHooksSupported(personal);
                    if (hasPredicate)
                    {
                        var predicate = effectPlan;
                        if (!hasResults && !hasPairPredicate)
                            predicate = predicate with { Bones = false, RequireBones = effectPlan.RequireBones || effectPlan.Bones };
                        if (!hooksKnown)
                            predicate = IdentityOnly(predicate);
                        else if (predicateArrivals is null)
                        {
                            // Private Rewards/Transformations predicates remain usable
                            // when a preceding player's shared arrival is unresolved.
                            predicate = predicate with
                            {
                                StructuredConditions = predicate.StructuredConditions.Where(c => !ReadsShared(c)).ToArray(),
                                EnabledDomains = predicate.EnabledDomains & ~Beta110FastDomain.FinalCurse,
                                RequiredFinalCurseIds = [], BannedFinalCurseIds = []
                            };
                        }
                        bool sharedDependent = predicate.HasFinalCurseFastProjection || predicate.StructuredConditions.Any(ReadsShared);
                        predicate = predicate with { SharedArrivals = sharedDependent ? predicateArrivals! : [] };
                        int gpuCount = _gpuStages.Count;
                        foreach (var part in PredicateParts(predicate))
                        {
                            _cpuPlans.Add(part);
                            if (!preferGpu) continue;
                            var effects = new NeowFamily(personal, part);
                            _gpuStages.Add((personal, effects));
                            if (effects.GpuPlan is null)
                                RuntimeLog.TryBackgroundInfo($"partyNeowCpuStage=true;slot={player.Slot};reason=GpuPlanUnavailable;predicate=NumericalReplay;owner=WholeTableExact");
                        }
                        if (optimize && player.Offers.IsEmpty && !player.Conditions.LegacyWorld.AncientOptionFilters.Any(f => f.Act == 1 && !f.IsEmpty) && _gpuStages.Count > gpuCount)
                            _gpuStages.RemoveRange(gateStart, gateCount);
                        if (!hooksKnown || predicateArrivals is null && (effectPlan.HasFinalCurseFastProjection || effectPlan.StructuredConditions.Any(ReadsShared)))
                            RuntimeLog.TryBackgroundInfo($"partyNeowEffectRemainder=true;slot={player.Slot};reason=UnmodeledAuthoredHookOrPrefixAuthority;owner=WholeTableExact");
                    }
                    if (effectPlan.ExactOnly.Length > 0)
                        RuntimeLog.TryBackgroundInfo($"partyNeowEffectRemainder=true;slot={player.Slot};reason={string.Join(',', effectPlan.ExactOnly)};owner=WholeTableExact");
                    var advances = SharedAdvanceOptions(personal, effectPlan);
                    arrivals = arrivals is null || advances is null ? null : arrivals.SelectMany(a => advances.Select(b =>
                        (Niche: a.Niche + b.Niche, Potions: a.Potions + b.Potions))).Distinct().OrderBy(x => x.Niche).ThenBy(x => x.Potions).ToArray();
                    if (arrivals?.Length > 4096) arrivals = null;

                }
            }
            catch (Exception ex)
            {
                _gpuStages.Clear(); // No device resources are allocated during planning.
                RuntimeLog.TryBackgroundInfo("partyNeowGpuUnavailable=true;selected=CPU;reason=" + ex.Message);
            }
        }
        private static bool ReadsShared(Beta110FastStructuredCondition condition) => ReadsSharedSource(condition.SourceRelicId);
        private static bool ReadsSharedSource(byte source) => source is
            Beta110FastRelicCatalog.NewLeaf or Beta110FastRelicCatalog.Kaleidoscope or Beta110FastRelicCatalog.PhialHolster;

        private static NeowReplayPlan IdentityOnly(NeowReplayPlan plan) => plan with
        { StructuredConditions = [], EnabledDomains = Beta110FastDomain.None,
            RequiredFinalCurseIds = [], BannedFinalCurseIds = [], AuthoredUpgrades = null };

        // The GPU body stores one predicate per source. Repeated authored rows
        // form a conjunction of necessary route unions; Exact binds one witness.
        private static IEnumerable<NeowReplayPlan> PredicateParts(NeowReplayPlan plan)
        {
            var groups = plan.StructuredConditions.GroupBy(c => c.SourceRelicId).Select(g => g.ToArray()).ToArray();
            int count = groups.Select(g => g.Length).DefaultIfEmpty(1).Max();
            bool packed = groups.All(g => g.Length == 1 ||
                g[0].SourceRelicId is Beta110FastRelicCatalog.LostCoffer or Beta110FastRelicCatalog.ScrollBoxes &&
                g.Select(c => c.Kind).Distinct().Count() == g.Length);
            if (packed) { yield return plan; yield break; }
            for (int i = 0; i < count; i++)
                yield return plan with { StructuredConditions = groups.Where(g => i < g.Length).Select(g => g[i]).ToArray() };
        }
        private static byte[] KnownBonesPair(NeowReplayPlan plan) => plan.First != 255 && plan.Second != 255
            ? [plan.First, plan.Second]
            : plan.Authority.BonesEligibleRelicIds.Where(id => (plan.BonesAll & Beta110FastRelicCatalog.Bit(id)) != 0).ToArray();

        private static bool AuthoredOpeningHooksSupported(ExactSearchExecutionRequest request)
        {
            var a = request.Authority.EffectAuthority;
            var relics = (a?.OrderedRelicBag ?? []).Concat(a?.SharedRelicPoolSource ?? []).Concat(a?.CharacterRelicPoolSource ?? []);
            return PartyInitialQuery.CapsuleEffectPremise(request.CompiledSearch.NormalizedQuery).Values
                .SelectMany(keys => keys).All(k => k == BaseGameModelKeys.OrdinaryRelics.Whetstone ||
                    k == BaseGameModelKeys.OrdinaryRelics.WarPaint || relics.Any(r => r.RelicKey == k &&
                        r.NestedClassificationExact && r.NestedEffectKind == NestedRelicEffectKind.NoTrackedImmediateEffect));
        }

        // A necessary-condition union of possible shared arrivals. It does not
        // select a player-local witness; whole-table Exact binds the real route.
        internal static (int Niche, int Potions)[]? SharedAdvanceOptions(ExactSearchExecutionRequest request, NeowReplayPlan plan)
        {
            var values = new HashSet<(int Niche, int Potions)>();
            bool Add(NeowReplayPlan order)
            {
                var d = SharedAdvance(request, order);
                if (d.Potions is not { } p) return false;
                if (d.Niche is { } n) values.Add((n, p));
                else if (plan.CapsuleUpgradeUpperBound >= 0)
                    for (int i = 0; i <= 2 * plan.CapsuleUpgradeUpperBound + 2 * plan.Authority.EffectCatalog.OtherCharacterPools.Length + 2; i++) values.Add((i, p));
                else return false;
                return true;
            }
            if (!plan.Bones || plan.Selected == 255) return Add(plan) ? values.ToArray() : null;
            foreach (byte a in plan.Authority.BonesEligibleRelicIds)
            foreach (byte b in plan.Authority.BonesEligibleRelicIds)
            {
                ulong mask = Beta110FastRelicCatalog.Bit(a) | Beta110FastRelicCatalog.Bit(b);
                if (a == b || (mask & plan.BonesAll) != plan.BonesAll || (mask & plan.BonesBan) != 0 ||
                    plan.BonesAny != 0 && (mask & plan.BonesAny) == 0 ||
                    plan.First != 255 && (a != plan.First || b != plan.Second)) continue;
                if (!Add(plan with { First = a, Second = b })) return null;
            }
            return values.ToArray();
        }

        internal static (int? Niche, int? Potions) SharedAdvance(ExactSearchExecutionRequest request, NeowReplayPlan plan)
        {
            if (plan.Selected == 255) return (0, 0);
            var pair = plan.Bones ? KnownBonesPair(plan) : [];
            if (plan.Bones && pair.Length != 2) return (null, null);
            var capsules = (plan.Bones ? pair : new[] { plan.Selected })
                .Where(id => id is Beta110FastRelicCatalog.SmallCapsule or Beta110FastRelicCatalog.LargeCapsule).ToArray();
            var effects = request.Evaluation.StructuredNeowEffects;
            // A Bones-parented nested target binds unambiguously only when the
            // fixed pair contains one Capsule. Two Capsule allocations stay unknown.
            if (capsules.Length == 1)
            {
                var source = capsules[0] == Beta110FastRelicCatalog.SmallCapsule
                    ? BaseGameModelKeys.Relics.SmallCapsule : BaseGameModelKeys.Relics.LargeCapsule;
                effects = effects.Select(c => c.SourceRelicKey == BaseGameModelKeys.Relics.NeowsBones &&
                    c.Scope == NeowStructuredEffectScope.NestedRelics ? c with { SourceRelicKey = source } : c).ToArray();
            }
            var upgrades = NeowAuthoredUpgradeContinuation.Compile(request with { Evaluation = request.Evaluation with
            { StructuredNeowEffects = effects } });
            var authoredCapsules = PartyInitialQuery.CapsuleEffectPremise(request.CompiledSearch.NormalizedQuery);
            if (!plan.Bones) return Direct(plan.Selected, 255);
            var forward = Order(pair[0], pair[1]);
            if (plan.First != 255 && plan.Second != 255) return forward;
            var reverse = Order(pair[1], pair[0]);
            return (forward.Niche == reverse.Niche ? forward.Niche : null,
                forward.Potions == reverse.Potions ? forward.Potions : null);

            (int? Niche, int? Potions) Order(byte first, byte second)
            {
                var x = Direct(first, 255); var y = Direct(second, first);
                return (x.Niche + y.Niche + 1, x.Potions + y.Potions);
            }
            (int? Niche, int? Potions) Direct(byte id, byte prior)
            {
                if (id == Beta110FastRelicCatalog.NewLeaf) return (1, 0);
                if (id == Beta110FastRelicCatalog.Kaleidoscope) return (2 * Math.Max(0, plan.Authority.EffectCatalog.OtherCharacterPools.Length - 1), 0);
                if (id == Beta110FastRelicCatalog.PhialHolster) return (0, 4);
                if (id is Beta110FastRelicCatalog.SmallCapsule or Beta110FastRelicCatalog.LargeCapsule)
                {
                    var source = id == Beta110FastRelicCatalog.SmallCapsule ? BaseGameModelKeys.Relics.SmallCapsule : BaseGameModelKeys.Relics.LargeCapsule;
                    var keys = authoredCapsules[source].ToArray();
                    if (keys.Length == 0) return (0, 0);
                    var authority = request.Authority.EffectAuthority;
                    foreach (var key in keys)
                        if (key != BaseGameModelKeys.OrdinaryRelics.Whetstone && key != BaseGameModelKeys.OrdinaryRelics.WarPaint &&
                            !(authority?.OrderedRelicBag ?? []).Concat(authority?.SharedRelicPoolSource ?? [])
                                .Concat(authority?.CharacterRelicPoolSource ?? []).Any(r => r.RelicKey == key && r.NestedClassificationExact &&
                                    r.NestedEffectKind == NestedRelicEffectKind.NoTrackedImmediateEffect)) return (null, null);
                    int advance = upgrades?.Advance(id, prior) ?? 0;
                    return (advance < 0 ? null : advance, 0);
                }
                return id <= Beta110FastRelicCatalog.StoneHumidifier ? (0, 0) : (null, null);
            }
        }

        internal static bool HasConditions(PlayerOfferQuery p)
        {
            var q = p.Conditions; var n = q.LegacyNeow;
            return !p.Offers.IsEmpty || p.SelectedOption is not null || p.Results.Count > 0 ||
                q.OpeningRoute is not null || q.OpeningRouteRelicRequirement is not null ||
                q.StructuredOpeningEffects.Any(c => !c.IsEmpty) || !n.NeowRelics.IsEmpty || n.RequireNeowsBones ||
                !n.BonesRelics.IsEmpty || n.RequiredBonesCombination.Count > 0 || n.RequiredBonesAcquisitionOrder.Count > 0 ||
                n.RequireSmallCapsule || n.RequireLargeCapsule || !n.CapsuleContainedRelics.IsEmpty ||
                n.RequireWhetstone || n.RequireWarPaint || n.RequiredFinalCurse.HasValue || n.BannedFinalCurses.Count > 0 ||
                n.Preset != NeowSearchPreset.None || n.EffectOutputConditions.Any(c => !c.IsEmpty) ||
                q.LegacyWorld.AncientOptionFilters.Any(f => f.Act == 1 && !f.IsEmpty);
        }
        internal bool MatchesInitialOffers(ulong root)
        {
            var party = _request.CompiledSearch.Context.Party!;
            return _request.CompiledSearch.NormalizedQuery.Players.All(p =>
            {
                if (!HasConditions(p)) return true;
                var offers = ModernNeowIdentityPredictor.PredictModernCore(root, party.Players[p.Slot], Beta111Profile.Instance, true).RelicKeys;
                return PartyInitialQuery.Matches(p.Offers, offers) && PartyInitialQuery.Matches(p.Conditions.LegacyNeow.NeowRelics, offers) &&
                    p.Conditions.LegacyWorld.AncientOptionFilters.Where(f => f.Act == 1).All(f => PartyInitialQuery.Matches(f.Keys, offers)) &&
                    (p.SelectedOption is null || offers.Contains(p.SelectedOption.Option)) &&
                    (p.Conditions.OpeningRoute is null || offers.Contains(p.Conditions.OpeningRoute.RouteRelicKey));
            });
        }
        private bool Matches(ulong root) => MatchesInitialOffers(root) && _cpuPlans.All(plan => NeowPartyReplay.Matches(root, plan));
        public string FamilyId => "N.Neow";
        public FamilyAnalyticalCostProjection AnalyticalCost => new(FamilyId, FamilyCpuExecution.Capacity, [], "PartyInitialAnalyticalWorkUnavailable;PhysicalQuoteBounded");
        public FamilyPhysicalQuote? QuotePhysicalWork(FamilyPhysicalQuoteRequest geometry)
        {
            using var optimizationScope = SearchOptimizationScope.Enter(_request.RunOptions.Optimizations);
            if (!FamilyPhysicalQuote.AdmittedRequest(geometry) || geometry.PrivateInput || geometry.PrivateOutput ||
                geometry.MeanInputPopulation <= 0) return null;
            int window = ConditionPerformance.UsesGpu ? FamilyPhysicalExecutionDefaults.DefaultCandidateBatchSize : FamilyCpuExecution.Capacity;
            var price = Price(geometry.MeanInputPopulation, geometry.CompactInput,
                GpuCostCalibration.Capture(), window);
            return price is null ? null : new(FamilyId, "PartyNMeasuredEnvelope",
                price.CanonicalMs * 1e6 / geometry.MeanInputPopulation, window, price.SetupMs,
                string.Join(';', price.Evidence) + ";CompleteCanonicalAbi1;PlannerRankingReference")
            { LocalCostSource = "Condition", OutputAlreadyOrdered = true, PublicTransportClass = "CompleteCanonicalAbi1" };
        }
        // Result stages may union several shared arrivals and retain Exact-only
        // remainders. One is a safe Fast-pass envelope until those projected
        // necessary predicates have a separate joint probability model.
        public FamilySurvivalProjection Survival
        {
            get
            {
                FamilySurvivalProjection offers = PartyStageSurvival.Project(_request, FamilyId);
                return offers.SurvivalProbability is double p
                    ? FamilySurvivalProjection.Resolved(FamilyId, p,
                        "PartyN.OfferGateUpperEnvelope;ResultArrivalUnionsAndExactRemaindersNotPaid;" + offers.Evidence)
                    : FamilySurvivalProjection.Resolved(FamilyId, 1,
                        "PartyN.ConservativeFastPassUpperEnvelope;OfferProbabilityUnavailable;" + offers.Evidence);
            }
        }
        public FamilyConditionPerformanceProjection ConditionPerformance => ResolveConditionPerformance(false);
        internal AutomaticPrivateSerialPricing.PricedOrder? Price(double roots, bool compact,
            GpuCostSnapshot gpuCosts, int rootWindow)
        {
            using var optimizationScope = SearchOptimizationScope.Enter(_request.RunOptions.Optimizations);
            double outputRate = Survival.SurvivalProbability ?? 1;
            if (_gpuStages.Count == 0)
            {
                double work = _request.CompiledSearch.NormalizedQuery.Players.Count +
                    _cpuPlans.Sum(plan => 1 + plan.StructuredConditions.Length * 3 +
                        (plan.HasFinalCurseFastProjection ? 1 : 0));
                var cpuMeasured = PartyMeasuredCost.Price(FamilyId, roots,
                    _request.CompiledSearch.NormalizedQuery.Players.Count, false, false,
                    FamilyCpuExecution.Capacity, work, outputRate, _request.WorkerCount);
                return cpuMeasured is { } cpuPrice
                    ? new([this], false, cpuPrice.Ms, outputRate, cpuPrice.SetupMs,
                        [cpuPrice.Evidence, $"CpuNPlans={_cpuPlans.Count};Work={work:G9}"])
                    : null;
            }
            IFamilyInvocation[] children = _gpuStages.Select(s => s.Family).ToArray();
            bool privateEdges = UsesPrivate || UsesFused;
            if (privateEdges)
            {
                double work = children.Sum(child => child is NeowFamily n
                    ? 1 + n.PricingReplayPlan.StructuredConditions.Length * 3 +
                        (n.PricingReplayPlan.HasFinalCurseFastProjection ? 1 : 0) : 1);
                var gpuMeasured = PartyMeasuredCost.Price(FamilyId, roots, children.Length,
                    true, true, NeowFamilyGpuExecutor.Capacity, work, outputRate);
                if (gpuMeasured is { } gpuPrice)
                    return new(children, true, gpuPrice.Ms, outputRate, gpuPrice.SetupMs,
                        [gpuPrice.Evidence, $"GpuNStages={children.Length};Work={work:G9};" +
                            (UsesFused ? "ExistingFusedHomogeneous" : "PrivateOrdinalChain")]);
            }
            FamilyPhysicalQuote? Quote(IFamilyInvocation child, FamilyPhysicalQuoteRequest geometry) =>
                child.QuotePhysicalWork(geometry) is { } raw ? gpuCosts.Local(child, raw, geometry) : null;
            // Several arrivals are intentionally unioned in this sieve. Charging
            // later children their full input is a work envelope, independent of
            // the whole-query probability and of same-run survivor observations.
            return AutomaticPrivateSerialPricing.Price(roots, children, privateEdges,
                Quote, (_, _) => 1, firstCompactInput: compact,
                rootWindowOverride: rootWindow, initialSetupMs: 0);
        }
        public FamilyConditionPerformanceProjection ResolveConditionPerformance(bool compact)
        {
            using var optimizationScope = SearchOptimizationScope.Enter(_request.RunOptions.Optimizations);
            return _gpuStages.Count == 0 ? _cpu.Condition(compact) :
                new(FamilyId, "N.Neow.PartyEffects.Gpu.v6." + (UsesFused ? "Fused." : UsesPrivate ? "PrivateOrdinal." : "Public.") + (compact ? "CompactAbi1" : "Dense") +
                    (UsesFused && _fused?.SharedPrefixHashRevisionFor(compact) is { } sharedPrefix ? "." + sharedPrefix : ""),
                    "OrderedSlotInitialOffers", 1, "InitialOffersAndResolvedEffects;SharedArrival=AuthoredDrawUnion;Remainder=WholeTableExact;BoundedLocalPhysicalQuote",
                    usesGpu: _gpuStages.Any(s => s.Family.ConditionPerformance.UsesGpu));
        }
        IEnumerable<IFamilyInvocation> IFamilyInvocation.CpuRealizations => [new PartyInitial(_request)];
        public async ValueTask<FamilyCandidateSet> InvokeAsync(FamilyExecutionContext context, FamilyObservationWindow window,
            FamilyCandidateSet input, CancellationToken token)
        {
            if (window.ExactRequest.SnapshotFingerprint != _request.SnapshotFingerprint)
                throw new InvalidOperationException("PartyNeowObservationContextMismatch");
            if (_gpuStages.Count == 0) return _cpu.Execute(window, input, token, Matches);
            token.ThrowIfCancellationRequested();
            _compact = !input.IsDense;
            var watch = Stopwatch.StartNew();
            var result = input;
            if ((UsesFused || UsesPrivate) && input.Count > 0)
            {
                if (UsesFused)
                {
                    _fused ??= await context.ExecuteGpuAsync(rd => new NeowFamilyGpuExecutor(rd,
                        ((NeowFamily)_gpuStages[0].Family).GpuPlan!, 43,
                        partyPlans: _gpuStages.Select(s => ((NeowFamily)s.Family).GpuPlan!).ToArray()), token).ConfigureAwait(false);
                    result = await context.ExecuteGpuAsync(_ =>
                    {
                        var output = _fused.Execute(input, token, out var metrics);
                        _fusedReadback += metrics.ReadbackBytes; _fusedDispatch += metrics.DispatchSyncMs;
                        return output;
                    }, token).ConfigureAwait(false);
                }
                else
                {
                    _chain ??= await context.ExecuteGpuAsync(rd => new FamilyPrivateGpuChain(rd, _gpuStages.Select(s => s.Family).ToArray()), token).ConfigureAwait(false);
                    result = await context.ExecuteGpuAsync(_ => _chain.Execute(input, token), token).ConfigureAwait(false);
                }
                if (!_privateParity)
                {
                    var accepted = result.ExportAbi1().ToArray();
                    foreach (ulong ordinal in input.EnumerateLogicalOrdinals().Take(256))
                    {
                        ulong root = Beta111Profile.Instance.ComputeRootSeed(VisibleSeedCandidateCodec.FormatOrdinal(Beta111Profile.Instance, input.Batch.GlobalCandidate(ordinal)));
                        bool expected = _gpuStages.All(s => s.Family switch
                        {
                            NeowFamily n => NeowPartyReplay.Matches(root, n.PricingReplayPlan),
                            CapsuleRelicFamily r => r.MatchesReference(root),
                            _ => throw new InvalidOperationException("PartyN.PrivateReferenceMissing")
                        });
                        if (expected != (Array.BinarySearch(accepted, ordinal) >= 0)) throw new InvalidDataException("PartyN.PrivateReferenceParity");
                    }
                    _privateParity = true;
                }
            }
            else foreach (var stage in _gpuStages)
            {
                if (result.Count == 0) break;
                result = await stage.Family.InvokeAsync(context, new(window.Batch, stage.Request), result, token).ConfigureAwait(false);
            }
            double elapsed = watch.Elapsed.TotalMilliseconds;
            _inputs += input.Count; _outputs += result.Count; _ms += elapsed;
            if (_batches++ > 0) { Interlocked.Add(ref _liveInputs, input.Count); _liveMs += elapsed; }
            return result;
        }
        public FamilyPerformanceObservation CapturePerformanceObservation() => _gpuStages.Count == 0 ? _cpu.Observation() :
            new(ResolveConditionPerformance(_compact), _fused?.Device ?? _chain?.Device ?? _gpuStages[0].Family.CapturePerformanceObservation().DeviceName,
                _fused?.SetupMs ?? _chain?.SetupMs ?? _gpuStages.Sum(s => s.Family.CapturePerformanceObservation().SetupMs), 0, 0, _batches, Math.Max(0, _batches - 1),
                _inputs, _outputs, _ms, false, "PartyInitialGpu;CanonicalAbi1Observation;BoundedLocalQuote;PostPickup=WholeTableExact");
        public FamilyLivePerformanceSnapshot? CaptureLivePerformanceSnapshot() => _gpuStages.Count == 0 ? _cpu.Live() :
            _batches < 2 ? null : new(FamilyId, ResolveConditionPerformance(_compact).PhysicalImplementationRevision,
                Interlocked.Read(ref _liveInputs), Volatile.Read(ref _liveMs), _batches - 1);
        public async ValueTask DisposeAsync(FamilyExecutionContext context)
        {
            if (_gpuStages.Count == 0) { _cpu.WriteSummary(); return; }
            if (_fused is not null)
                await context.ExecuteGpuAsync(_ => { _fused.Dispose(); return true; }, CancellationToken.None).ConfigureAwait(false);
            if (_chain is not null)
                await context.ExecuteGpuAsync(_ => { _chain.Dispose(); return true; }, CancellationToken.None).ConfigureAwait(false);
            foreach (var stage in _gpuStages) await stage.Family.DisposeAsync(context).ConfigureAwait(false);
            RuntimeLog.TryBackgroundInfo($"partyNeowPipelineSummary=true;fused={UsesFused};private={UsesPrivate && !UsesFused};stages={_gpuStages.Count};batches={_batches};input={_inputs};output={_outputs};wallMs={_ms};setupMs={_fused?.SetupMs ?? _chain?.SetupMs ?? 0};intermediatePayloadReadbackBytes={(_chain is not null || _fused is not null ? 0 : -1)};readbackBytes={_chain?.ReadbackBytes ?? _fusedReadback};dispatchSyncMs={_chain?.DispatchMs ?? _fusedDispatch};parity={_privateParity}");
        }
    }
}
