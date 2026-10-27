using System.Globalization;
using RolltheSpire2.Bootstrap;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Presentation.ContentNames;
using RolltheSpire2.Presentation.Ui1;
using RolltheSpire2.Search.Compilation;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Selectivity;
using RolltheSpire2.Search.Semantics;
using RolltheSpire2.Ui.Persistence;

namespace RolltheSpire2.Ui.Shell;

internal sealed partial class QueryWorkbenchFrame
{
    private string _analysisKey="", _expectationText="", _conditionText="";
    private FamilySearchEtaProjectionV1? _previewEta;
    private readonly WorkbenchProbabilityPreviewQueue _probabilityPreview = new();
    private bool _probabilityPending;
    internal static string CompactNumber(double value)
    {
        if(!double.IsFinite(value) || value<0) return "—";
        string[] units=["","K","M","B","T"];
        int unit=0;while(value>=999.5 && unit<units.Length-1){value/=1000;unit++;}
        return value.ToString(value>=100?"0":value>=10?"0.#":"0.##",CultureInfo.InvariantCulture)+units[unit];
    }
    internal static string Rarity(double? probability) => probability is not { } p || !double.IsFinite(p) || p<0 || p>1
        ? "—" : p==0 ? "1 / ∞" : "1 / "+CompactNumber(1/p);
    private string Rate(double? rate) => rate is >0 && double.IsFinite(rate.Value) ? _text.Format("workflow.rate",CompactNumber(rate.Value)) : "—";
    private string Duration(double? milliseconds)
    {
        if(milliseconds is not { } ms || !double.IsFinite(ms) || ms<0) return "—";
        double seconds=ms/1000;
        return seconds<60 ? _text.Format("workflow.seconds",seconds.ToString("0.#",CultureInfo.InvariantCulture)) :
            seconds<3600 ? _text.Format("workflow.minutes",(seconds/60).ToString("0.#",CultureInfo.InvariantCulture)) :
            seconds<86400 ? _text.Format("workflow.hours",(seconds/3600).ToString("0.#",CultureInfo.InvariantCulture)) :
            _text.Format("workflow.days",CompactNumber(seconds/86400));
    }
    private SearchResourceBudget SearchResources => _persistence.SearchResources;
    private SearchOptimizationSettings SearchOptimizations => _persistence.SearchOptimizations;
    private int SearchWorkers => SearchResources.ResolveCpuWorkers(System.Environment.ProcessorCount);
    private bool SearchCpuOnly => _persistence.Preferences.SearchMode == "CPU" || SearchResources.GpuPercent == 0;
    private string AnalysisKey(string query) => query+_language+":"+_target+":"+_persistence.Preferences.SearchMode+":"+SearchWorkers+
        ":cpuBudget="+SearchResources.CpuPercent+":gpuBudget="+SearchResources.GpuPercent+
        ":optimizations="+SearchOptimizations.Key+
        ":gpu="+FamilyDeviceProfileFoundation.Initialization.Capture().Revision;
    private string _draftCompileIssue="";
    private bool UpdateProbabilityPanel(WorkbenchSearchDraft draft,string serialized)
    {
        if(_summary is null) return true;
        if(_analysisKey==AnalysisKey(serialized)) return _draftCompileIssue.Length==0;
        _analysisKey=AnalysisKey(serialized);_draftCompileIssue="";_expectationContext="";
        _probabilityPreview.Invalidate();
        _probabilityPending = false;
        CompiledSearch compiled;
        try { compiled=draft.Compile(_runtime,out _); }
        catch(Exception ex) {
            RuntimeLog.Warn("workbenchCompileUnavailable="+ex.Message);
            _draftCompileIssue=ex.Message.StartsWith(RolltheSpire2.Compatibility.ProfileSeedGenerator.UnavailableCode, StringComparison.Ordinal)
                ? SeedProfileUnavailableMessage()
                : _text.Get(TransformationContinuationIssueKey(ex.Message) ?? "integration.rejected");
            SetCheckpointFingerprint("");
            _previewEta=null;_familyEntries.Clear();
            var q=draft.Query;
            void Pending(string id,bool present) {if(present)_familyEntries.Add(new(id,FamilyTitle(id),null,"—",""));}
            Pending("N",q.OpeningRoute is not null || q.StructuredOpeningEffects.Count>0);
            Pending("T",q.TransformationAggregate is not null);Pending("C",q.CombatCardRewards is not null || q.CombatPotionRewards is not null);
            Pending("A",q.AncientBranches.Any(b=>b.OptionAny.Count>0||b.SeaGlassTargetAny.Count>0)||q.LegacyWorld.AncientOptionFilters.Count>0);
            Pending("W",q.AncientBranches.Count>0||q.EventSequenceConstraints.Count>0||q.VariantBossBranches.Count>0);
            Pending("R",q.RelicSequenceConstraints.Count>0||q.RelicShopSequenceConditions.Count>0);Pending("E",q.EventResultConditions.Count>0);
            Pending("M",q.StandardMaps.Count>0);Pending("S",q.MerchantColorlessSequenceConditions.Count>0);
            RenderFamilyDashboard();PresentExpectations(null,null,null);
            return false;
        }
        SetCheckpointFingerprint(compiled.SemanticFingerprint);
        _previewEta = null;
        _probabilityPending = true;
        _familyEntries.Clear(); RenderFamilyDashboard();
        _expectationContext = _language == "zh" ? "正在计算当前条件…" : "Calculating current conditions…";
        PresentExpectations(null, null, null);
        if (_expectationValues[0] is { } rarity) rarity.Text = rarity.TooltipText = _language == "zh" ? "计算中…" : "Calculating…";
        _probabilityPreview.Submit(new(_analysisKey, compiled, _target, SearchWorkers, SearchCpuOnly, SearchResources, SearchOptimizations));
        // Estimate failures do not change admission; canonical compile failures do.
        return true;
    }

