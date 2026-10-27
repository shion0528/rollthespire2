#version 450
#define BONES_K_MODE __RT2_BONES_K_MODE__
#define N_STAGE __RT2_N_STAGE__
#define N_DIRECT_NESTED __RT2_N_DIRECT_NESTED__
#define NR_CAPSULE __RT2_NR_CAPSULE__
#define CAPSULE_PHYSICAL __RT2_CAPSULE_PHYSICAL__
#define N_LEAFY_PRE_GATE __RT2_N_LEAFY_PRE_GATE__
#define N_AUTHORED_UPGRADES __RT2_AUTHORED_UPGRADES__
#define N_LOCAL_RESULTS __RT2_LOCAL_RESULTS__
#define N_TRANSFORM_META_OFFSET __RT2_N_TRANSFORM_META_OFFSET__
#ifndef RT2_N_GROUP_OUTPUT
#define RT2_N_GROUP_OUTPUT 0
#endif
#ifndef N_INTERNAL_ORDER
#define N_INTERNAL_ORDER 0
#endif
#ifndef RT2_LEAFY_GATE_LEGACY
#define RT2_LEAFY_GATE_LEGACY 0
#endif
#ifndef RT2_PREFIX_HASH_CACHE
#define RT2_PREFIX_HASH_CACHE 0
#endif
#extension GL_EXT_shader_explicit_arithmetic_types_int64 : require
#extension GL_EXT_shader_explicit_arithmetic_types_float64 : require
layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;
layout(set=0,binding=0,std430) readonly restrict buffer Batch { uint values[]; } batch;
layout(set=0,binding=1,std430) readonly restrict buffer Meta { uint values[]; } plan_meta;
layout(set=0,binding=2,std430) readonly restrict buffer Pools { uint values[]; } other_pool_meta;
layout(set=0,binding=3,std430) readonly restrict buffer Cards { uint ids[]; } other_card_ids;
layout(set=0,binding=4,std430) readonly restrict buffer Strike { uint ids[]; } leafy_strike;
layout(set=0,binding=5,std430) readonly restrict buffer Defend { uint ids[]; } leafy_defend;
layout(set=0,binding=6,std430) restrict buffer Header { uint values[]; } header;
layout(set=0,binding=7,std430) restrict buffer Output { uint values[]; } output_ordinals;
layout(set=0,binding=8,std430) readonly restrict buffer Bones { uint values[]; } bones;
layout(set=0,binding=9,std430) readonly restrict buffer Conditions { uint values[]; } conditions;
layout(set=0,binding=10,std430) readonly restrict buffer Input { uint values[]; } input_ordinals;
#if N_STAGE != 3
layout(set=0,binding=11,std430) restrict buffer CurseSurvivors { uint values[]; } curse_survivors;
layout(set=0,binding=12,std430) restrict buffer PairSurvivors { uint values[]; } pair_survivors;
#endif
#if RT2_PREFIX_HASH_CACHE && N_STAGE == 0
layout(set=0,binding=13,std430) readonly restrict buffer PrefixHashTable { uint values[]; } prefix_hash_cache;
uint prefix_hash_cache_value(uint i) { return prefix_hash_cache.values[i]; }
#endif
uint plan_value(uint i) { return plan_meta.values[i]; }
uint64_t plan_u64(uint i) { return uint64_t(plan_value(i)) | (uint64_t(plan_value(i+1u)) << 32u); }

/*__RT2_NEOW_LOCAL_DONOR__*/
/*__RT2_N_INTERNAL_FILTER_ORDER__*/
/*__RT2_N_JOINT_TRANSFORMS__*/
/*__RT2_CAPSULE_COMPOSITE__*/

#if N_DIRECT_NESTED == 101
bool bones_kaleidoscope_rarity_gate(RngState arrival, uint targets) {
    uint slot;
    bool first = kaleidoscope_rare_prefix(arrival,slot);
    if (targets==1u && first) return true;
    if (targets==2u && !first) return false;
    // Finish the first group's nine Rewards calls in a COPY of route arrival.
    // A failed prefix consumed seven calls; a hit consumed 3*slot+1.
    uint consumed=first ? slot*3u+1u : 7u;
    for(uint i=consumed;i<9u;i++) next_u64(arrival);
    return kaleidoscope_rare_prefix(arrival,slot);
}
#endif