    private void PollProbabilityPreview()
    {
        if (!_probabilityPreview.TryTake(out var completed) || completed is null ||
            completed.Request.Key != _analysisKey || _session is not null || _showResults) return;
        _probabilityPending = false;
        if (completed.Error is { } error)
        {
            RuntimeLog.Warn("workbenchProbabilityUnavailable=" + error.Message);
            _familyEntries.Clear(); RenderFamilyDashboard();
            _expectationContext = _language == "zh" ? "当前条件的估算不可用。" : "Estimate unavailable for these conditions.";
            PresentExpectations(null, null, null);
            return;
        }
        try
        {
            DescribeProbability(completed.Request.Compiled, completed.Preview!);
            if (_summary is not null) _summary.Text = "";
            RenderFamilyDashboard();
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("workbenchProbabilityPresentationUnavailable=" + ex.Message);
            _familyEntries.Clear(); RenderFamilyDashboard(); _expectationContext = "";
            PresentExpectations(null, null, null);
        }
        if (completed.Milliseconds >= 100)
            RuntimeLog.Info($"workbenchPreviewCompleted=true;elapsedMs={completed.Milliseconds:F1};background=true");
    }

    internal string DescribeProbability(CompiledSearch compiled) => DescribeProbability(compiled,
        WorkbenchProbabilityPreview.Compute(compiled, _target, SearchWorkers, SearchCpuOnly, SearchResources, SearchOptimizations));