#if N_LOCAL_RESULTS
bool local_operator(uint source, inout RouteRngState state) {
    if (source == 255u) return true;
    uint o = source * 5u;
    uint count = conditions.values[o], t0 = conditions.values[o+1u], t1 = conditions.values[o+2u], t2 = conditions.values[o+3u];
    uint flags = conditions.values[o+4u];
    bool predicate = (flags & 0x80000000u) != 0u;
    uint streams = plan_value(82u);
    bool rewards = (streams & 1u) != 0u, niche = (streams & 2u) != 0u;
    if (source == 3u || source == 28u) {
        if (rewards) { next_float(state.rewards); if (source == 3u) next_float(state.rewards); }
        return true;
    }
    if (source == 14u) {
#if N_DIRECT_NESTED == 101
        if(predicate && !bones_kaleidoscope_rarity_gate(state.rewards,count)) return false;
#endif
        if (rewards) return predicate && (flags & 4u) != 0u
            ? execute_kaleidoscope_ordered_route(t0,t1,state)
            : execute_kaleidoscope_route(count,t0,t1,predicate,state);
        if (niche) { shuffled_pool_order(plan_value(9u),state.niche); shuffled_pool_order(plan_value(9u),state.niche); }
    }
    if (source == 19u && niche) return execute_new_leaf_route(t0,predicate,state);
    if (source == 4u && (streams & 4u) != 0u) return execute_leafy_route(count,t0,t1,predicate,state);
    if (source == 20u && (streams & 8u) != 0u) return execute_phial_holster_route(count,t0,t1,predicate,state);
    if (!rewards) return true;
    if (source == 10u) return execute_arcane_scroll_route(t0,predicate,state);
    if (source == 2u) return execute_hefty_tablet_route(t0,predicate,state);
    if (source == 15u) return execute_lead_paperweight_route(t0,predicate,state);
    if (source == 16u) return execute_lost_coffer_route(count,t0,t1,predicate,state);
    if (source == 22u) return execute_scroll_boxes_route(count,t0,t1,t2,flags & 0x7fffffffu,predicate,state);
    return true;
}
#if N_LEAFY_PRE_GATE
// This Boolean pre-gate owns no continuation state. The real route still uses
// rng_initialize/execute_leafy_route, including both complete state transitions.
uint64_t leafy_pre_gate_first_draw(uint64_t seed, out uint64_t initial_s1) {
    // SplitMix's second output is the initial Xoshiro s1. No other initial
    // word contributes to Xoshiro256**'s first output.
    uint64_t state = seed + make_u64(0x7f4a7c15u, 0x9e3779b9u);
    initial_s1 = splitmix_next(state);
    return rotl64(initial_s1 * uint64_t(5u), 7u) * uint64_t(9u);
}
uint64_t leafy_pre_gate_second_draw(uint64_t seed, uint64_t initial_s1) {
    uint64_t state = seed;
    uint64_t initial_s0 = splitmix_next(state);
    state += make_u64(0x7f4a7c15u, 0x9e3779b9u);
    uint64_t initial_s2 = splitmix_next(state);
    // After the first next_u64, s1 is old_s1 ^ old_s2 ^ old_s0.
    // Initial s3 cannot influence either of the first two output words.
    uint64_t second_s1 = initial_s1 ^ initial_s2 ^ initial_s0;
    return rotl64(second_s1 * uint64_t(5u), 7u) * uint64_t(9u);
}
bool leafy_pre_gate_matches(uint64_t seed, uint target_count, uint target0, uint target1) {
#if !RT2_LEAFY_GATE_LEGACY
    // Admission currently proves exactly two targets. Preserve the canonical
    // behavior for other counts as well, without extending this optimization.
    if (target_count == 2u) {
        uint strike_length = plan_value(58u), defend_length = plan_value(59u);
        if (strike_length == 0u || defend_length == 0u) return false;
        uint64_t initial_s1;
        uint64_t first_draw = leafy_pre_gate_first_draw(seed, initial_s1);
        uint first = leafy_strike.ids[next_int_current_shortcut_from_mantissa(first_draw >> 11u, strike_length)];
        if (first != target0 && first != target1) return false;
        uint64_t second_draw = leafy_pre_gate_second_draw(seed, initial_s1);
        uint second = leafy_defend.ids[next_int_current_shortcut_from_mantissa(second_draw >> 11u, defend_length)];
        return (first == target0 && second == target1) ||
               (first == target1 && second == target0);
    }
#endif
    RouteRngState state;
    state.transformations = rng_initialize(seed);
    return execute_leafy_route(target_count, target0, target1, true, state);
}
#endif
#if N_LEAFY_PRE_GATE || N_TRANSFORM_META_OFFSET >= 0
bool leafy_pre_gate(uint64_t root) {
#if N_TRANSFORM_META_OFFSET >= 0
    if(!joint_transform_pre_gate(root))return false;
#endif
#if N_LEAFY_PRE_GATE
    uint o = 4u * 5u;
    return leafy_pre_gate_matches(root + plan_u64(37u),
        conditions.values[o],conditions.values[o+1u],conditions.values[o+2u]);
#else
    return true;
#endif
}
#endif
#if N_AUTHORED_UPGRADES
uint authored_upgrade_advance(uint source, uint prior) {
    if(source!=3u && source!=28u) return 0u;
    return conditions.values[160u+(source==28u ? 0u : 33u)+(prior==255u ? 32u : prior)];
}
#endif
bool route_at_arrival(uint64_t root, RngState rewards, uint a, uint b, uint advance_a, uint advance_b) {
#if NR_CAPSULE == 1
    return capsule_composite(a,rewards,root);
#endif
    for (uint i=0u; i<plan_value(84u); i++) {
        uint source=plan_value(85u+i);
        if (source != a && source != b) return false;
    }
    RouteRngState state = initialize_route_rng_state(root,rewards);
    if (!local_operator(a,state)) return false;
#if N_AUTHORED_UPGRADES
    for(uint i=0u;i<advance_a;i++) next_u64(state.niche);
#endif
    if (!local_operator(b,state)) return false;
#if N_AUTHORED_UPGRADES
    for(uint i=0u;i<advance_b;i++) next_u64(state.niche);
#endif
    if (plan_value(83u) != 0u) {
        uint offset = n1c_meta(12u), count = n1c_meta(13u);
        if (count == 0u) return false;
        return final_curse_matches(other_card_ids.ids[offset+next_int(state.niche,count)]);
    }
    return true;
}
// A single player has one opening arrival. Unknown authored advances retain the
// existing necessary-condition behavior; Production Exact confirms the candidate.
bool route(uint64_t root, RngState rewards, uint a, uint b) {
    uint da=0u, db=0u;
#if N_AUTHORED_UPGRADES
    da=authored_upgrade_advance(a,255u); db=authored_upgrade_advance(b,a);
    if(da==0xffffffffu || db==0xffffffffu) return true;
#endif
    return route_at_arrival(root,rewards,a,b,da,db);
}

#endif
bool pair_identity(inout RngState rewards, out uint a, out uint b) {
    bool identity = true;
    a = plan_value(81u); b = 255u;
    bool has_bones = (plan_value(56u) & 2u) != 0u;
    if (has_bones) {
        uint length=plan_value(8u);
        if (plan_value(67u) != 0u) {
            uint p=plan_value(25u), q=plan_value(26u);
            for (uint count=length;count>1u;count--) {
                uint j=next_int(rewards,count);
                swap_tracked_position(p,j,count-1u); swap_tracked_position(q,j,count-1u);
            }
            if (p>=2u || q>=2u) return false;
            a=plan_value(p==0u ? 27u : 28u); b=plan_value(p==0u ? 28u : 27u);
        } else {
            uint pool[64];
            for (uint i=0u;i<length;i++) pool[i]=bones.values[i];
            for (uint count=length;count>1u;count--) {
                uint j=next_int(rewards,count); uint temp=pool[j]; pool[j]=pool[count-1u]; pool[count-1u]=temp;
            }
            a=pool[0]; b=pool[1];
        }
        uint64_t pair=relic_bit(a)|relic_bit(b), any=plan_u64(48u), all=plan_u64(50u), ban=plan_u64(52u);
        identity = identity && (any==uint64_t(0u)||(pair&any)!=uint64_t(0u)) && (pair&all)==all && (pair&ban)==uint64_t(0u);
        if (plan_value(12u)!=255u && plan_value(13u)!=255u) {
            identity = identity && pair==(relic_bit(plan_value(12u))|relic_bit(plan_value(13u)));
            a=plan_value(12u); b=plan_value(13u);
        }
    }
    return identity;
}
/*__RT2_BONES_K__*/
bool local_matches(uint64_t root, RngState rewards, uint a, uint b) {
#if N_LOCAL_RESULTS
#if BONES_K_MODE != 0
    return bones_k_matches(root,rewards,a,b);
#endif
#if N_DIRECT_NESTED == 102
    // Fixed K + Phial only, no Final Curse or other stateful pickup. K consumes
    // Rewards/Niche; Phial consumes CombatPotionGeneration. Both pickup orders
    // therefore observe the same product outputs. Reuse both canonical bodies.
    RouteRngState state=initialize_route_rng_state(root,rewards);
    uint k=14u*5u,p=20u*5u;
    if((conditions.values[k+4u]&0x80000000u)!=0u &&
        !execute_kaleidoscope_route(conditions.values[k],conditions.values[k+1u],conditions.values[k+2u],true,state)) return false;
    if((conditions.values[p+4u]&0x80000000u)!=0u &&
        !execute_phial_holster_route(conditions.values[p],conditions.values[p+1u],conditions.values[p+2u],true,state)) return false;
    return true;
#endif
#if NR_CAPSULE == 2
    return bones_capsule_composite(a,b,rewards,root);
#endif
    if (NR_CAPSULE==0 && plan_value(84u)==0u && plan_value(83u)==0u) return true;
    bool forward=route(root,rewards,a,b);
#if N_DIRECT_NESTED == 100 || N_DIRECT_NESTED == 101
    if(forward) return true;
#endif
    if ((plan_value(56u) & 2u)==0u || plan_value(12u)!=255u) return forward;
    bool reverse=route(root,rewards,b,a);
    return forward || reverse;
#else
    return true;
#endif
}
bool matches(uint64_t root) {
#if NR_CAPSULE == 1 && CAPSULE_PHYSICAL != 0 && CAPSULE_PHYSICAL != 5 && CAPSULE_PHYSICAL != 6
    // Bounded direct-only physical ordering; semantic R matcher is shared.
    RngState capsule_rewards=rng_initialize(root+plan_u64(33u));
#if CAPSULE_PHYSICAL == 1
    return capsule_composite(plan_value(81u),capsule_rewards,root);
#elif CAPSULE_PHYSICAL == 2 || CAPSULE_PHYSICAL == 3 || CAPSULE_PHYSICAL == 4
    uint capsule_rarity0,capsule_rarity1;
    if(!capsule_rarity_projection(plan_value(81u),capsule_rewards,capsule_rarity0,capsule_rarity1)) return false;
#if CAPSULE_PHYSICAL == 4
    return true; // Rarity-only private stage/diagnostic, never a complete NR invocation.
#endif
#endif
#endif
#if N_DIRECT_NESTED == 11 || N_DIRECT_NESTED == 12
    RngState lost_prefix=rng_initialize(root+plan_u64(33u));
    uint lost_slot;
    if(!kaleidoscope_rare_prefix(lost_prefix,lost_slot)) return false;
#elif N_DIRECT_NESTED == 13
    RngState lost_potion_prefix=rng_initialize(root+plan_u64(33u));
    for(uint i=0u;i<9u;i++) next_u64(lost_potion_prefix);
    if(next_float(lost_potion_prefix)>0.1f) return false;
#elif N_DIRECT_NESTED == 21
    if(!lead_rare_opportunity(rng_initialize(root+plan_u64(33u)))) return false;
#elif N_DIRECT_NESTED == 30 || N_DIRECT_NESTED == 31
    RouteRngState arcane_state;
    arcane_state.rewards=rng_initialize(root+plan_u64(33u));
    if(!execute_arcane_scroll_route(conditions.values[10u*5u+1u],true,arcane_state)) return false;
#elif N_DIRECT_NESTED == 40
    if(!phial_rare_opportunity(rng_initialize(root+plan_u64(62u)),conditions.values[20u*5u])) return false;
#endif
#if N_DIRECT_NESTED == 5 || N_DIRECT_NESTED == 6
    RouteRngState new_leaf_state;
    new_leaf_state.niche=rng_initialize(root+plan_u64(35u));
    if(!execute_new_leaf_route(conditions.values[19u*5u+1u],true,new_leaf_state)) return false;
#elif N_DIRECT_NESTED == 7
    RngState special_rewards=rng_initialize(root+plan_u64(33u));
    if(!scroll_special_only(special_rewards)) return false;
#endif
#if N_DIRECT_NESTED >= 1 && N_DIRECT_NESTED <= 3
    RngState prefix_rewards = rng_initialize(root + plan_u64(33u));
    uint prefix_slot;
    if (!kaleidoscope_rare_prefix(prefix_rewards,prefix_slot)) return false;
#if N_DIRECT_NESTED == 3
    // Finish group 0's consumed lanes, then require a Rare opportunity in group 1.
    for(uint i=prefix_slot*3u+1u;i<9u;i++) next_u64(prefix_rewards);
    if (!kaleidoscope_rare_prefix(prefix_rewards,prefix_slot)) return false;
#endif
#endif
#if N_LEAFY_PRE_GATE || N_TRANSFORM_META_OFFSET >= 0
    if (!leafy_pre_gate(root)) return false;
#endif
    uint route_mask, curse, curse_ordinal;
    RngState event_rng;
    if (!draw_neow_curse(root,event_rng,curse_ordinal,curse)) return false;
    uint bit = 1u << curse_ordinal;
    if ((plan_value(69u) & bit) != 0u) return false;
    if ((plan_value(68u) & bit) == 0u && !replay_neow_top_identity_after_curse(event_rng,curse,route_mask)) return false;
#if NR_CAPSULE == 1 && CAPSULE_PHYSICAL == 6
    RngState capsule_rewards=rng_initialize(root+plan_u64(33u));
    uint post_entry_rarity0,post_entry_rarity1;
    return capsule_rarity_projection(plan_value(81u),capsule_rewards,post_entry_rarity0,post_entry_rarity1);
#endif
#if NR_CAPSULE == 1 && (CAPSULE_PHYSICAL == 2 || CAPSULE_PHYSICAL == 5)
    return true; // Bounded Entry completed; target projection follows in private R.
#endif
#if N_DIRECT_NESTED >= 10 && N_DIRECT_NESTED <= 14
    uint o=16u*5u;
#if N_DIRECT_NESTED == 12
    RouteRngState lost_state=initialize_route_rng_state(root,lost_prefix);
    return execute_lost_coffer_body(conditions.values[o],conditions.values[o+1u],conditions.values[o+2u],true,lost_state,lost_slot,true);
#else
    RouteRngState lost_state=initialize_route_rng_state(root,rng_initialize(root+plan_u64(33u)));
    return execute_lost_coffer_route(conditions.values[o],conditions.values[o+1u],conditions.values[o+2u],true,lost_state);
#endif
#elif N_DIRECT_NESTED >= 20 && N_DIRECT_NESTED <= 22
    RouteRngState lead_state=initialize_route_rng_state(root,rng_initialize(root+plan_u64(33u)));
#if N_DIRECT_NESTED == 20
    if(!lead_rare_opportunity(lead_state.rewards)) return false;
#endif
    return execute_lead_paperweight_route(conditions.values[15u*5u+1u],true,lead_state);
#elif N_DIRECT_NESTED == 30 || N_DIRECT_NESTED == 31
    return true;
#elif N_DIRECT_NESTED == 32 || N_DIRECT_NESTED == 33
    RouteRngState arcane_state;
    arcane_state.rewards=rng_initialize(root+plan_u64(33u));
    return execute_arcane_scroll_route(conditions.values[10u*5u+1u],true,arcane_state);
#elif N_DIRECT_NESTED >= 40 && N_DIRECT_NESTED <= 42
    RouteRngState phial_state;
    phial_state.combat_potions=rng_initialize(root+plan_u64(62u));
    uint o=20u*5u;
#if N_DIRECT_NESTED == 41
    if(!phial_rare_opportunity(phial_state.combat_potions,conditions.values[o])) return false;
#endif
    return execute_phial_holster_route(conditions.values[o],conditions.values[o+1u],conditions.values[o+2u],true,phial_state);
#endif
#if N_DIRECT_NESTED == 6 || N_DIRECT_NESTED == 7
    return true; // The complete, sole direct nested predicate already passed.
#elif N_DIRECT_NESTED == 5
    RouteRngState replay_leaf;
    replay_leaf.niche=rng_initialize(root+plan_u64(35u));
    return execute_new_leaf_route(conditions.values[19u*5u+1u],true,replay_leaf);
#elif N_DIRECT_NESTED == 8
    RngState local_special=rng_initialize(root+plan_u64(33u));
    return scroll_special_only(local_special);
#endif
#if N_DIRECT_NESTED == 2
    RouteRngState state = initialize_route_rng_state(root,prefix_rewards);
    uint o=14u*5u;
    return execute_kaleidoscope_body(conditions.values[o],conditions.values[o+1u],conditions.values[o+2u],true,state,prefix_slot,true);
#elif N_DIRECT_NESTED == 1 || N_DIRECT_NESTED == 3 || N_DIRECT_NESTED == 4 || N_DIRECT_NESTED == 9
    // Same concrete direct operator call as the retained-prefix candidate.
    // Keeping the generic source switch here would confound replay versus retention.
    RouteRngState state=initialize_route_rng_state(root,rng_initialize(root+plan_u64(33u)));
    uint o=14u*5u;
    return execute_kaleidoscope_route(conditions.values[o],conditions.values[o+1u],conditions.values[o+2u],true,state);
#endif
    RngState rewards = rng_initialize(root + plan_u64(33u));
    uint a,b;
    return pair_identity(rewards,a,b) && local_matches(root,rewards,a,b);
}
// Each 64-thread workgroup consumes at most 8 roots per thread. All lanes,
// including empty/tail/error lanes, rendezvous before the cooperative flush.
// Keeping only survivor payloads in shared memory avoids 8 live root registers
// per thread. Stage 1/2 retain their sparse append to avoid empty-group barriers.
#if RT2_N_GROUP_OUTPUT && (N_STAGE == 0 || N_STAGE == 4)
shared uint rt2_group_count;
shared uint rt2_group_base;
shared uint rt2_group_payload[1536];
void rt2_group_begin() {
    if(gl_LocalInvocationID.x==0u) rt2_group_count=0u;
    barrier();
}
void rt2_group_add(uint ordinal, uint64_t root) {
    uint slot=atomicAdd(rt2_group_count,1u);
    uint dst=slot*3u;
    rt2_group_payload[dst]=ordinal;
    rt2_group_payload[dst+1u]=uint(root);
    rt2_group_payload[dst+2u]=uint(root>>32u);
}
void rt2_group_flush() {
    barrier();
#if N_STAGE == 0
    uint capacity=batch.values[6u];
#else
    uint capacity=min(batch.values[6u],batch.values[5u]>0u ? (batch.values[5u]-1u)/3u : 0u);
#endif
    if(gl_LocalInvocationID.x==0u) {
        uint count=rt2_group_count;
        rt2_group_base=0u;
        if(count!=0u) {
#if N_STAGE == 0
            rt2_group_base=atomicAdd(header.values[6u],count);
#else
            rt2_group_base=atomicAdd(output_ordinals.values[0u],count);
#endif
            // The production capacities and at most 512 additions per group
            // keep this addition below uint overflow. Keep the old error ABI.
            if(rt2_group_base>capacity || count>capacity-rt2_group_base)
                atomicExchange(header.values[3u],1u);
        }
    }
    barrier();
    for(uint i=gl_LocalInvocationID.x;i<rt2_group_count;i+=64u) {
        uint slot=rt2_group_base+i;
        if(slot>=capacity) continue;
        uint src=i*3u;
#if N_STAGE == 0
        uint dst=slot*3u;
        curse_survivors.values[dst]=rt2_group_payload[src];
        curse_survivors.values[dst+1u]=rt2_group_payload[src+1u];
        curse_survivors.values[dst+2u]=rt2_group_payload[src+2u];
#else
        uint dst=1u+slot*3u;
        output_ordinals.values[dst]=rt2_group_payload[src];
        output_ordinals.values[dst+1u]=rt2_group_payload[src+1u];
        output_ordinals.values[dst+2u]=rt2_group_payload[src+2u];
#endif
    }
}
#endif
void append_ordinal(uint ordinal) {
#if BONES_K_MODE == 1
    ordinal |= bones_k_route_mask << 24u;
#endif
    uint slot=atomicAdd(header.values[2u],1u);
    if(slot<batch.values[5u]) output_ordinals.values[slot]=ordinal;
    else atomicExchange(header.values[3u],1u);
}
#if N_STAGE == 4
void main() {
#if RT2_N_GROUP_OUTPUT
    rt2_group_begin();
    bool input_valid=header.values[3u]==0u;
#else
    if (header.values[3u]!=0u) return;
#endif
    uint first=gl_GlobalInvocationID.x*8u;
    uint count=min(header.values[6u],batch.values[6u]);
#if RT2_N_GROUP_OUTPUT
    for(uint lane=0u;lane<8u && first+lane<count && input_valid;lane++) {
#else
    for(uint lane=0u;lane<8u && first+lane<count;lane++) {
#endif
        uint src=(first+lane)*3u;
        uint ordinal=curse_survivors.values[src];
        uint64_t root=make_u64(curse_survivors.values[src+1u],curse_survivors.values[src+2u]);
        if(!internal_final_curse_gate(root)) continue;
#if RT2_N_GROUP_OUTPUT
        rt2_group_add(ordinal,root);
#else
        uint slot=atomicAdd(output_ordinals.values[0u],1u);
        if(slot>=batch.values[6u] || slot>=(batch.values[5u]-1u)/3u) {
            atomicExchange(header.values[3u],1u); continue;
        }
        uint dst=1u+slot*3u;
        output_ordinals.values[dst]=ordinal;
        output_ordinals.values[dst+1u]=uint(root);
        output_ordinals.values[dst+2u]=uint(root>>32u);
#endif
    }
#if RT2_N_GROUP_OUTPUT
    rt2_group_flush();
#endif
}
#elif N_STAGE == 1 || N_STAGE == 2
void main() {
    if (header.values[3u]!=0u) return;
    uint first=gl_GlobalInvocationID.x*8u;
#if N_STAGE == 1 && N_INTERNAL_ORDER
    uint count=min(output_ordinals.values[0u],batch.values[6u]);
#else
    uint count=min(header.values[N_STAGE==1 ? 6u : 7u],batch.values[N_STAGE==1 ? 6u : 7u]);
#endif
    for (uint lane=0u;lane<8u && first+lane<count;lane++) {
#if N_STAGE == 1
#if N_INTERNAL_ORDER
        uint src=1u+(first+lane)*3u;
        uint ordinal=output_ordinals.values[src];
        uint64_t root=make_u64(output_ordinals.values[src+1u],output_ordinals.values[src+2u]);
#else
        uint src=(first+lane)*3u;
        uint ordinal=curse_survivors.values[src];
        uint64_t root=make_u64(curse_survivors.values[src+1u],curse_survivors.values[src+2u]);
#endif
        RngState rewards=rng_initialize(root+plan_u64(33u));
        uint a,b;
        if (!pair_identity(rewards,a,b)) continue;
        uint slot=atomicAdd(header.values[7u],1u);
        if(slot>=batch.values[7u]) { atomicExchange(header.values[3u],1u); continue; }
        uint dst=slot*12u;
        pair_survivors.values[dst]=ordinal;
        pair_survivors.values[dst+1u]=uint(root); pair_survivors.values[dst+2u]=uint(root>>32u);
        pair_survivors.values[dst+3u]=a|(b<<8u);
        pair_survivors.values[dst+4u]=uint(rewards.s0); pair_survivors.values[dst+5u]=uint(rewards.s0>>32u);
        pair_survivors.values[dst+6u]=uint(rewards.s1); pair_survivors.values[dst+7u]=uint(rewards.s1>>32u);
        pair_survivors.values[dst+8u]=uint(rewards.s2); pair_survivors.values[dst+9u]=uint(rewards.s2>>32u);
        pair_survivors.values[dst+10u]=uint(rewards.s3); pair_survivors.values[dst+11u]=uint(rewards.s3>>32u);
#else
        uint src=(first+lane)*12u;
        uint64_t root=make_u64(pair_survivors.values[src+1u],pair_survivors.values[src+2u]);
        uint pair=pair_survivors.values[src+3u];
        RngState rewards;
        rewards.s0=make_u64(pair_survivors.values[src+4u],pair_survivors.values[src+5u]);
        rewards.s1=make_u64(pair_survivors.values[src+6u],pair_survivors.values[src+7u]);
        rewards.s2=make_u64(pair_survivors.values[src+8u],pair_survivors.values[src+9u]);
        rewards.s3=make_u64(pair_survivors.values[src+10u],pair_survivors.values[src+11u]);
        if(local_matches(root,rewards,pair&255u,pair>>8u)) append_ordinal(pair_survivors.values[src]);
#endif
    }
}
#else
void main() {
#if RT2_N_GROUP_OUTPUT && N_STAGE == 0
    rt2_group_begin();
    bool hash_valid=true;
#endif
    uint first=gl_GlobalInvocationID.x*8u;
    uint count=batch.values[3u];
    uint64_t base=uint64_t(batch.values[0u])|(uint64_t(batch.values[1u])<<32u);
    uint64_t seed_first8; uint tail;
#if RT2_PREFIX_HASH_CACHE && N_STAGE == 0
    uint prefix_row, suffix_ordinal;
    uint64_t prefix_hash;
    if(batch.values[4u]==0u && first<count &&
        !prefix_hash_cache_initialize(first,prefix_row,suffix_ordinal,tail,prefix_hash)) {
        atomicExchange(header.values[3u],1u);
#if RT2_N_GROUP_OUTPUT && N_STAGE == 0
        hash_valid=false;
#else
        return;
#endif
    }
#else
    if (batch.values[4u]==0u && first<count) encode_seed12_packed(base+uint64_t(first),seed_first8,tail);
#endif
    // Donor dispatch accounting: no globally contended atomic per eight roots.
    if (first==0u) header.values[4u]=count;
#if RT2_N_GROUP_OUTPUT && N_STAGE == 0
    for (uint lane=0u;lane<8u && first+lane<count && hash_valid;lane++) {
#else
    for (uint lane=0u;lane<8u && first+lane<count;lane++) {
#endif
        uint ordinal=batch.values[4u]==0u ? first+lane : input_ordinals.values[first+lane];
#if RT2_PREFIX_HASH_CACHE && N_STAGE == 0
        uint64_t root=batch.values[4u]==0u ? xxhash64_seed12_from_prefix(prefix_hash,tail) : root_hash_for_ordinal(base+uint64_t(ordinal));
#else
        uint64_t root=batch.values[4u]==0u ? xxhash64_seed12_packed(seed_first8,tail) : root_hash_for_ordinal(base+uint64_t(ordinal));
#endif
#if N_STAGE == 0
        RngState event_rng; uint curse_ordinal,curse;
#if N_LEAFY_PRE_GATE || N_TRANSFORM_META_OFFSET >= 0
        if (leafy_pre_gate(root) && draw_neow_curse(root,event_rng,curse_ordinal,curse) && (plan_value(69u)&(1u<<curse_ordinal))==0u) {
#else
        if (draw_neow_curse(root,event_rng,curse_ordinal,curse) && (plan_value(69u)&(1u<<curse_ordinal))==0u) {
#endif
#if RT2_N_GROUP_OUTPUT
            rt2_group_add(ordinal,root);
#else
            uint slot=atomicAdd(header.values[6u],1u);
            if(slot<batch.values[6u]) {
                uint dst=slot*3u;
                curse_survivors.values[dst]=ordinal;
                curse_survivors.values[dst+1u]=uint(root); curse_survivors.values[dst+2u]=uint(root>>32u);
            } else atomicExchange(header.values[3u],1u);
#endif
        }
#else
        if (matches(root)) append_ordinal(ordinal);
#endif
        if(batch.values[4u]==0u && lane<7u) {
#if RT2_PREFIX_HASH_CACHE && N_STAGE == 0
            if(first+lane+1u<count && !prefix_hash_cache_advance(prefix_row,suffix_ordinal,tail,prefix_hash)) {
                atomicExchange(header.values[3u],1u);
#if RT2_N_GROUP_OUTPUT && N_STAGE == 0
                hash_valid=false;
                break;
#else
                return;
#endif
            }
#else
            advance_seed12_packed(seed_first8,tail);
#endif
        }
    }
#if RT2_N_GROUP_OUTPUT && N_STAGE == 0
    rt2_group_flush();
#endif
}
#endif