    internal string DescribeProbability(CompiledSearch compiled, WorkbenchProbabilityPreview preview)
    {
        foreach (string issue in preview.Issues) RuntimeLog.Warn(issue);
        if (compiled.Context.Party is { } party)
        {
            var partyProbability = preview.Joint;
            _familyEntries.Clear(); _previewEta = null;
            if (PartyInitialQuery.HasWorld(compiled.NormalizedQuery) || compiled.NormalizedQuery.EventSequenceConstraints.Count > 0 ||
                compiled.NormalizedQuery.LegacyWorld.AncientIdentityFilters.Count > 0)
                _familyEntries.Add(new("W", FamilyTitle("W"), null, "—", _language == "zh" ? "整桌共用条件；概率只计一次。" : "Shared table conditions; probability counted once."));
            if (compiled.NormalizedQuery.Players.Any(p => !p.Offers.IsEmpty || p.SelectedOption is not null || p.Conditions.OpeningRoute is not null))
                _familyEntries.Add(new("N", FamilyTitle("N"), null, "—", string.Join("\n", compiled.NormalizedQuery.Players.Where(p => !p.Offers.IsEmpty || p.SelectedOption is not null || p.Conditions.OpeningRoute is not null)
                    .Select(p => $"P{p.Slot + 1} · " + (p.SelectedOption is null ? (_language == "zh" ? "初始选项" : "Initial offers") :
                        p.SelectedOption.Intent == Core.Effects.PartyNeowIntent.PremiseOnly ? (_language == "zh" ? "仅领取前提" : "Premise only") : (_language == "zh" ? "领取结果" : "Selected result")) + ": " +
                        string.Join(",", p.Offers.All.Concat(p.SelectedOption is null ? p.Conditions.OpeningRoute is { } n ? [n.RouteRelicKey] : [] : new[] { p.SelectedOption.Option }).Select(k =>
                            RolltheSpire2.Infrastructure.ContentNames.RuntimeGameContentNameResolver.Create(_language).Resolve(k, GameContentKind.Relic)))))));
            foreach (var family in new[] { "R", "S", "E", "A", "C" })
            {
                var owners = compiled.NormalizedQuery.Players.Where(p => family switch
                {
                    "R" => p.Conditions.RelicSequenceConstraints.Count > 0 || p.Conditions.RelicShopSequenceConditions.Count > 0,
                    "S" => p.Conditions.MerchantColorlessSequenceConditions.Count > 0,
                    "E" => p.Conditions.EventResultConditions.Count > 0 || p.Conditions.EventSequenceConstraints.Count > 0,
                    "A" => p.Conditions.AncientBranches.Count > 0 || p.Conditions.LegacyWorld.AncientOptionFilters.Count > 0,
                    "C" => p.Conditions.HasCombatRewardConstraints,
                    _ => false
                }).ToArray();
                if (owners.Length > 0)
                {
                    double? probability = null;
                    string detail = string.Join(" · ", owners.Select(p => $"P{p.Slot + 1}"));
                    if (family == "C")
                    {
                        var terms = owners.Select(p => (Slot: p.Slot, P: partyProbability.KnownComponents
                            .FirstOrDefault(c => c.Id == $"party:player:{p.Slot}/C")?.Probability)).ToArray();
                        if (terms.All(t => t.P.HasValue)) probability = terms.Aggregate(1d, (p, t) => p * t.P!.Value);
                        detail = (_language == "zh" ? "给定各自开局条件\n" : "Conditional on each player's opening\n") +
                            string.Join("\n", terms.Select(t => $"P{t.Slot + 1} · " + Rarity(t.P)));
                    }
                    _familyEntries.Add(new(family, FamilyTitle(family), probability, "—", detail));
                }
            }
            if (compiled.NormalizedQuery.StandardMaps.Count > 0)
                _familyEntries.Add(new("M", FamilyTitle("M"), null, "—", _language == "zh" ? "共用开局地图" : "Shared initial maps"));
            string premise = _language == "zh" ? "共享事实只计一次；个人条件采用独立近似。" : "Shared facts counted once; personal conditions use an independence approximation.";
            _expectationText = ExpectationText(null, partyProbability.Probability, null) + "\n" + premise;
            if (partyProbability.Probability is null)
                _expectationText += "\n" + (_language == "zh" ? "概率未知：部分条件尚无多人模型。" : "Probability unknown: some conditions lack a multiplayer model.");
            _expectationContext = premise + (partyProbability.Probability is null
                ? "\n" + (_language == "zh" ? "概率未知：部分条件尚无多人模型。" : "Probability unknown: some conditions lack a multiplayer model.") : "");
            PresentExpectations(null, partyProbability.Probability, null);
            _conditionText = premise + "\n" + string.Join("\n", partyProbability.KnownComponents.Select(c => c.Condition + " · " + Rarity(c.Probability))) +
                "\n" + string.Join("\n", partyProbability.UnknownComponents);
            return _expectationText + "\n" + _conditionText;
        }
        var view=preview.View!;
        var q=compiled.NormalizedQuery;
        var names=RolltheSpire2.Infrastructure.ContentNames.RuntimeGameContentNameResolver.Create(_language);
        string Name(ModelKey k) => names.Resolve(k,k.Category switch {
            "CARD"=>GameContentKind.Card,"RELIC"=>GameContentKind.Relic,"EVENT"=>GameContentKind.Event,
            "CHARACTER"=>GameContentKind.Character,"POTION"=>GameContentKind.Potion,"ENCOUNTER"=>GameContentKind.Encounter,
            "ACT"=>GameContentKind.Act,_=>GameContentKind.Relic });
        string Keys(IEnumerable<ModelKey> keys) => string.Join(" · ",keys.Select(Name));
        var relicKeys=q.RelicSequenceConstraints.SelectMany(c=>c.Keys.Any.Concat(c.Keys.All).Concat(c.Keys.Ban))
            .Concat(q.RelicShopSequenceConditions.SelectMany(c=>c.Slots).Where(k=>k.HasValue).Select(k=>k!.Value));
        string Probability(double? p) => Rarity(p);
        var output=new List<string>();
        _familyEntries.Clear();
        _previewEta=preview.Eta;
        var costs=preview.Costs.ToDictionary(p=>p.Key,p=>BestQuotedSpeed(p.Value.Cpu,p.Value.Gpu));
        string Cost(params string[] ids) {
            string FamilyName(string id)=>_text.Get(id switch {
                "N.Neow"=>"query.domain.neow","R.Relic"=>"query.domain.relics","W.World"=>"coverage.world",
                "A.AncientOption"=>"query.domain.ancients",_=>"query.domain.events"});
            var found=ids.Where(costs.ContainsKey).Select(id=>ids.Length>1?FamilyName(id)+" · "+costs[id]:costs[id]).ToArray();
            return found.Length==0?"—":string.Join("\n",found.Distinct());
        }
        foreach(var row in view.Rows)
        {
            if(row.Kind==SearchProbabilityRowKind.EventResult) continue;
            var (title,detail,cost)=row.Kind switch {
                SearchProbabilityRowKind.Neow => (_text.Get("query.domain.neow"),Keys((q.OpeningRoute is { } n?new[]{n.RouteRelicKey}:q.LegacyNeow.NeowRelics.Any.Concat(q.LegacyNeow.NeowRelics.All))
                    .Concat(q.OpeningRouteRelicRequirement?.RequiredRelicKeys??[]).Concat(q.StructuredOpeningEffects.SelectMany(c=>c.OutputKeys))),
                    q.StructuredOpeningEffects.Any(c=>c.OutputKind==NeowStructuredOutputKind.Relic)?Cost("N.Neow","R.Relic"):Cost("N.Neow")),
                SearchProbabilityRowKind.Relic => (_text.Get("query.domain.relics"),Keys(relicKeys),Cost("R.Relic")),
                SearchProbabilityRowKind.CapsuleRelicJoint => (_text.Get("coverage.capsule.joint"),Keys(q.StructuredOpeningEffects.SelectMany(c=>c.OutputKeys).Concat(relicKeys).Distinct()),Cost("R.Relic","N.Neow")),
                SearchProbabilityRowKind.Ancient => (_text.Get("query.domain.ancients"),Keys(q.AncientBranches.SelectMany(b=>b.OptionAny).Concat(q.LegacyWorld.AncientOptionFilters.SelectMany(f=>f.Keys.All))),Cost("A.AncientOption","W.World")),
                SearchProbabilityRowKind.WorldEvent => (_text.Get("coverage.world"),Keys(q.EventSequenceConstraints.SelectMany(c=>c.Keys.Any.Concat(c.Keys.All).Concat(c.Keys.Ban))
                    .Concat(q.AncientBranches.Select(b=>b.AncientKey)).Concat(q.LegacyWorld.AncientIdentityFilters.SelectMany(f=>f.Keys.Any.Concat(f.Keys.All).Concat(f.Keys.Ban)))),Cost("W.World")),
                SearchProbabilityRowKind.CombatReward => (_text.Get("query.domain.combat_rewards"),Keys((q.CombatCardRewards?.Slots.Where(k=>k.HasValue).Select(k=>k!.Value)??[])
                    .Concat(q.CombatPotionRewards?.Slots.Where(p=>p.PotionKey.HasValue).Select(p=>p.PotionKey!.Value)??[])),Cost("C.CombatReward")),
                SearchProbabilityRowKind.TransformationAggregate => (_text.Get("query.summary.transform_aggregate"),Keys(q.TransformationAggregate!.TargetMultiset),Cost("T.TransformationAggregate")),
                _ => (_text.Get("coverage.shop"),Keys(q.MerchantColorlessSequenceConditions.SelectMany(c=>c.Slots).Where(k=>k.HasValue).Select(k=>k!.Value)),Cost("S.MerchantShopColorless"))
            };
            if(row.Kind==SearchProbabilityRowKind.TransformationAggregate && q.TransformationAggregate is { RequiresRareRemainder: true } mixed)
                detail += "\n" + _text.Format("query.transform.remaining_rare", mixed.OpportunityCount - mixed.TargetMultiset.Count);
            if(row.Kind==SearchProbabilityRowKind.TransformationAggregate && row.Probability is null)
                detail += (detail.Length>0 ? "\n" : "") + (_language=="zh"
                    ? "概率未知：这组来源或开局副作用的联合模型尚未闭合；不会用独立相乘代替。"
                    : "Probability unknown: the joint model for these sources or opening effects is incomplete; no independent-product substitute is used.");
            string id=row.Kind switch {
                SearchProbabilityRowKind.Neow=>"N",SearchProbabilityRowKind.Relic=>"R",SearchProbabilityRowKind.CapsuleRelicJoint=>"N/R",
                SearchProbabilityRowKind.Ancient=>"A",SearchProbabilityRowKind.WorldEvent=>"W",SearchProbabilityRowKind.CombatReward=>"C",
                SearchProbabilityRowKind.TransformationAggregate=>"T",_=>"S"};
            if(id=="N/R") {
                _familyEntries.Add(new("N",FamilyTitle("N"),null,Cost("N.Neow"),title+" · "+Rarity(row.Probability)+"\n"+detail));
                _familyEntries.Add(new("R",FamilyTitle("R"),null,Cost("R.Relic"),title+" · "+Rarity(row.Probability)+"\n"+detail));
            } else _familyEntries.Add(new(id,FamilyTitle(id),row.Probability,costs.FirstOrDefault(k=>k.Key.StartsWith(id+".")).Value??"—",detail));
            output.Add(title+(detail.Length>0?"\n"+detail:"")+"\n"+_text.Get("workflow.rarity")+" "+Probability(row.Probability)+"\n"+cost);
        }
        if(q.StandardMaps.Count>0)
        {
            var mapEstimate = preview.Map!;
            string detail = MapProbabilityExplanation(mapEstimate.EvidenceCode, mapEstimate.Probability.HasValue, _language=="zh");
            string rarity = mapEstimate.Probability.HasValue ? Probability(mapEstimate.Probability) : (_language=="zh" ? "未知" : "Unknown");
            output.Add(_text.Get("coverage.map")+"\n"+_text.Get("workflow.rarity")+" "+rarity+"\n"+detail+"\n"+Cost("M.StandardMap"));
            _familyEntries.Add(new("M",_text.Get("coverage.map"),mapEstimate.Probability,Cost("M.StandardMap"),detail));
        }
        var eventDetails=new List<string>();
        foreach(var c in q.EventResultConditions) {
            string entry=c.Kind switch {
                EventResultConditionKind.FakeMerchantOfferedFakeRelic=>"FAKE_MERCHANT",
                EventResultConditionKind.TrashHeapDiveRelic or EventResultConditionKind.TrashHeapGrabCard=>"TRASH_HEAP",
                EventResultConditionKind.ColorfulPhilosophersOfferedColor=>"COLORFUL_PHILOSOPHERS",
                EventResultConditionKind.TrialCase=>"TRIAL",EventResultConditionKind.TinkerTimeTypeAndRider=>"TINKER_TIME",
                _=>EventResultTransformSemantics.Entry(c.Kind) };
            string detail=c.Kind switch {
                EventResultConditionKind.TrialCase=>_text.Get("query.event.results.case_"+((int)c.TrialCase!+1)),
                EventResultConditionKind.TinkerTimeTypeAndRider=>_text.Get("query.event.results.card_type_"+((int)c.TinkerCardType!+1))+
                    (c.TinkerRider is { } rider?" · "+_text.Get("query.event.results.rider."+rider.ToString().ToLowerInvariant()):""),
                _=>Name(c.TargetKey)+(c.MorphicGroveSecondCard is { } second?"\n"+Name(second):"") };
            var eventPreview=preview.Events[c];
            var estimate=eventPreview.Estimate;
            string eventSpeed=BestQuotedSpeed(eventPreview.Cpu,eventPreview.Gpu);
            eventDetails.Add(Name(new("EVENT",entry))+"\n"+detail+"\n"+Probability(estimate.Probability)+"\n"+eventSpeed);
            output.Add(Name(new("EVENT",entry))+"\n"+detail+"\n"+_text.Get("workflow.rarity")+" "+Probability(estimate.Probability)+"\n"+eventSpeed);
        }
        if(eventDetails.Count>0) _familyEntries.Add(new("E",_text.Get("query.domain.events"),
            view.Rows.FirstOrDefault(r=>r.Kind==SearchProbabilityRowKind.EventResult)?.Probability,Cost("E.EventResult"),string.Join("\n\n",eventDetails)));
        // Execution Families remain visible even when their probability is priced in a shared block.
        foreach(var id in costs.Keys.Select(k=>k.Split('.')[0]))
            if(!_familyEntries.Any(e=>e.Id==id)) _familyEntries.Add(new(id,FamilyTitle(id),null,costs.First(k=>k.Key.StartsWith(id+".")).Value,_text.Get("workflow.shared")));
        double? rate=EtaRate(_previewEta);
        _expectationText=ExpectationText(_previewEta?.FirstResultSearchMeanMs,view.Status==SearchProbabilityQuickViewStatus.Impossible?0:view.TotalProbability,rate);
        _expectationContext = compiled.Context.Authority.UsesBestEffortModel
            ? (view.TotalProbability == 0 || view.Status == SearchProbabilityQuickViewStatus.Impossible
                ? (_language == "zh" ? "按当前模型无匹配；模组规则仍未验证。" : "No match under the current model; Mod rules remain unverified.")
                : (_language == "zh" ? "按当前模型估算；模组规则可能影响结果。" : "Current-model estimate; Mod rules may affect the result.")) : "";
        PresentExpectations(_previewEta?.FirstResultSearchMeanMs,view.Status==SearchProbabilityQuickViewStatus.Impossible?0:view.TotalProbability,rate);
        _conditionText=string.Join("\n\n",output);
        return _expectationText+"\n\n"+_conditionText;
    }
    private static double? EtaRate(FamilySearchEtaProjectionV1? eta) =>
        eta?.TargetMeanRootHorizon is { } roots && eta.TargetSearchMeanMs is >0 ? roots*1000d/eta.TargetSearchMeanMs.Value :
        eta?.FixedScanRangeMs is >0 ? eta.ScanCount*1000d/eta.FixedScanRangeMs.Value : null;
    internal static bool PreferGpuQuote(double? cpuNs,double? gpuNs) =>
        gpuNs is >0 && double.IsFinite(gpuNs.Value) &&
        (!(cpuNs is >0 && double.IsFinite(cpuNs.Value)) || gpuNs.Value<cpuNs.Value);
    private string BestQuotedSpeed(double? cpuNs,double? gpuNs)
    {
        bool gpu=PreferGpuQuote(cpuNs,gpuNs);
        double? ns=gpu?gpuNs:cpuNs;
        return ns is >0 && double.IsFinite(ns.Value)
            ? _text.Format(gpu?"workflow.quote.gpu":"workflow.quote.cpu",CompactNumber(1e9/ns.Value)) : "—";
    }
    internal static string MapProbabilityExplanation(string evidence, bool known, bool zh) => known
        ? (zh ? "依据已有地图样本估计。" : "Estimated from the existing map sample corpus.")
        : evidence.Contains("NoObserved",StringComparison.Ordinal)
            ? (zh ? "概率未知：已有样本中没有命中，不能据此认定条件不可能。" : "Probability unknown: no hits in the existing samples; this does not prove impossibility.")
            : evidence.Contains("ContextMismatch",StringComparison.Ordinal)
                ? (zh ? "概率未知：现有样本不适用于当前游戏上下文。" : "Probability unknown: the existing samples do not cover this game context.")
                : (zh ? "概率未知：现有样本未覆盖这组地图条件的联合分布。" : "Probability unknown: the existing samples do not cover this combination of map conditions.");

    private string ExpectationText(double? time,double? probability,double? rate) =>
        _text.Get("workflow.total_rarity")+"   "+(probability is null && _familyEntries.Any(e=>e.Id is "T" or "M") ? (_language=="zh"?"未知":"Unknown") : DashboardRarity(probability))+"\n"+
        _text.Get("workflow.expected_speed")+"   "+Rate(rate)+"\n"+
        _text.Get("workflow.eta")+"   "+Duration(time);
}
