// Single-player core: no party state or multiplayer effect implementation.
// Adopted numeric functions from Beta110GpuNeowP1Common.glsl.
// HistoricalNextInt retained; route state is strictly Neow-local. No Relic Bag.
struct RngState { uint64_t s0; uint64_t s1; uint64_t s2; uint64_t s3; };
struct RouteRngState { RngState rewards; RngState niche; RngState transformations; RngState combat_potions; };
uint64_t make_u64(uint low, uint high) {
    return uint64_t(low) | (uint64_t(high) << 32u);
}

uint64_t splitmix_next(inout uint64_t state) {
    state += make_u64(0x7f4a7c15u, 0x9e3779b9u);
    uint64_t value = state;
    value = (value ^ (value >> 30u)) * make_u64(0x1ce4e5b9u, 0xbf58476du);
    value = (value ^ (value >> 27u)) * make_u64(0x133111ebu, 0x94d049bbu);
    return value ^ (value >> 31u);
}

RngState rng_initialize(uint64_t seed) {
    uint64_t state = seed;
    RngState result;
    result.s0 = splitmix_next(state);
    result.s1 = splitmix_next(state);
    result.s2 = splitmix_next(state);
    result.s3 = splitmix_next(state);
    return result;
}

uint next_int_current_shortcut_from_mantissa(uint64_t x, uint bound) {
    uint low = uint(x);
    uint high = uint(x >> 32u);
    uint64_t low_product = uint64_t(low) * uint64_t(bound);
    uint64_t upper_limb = uint64_t(high) * uint64_t(bound) + (low_product >> 32u);
    return uint(upper_limb >> 21u);
}

uint64_t rotl64(uint64_t value, uint amount) {
    return (value << amount) | (value >> (64u - amount));
}

uint64_t next_u64(inout RngState rng) {
    uint64_t result = rotl64(rng.s1 * uint64_t(5u), 7u) * uint64_t(9u);
    uint64_t temporary = rng.s1 << 17u;
    rng.s2 ^= rng.s0;
    rng.s3 ^= rng.s1;
    rng.s1 ^= rng.s2;
    rng.s0 ^= rng.s3;
    rng.s2 ^= temporary;
    rng.s3 = rotl64(rng.s3, 45u);
    return result;
}

uint next_int(inout RngState rng, uint bound) {
    return next_int_current_shortcut_from_mantissa(next_u64(rng) >> 11u, bound);
}

bool draw_neow_curse(
    uint64_t root_hash,
    out RngState event_rng,
    out uint curse_ordinal,
    out uint curse_id)
{
    uint curse_count = plan_value(39u);
    event_rng = rng_initialize(
        root_hash + plan_u64(31u));
    if (curse_count == 0u || curse_count > 10u) {
        curse_ordinal = 0xffffffffu;
        curse_id = 0xffffffffu;
        return false;
    }
    curse_ordinal = next_int(event_rng, curse_count);
    curse_id = plan_value(71u + curse_ordinal);
    return curse_id != 0xffffffffu;
}

bool positive_allowed(uint id, uint curse, uint flags) {
    bool all_character_pools_unlocked = (flags & 4u) != 0u;
    bool scroll_boxes_allowed = (flags & 8u) != 0u;
    if (id == 17u) return false; // Massive Scroll
    if (id == 14u && !all_character_pools_unlocked) return false; // Kaleidoscope
    if (id == 22u && !scroll_boxes_allowed) return false; // Scroll Boxes
    if (curse == 0u && id == 13u) return false; // Cursed Pearl / Golden Pearl
    if (curse == 2u && id == 10u) return false; // Hefty Tablet / Arcane Scroll
    if (curse == 4u && id == 19u) return false; // Leafy Poultice / New Leaf
    if (curse == 7u && id == 21u) return false; // Precarious / Precise Scissors
    if (curse == 6u && (id == 20u || id == 16u)) return false; // Sacrifice / Phial / Coffer
    return true;
}

uint64_t relic_bit(uint id) {
    return uint64_t(1u) << id;
}

bool replay_neow_top_identity_after_curse(
    inout RngState event_rng,
    uint curse_id,
    out uint route_mask)
{
    uint flags = plan_value(56u);
    uint positives[17];
    uint positive_count = 0u;
    for (uint id = 10u; id <= 23u; ++id) {
        if (positive_allowed(id, curse_id, flags)) {
            positives[positive_count++] = id;
        }
    }
    if (curse_id != 3u) { // Large Capsule suppresses the Lava/Small pair.
        positives[positive_count++] = next_int(event_rng, 2u) == 0u ? 24u : 28u;
    }
    positives[positive_count++] = next_int(event_rng, 2u) == 0u ? 26u : 29u;
    positives[positive_count++] = next_int(event_rng, 2u) == 0u ? 25u : 27u;
    if (positive_count < 2u) {
        route_mask = 0u;
        return false;
    }

    for (uint count = positive_count; count > 1u; --count) {
        uint selected = next_int(event_rng, count);
        uint tail = count - 1u;
        uint temporary = positives[selected];
        positives[selected] = positives[tail];
        positives[tail] = temporary;
    }

    uint first = positives[0u];
    uint second = positives[1u];
    uint64_t top_mask = relic_bit(first) | relic_bit(second) | relic_bit(curse_id);
    uint64_t top_any = plan_u64(40u);
    uint64_t top_all = plan_u64(42u);
    uint64_t top_ban = plan_u64(44u);
    uint64_t selected_route = plan_u64(46u);
    if (top_any != uint64_t(0u) && (top_mask & top_any) == uint64_t(0u)) {
        route_mask = 0u;
        return false;
    }
    if ((top_mask & top_all) != top_all || (top_mask & top_ban) != uint64_t(0u)) {
        route_mask = 0u;
        return false;
    }
    if (selected_route != uint64_t(0u) && (top_mask & selected_route) == uint64_t(0u)) {
        route_mask = 0u;
        return false;
    }

    bool bones_present = curse_id == 5u;
    bool require_bones = (flags & 1u) != 0u;
    bool requires_bones_projection = (flags & 2u) != 0u;
    if ((require_bones || requires_bones_projection) && !bones_present) {
        route_mask = 0u;
        return false;
    }

    if (selected_route == uint64_t(0u)) route_mask = 1u | 2u | 4u;
    else if ((selected_route & relic_bit(first)) != uint64_t(0u)) route_mask = 1u;
    else if ((selected_route & relic_bit(second)) != uint64_t(0u)) route_mask = 2u;
    else if ((selected_route & relic_bit(curse_id)) != uint64_t(0u)) route_mask = 4u;
    else route_mask = 0u;

    if (bones_present && (require_bones || selected_route == relic_bit(5u))) {
        route_mask = 8u | 16u;
    }
    return route_mask != 0u;
}

bool replay_neow_top_identity(
    uint64_t root_hash,
    out uint route_mask,
    out uint curse_id)
{
    RngState event_rng;
    uint curse_ordinal;
    if (!draw_neow_curse(root_hash, event_rng, curse_ordinal, curse_id)) {
        route_mask = 0u;
        return false;
    }
    return replay_neow_top_identity_after_curse(event_rng, curse_id, route_mask);
}

void swap_tracked_position(inout uint position, uint selected, uint tail) {
    if (position == 0xffffffffu) return;
    if (position == selected) position = tail;
    else if (position == tail) position = selected;
}

float next_float(inout RngState rng) {
    return float(double(next_u64(rng) >> 11u) * 1.1102230246251565e-16lf);
}

uint n1c_meta_base() {
    return 20u + plan_value(9u) * 6u;
}

uint n1c_meta(uint offset) {
    return other_pool_meta.values[n1c_meta_base() + offset];
}

bool final_curse_matches(uint curse_id) {
    uint required_offset = n1c_meta(6u);
    uint required_count = n1c_meta(7u);
    uint banned_offset = n1c_meta(8u);
    uint banned_count = n1c_meta(9u);
    for (uint index = 0u; index < required_count; ++index) {
        if (other_card_ids.ids[required_offset + index] != curse_id) return false;
    }
    for (uint index = 0u; index < banned_count; ++index) {
        if (other_card_ids.ids[banned_offset + index] == curse_id) return false;
    }
    return true;
}

RouteRngState initialize_route_rng_state(uint64_t root_hash, RngState rewards_after_bones) {
    RouteRngState state;
    state.rewards = rewards_after_bones;
    state.niche = rng_initialize(root_hash + plan_u64(35u));
    state.transformations = rng_initialize(root_hash + plan_u64(37u));
    state.combat_potions = rng_initialize(root_hash + plan_u64(62u));

    return state;
}

uint alphabet_byte(uint digit) {
    if (digit < 10u) return 48u + digit;
    if (digit < 18u) return 65u + (digit - 10u);
    if (digit < 23u) return 74u + (digit - 18u);
    return 80u + (digit - 23u);
}

void encode_seed12_packed(uint64_t ordinal, out uint64_t first8, out uint tail4) {
    uint d11 = uint(ordinal % uint64_t(34u)); ordinal /= uint64_t(34u);
    uint d10 = uint(ordinal % uint64_t(34u)); ordinal /= uint64_t(34u);
    uint d9  = uint(ordinal % uint64_t(34u)); ordinal /= uint64_t(34u);
    uint d8  = uint(ordinal % uint64_t(34u)); ordinal /= uint64_t(34u);
    uint d7  = uint(ordinal % uint64_t(34u)); ordinal /= uint64_t(34u);
    uint d6  = uint(ordinal % uint64_t(34u)); ordinal /= uint64_t(34u);
    uint d5  = uint(ordinal % uint64_t(34u)); ordinal /= uint64_t(34u);
    uint d4  = uint(ordinal % uint64_t(34u)); ordinal /= uint64_t(34u);
    uint d3  = uint(ordinal % uint64_t(34u)); ordinal /= uint64_t(34u);
    uint d2  = uint(ordinal % uint64_t(34u)); ordinal /= uint64_t(34u);
    uint d1  = uint(ordinal % uint64_t(34u)); ordinal /= uint64_t(34u);
    uint d0  = uint(ordinal);

    first8 = uint64_t(alphabet_byte(d0)) |
             (uint64_t(alphabet_byte(d1)) << 8u) |
             (uint64_t(alphabet_byte(d2)) << 16u) |
             (uint64_t(alphabet_byte(d3)) << 24u) |
             (uint64_t(alphabet_byte(d4)) << 32u) |
             (uint64_t(alphabet_byte(d5)) << 40u) |
             (uint64_t(alphabet_byte(d6)) << 48u) |
             (uint64_t(alphabet_byte(d7)) << 56u);
    tail4 = alphabet_byte(d8) |
            (alphabet_byte(d9) << 8u) |
            (alphabet_byte(d10) << 16u) |
            (alphabet_byte(d11) << 24u);
}

uint64_t prime5() { return make_u64(0x165667c5u, 0x27d4eb2fu); }

uint64_t prime2() { return make_u64(0x27d4eb4fu, 0xc2b2ae3du); }

uint64_t prime1() { return make_u64(0x85ebca87u, 0x9e3779b1u); }

uint64_t xxh_round(uint64_t accumulator, uint64_t input_value) {
    accumulator += input_value * prime2();
    accumulator = rotl64(accumulator, 31u);
    return accumulator * prime1();
}

uint64_t prime4() { return make_u64(0xc2b2ae63u, 0x85ebca77u); }

uint64_t prime3() { return make_u64(0x9e3779f9u, 0x165667b1u); }

uint64_t xxhash64_seed12_packed(uint64_t first8, uint tail4) {
    uint64_t hash = prime5() + uint64_t(12u);
    hash ^= xxh_round(uint64_t(0u), first8);
    hash = rotl64(hash, 27u) * prime1() + prime4();
    hash ^= uint64_t(tail4) * prime1();
    hash = rotl64(hash, 23u) * prime2() + prime3();
    hash ^= hash >> 33u;
    hash *= prime2();
    hash ^= hash >> 29u;
    hash *= prime3();
    hash ^= hash >> 32u;
    return hash;
}

// Same suffix and avalanche as xxhash64_seed12_packed. The host table supplies
// its exact first8 hash state, not an approximation or a different seed hash.
uint64_t xxhash64_seed12_from_prefix(uint64_t prefix_hash, uint tail4) {
    uint64_t hash = prefix_hash ^ (uint64_t(tail4) * prime1());
    hash = rotl64(hash, 23u) * prime2() + prime3();
    hash ^= hash >> 33u;
    hash *= prime2();
    hash ^= hash >> 29u;
    hash *= prime3();
    hash ^= hash >> 32u;
    return hash;
}

uint64_t root_hash_for_ordinal(uint64_t ordinal) {
    uint64_t first8;
    uint tail4;
    encode_seed12_packed(ordinal, first8, tail4);
    return xxhash64_seed12_packed(first8, tail4);
}

uint next_seed_ascii(uint value, out bool carry) {
    carry = false;
    if (value >= 48u && value < 57u) return value + 1u;
    if (value == 57u) return 65u;
    if (value >= 65u && value < 72u) return value + 1u;
    if (value == 72u) return 74u;
    if (value >= 74u && value < 78u) return value + 1u;
    if (value == 78u) return 80u;
    if (value >= 80u && value < 90u) return value + 1u;
    carry = true;
    return 48u;
}

bool advance_seed_byte32(inout uint packed, uint shift) {
    bool carry;
    uint next = next_seed_ascii((packed >> shift) & 0xffu, carry);
    packed = (packed & ~(0xffu << shift)) | (next << shift);
    return carry;
}

bool advance_seed_byte64(inout uint64_t packed, uint shift) {
    bool carry;
    uint next = next_seed_ascii(uint((packed >> shift) & uint64_t(0xffu)), carry);
    packed = (packed & ~(uint64_t(0xffu) << shift)) | (uint64_t(next) << shift);
    return carry;
}

void advance_seed12_packed(inout uint64_t first8, inout uint tail4) {
    if (!advance_seed_byte32(tail4, 24u)) return;
    if (!advance_seed_byte32(tail4, 16u)) return;
    if (!advance_seed_byte32(tail4, 8u)) return;
    if (!advance_seed_byte32(tail4, 0u)) return;
    if (!advance_seed_byte64(first8, 56u)) return;
    if (!advance_seed_byte64(first8, 48u)) return;
    if (!advance_seed_byte64(first8, 40u)) return;
    if (!advance_seed_byte64(first8, 32u)) return;
    if (!advance_seed_byte64(first8, 24u)) return;
    if (!advance_seed_byte64(first8, 16u)) return;
    if (!advance_seed_byte64(first8, 8u)) return;
    advance_seed_byte64(first8, 0u);
}

#if RT2_PREFIX_HASH_CACHE && N_STAGE == 0
const uint PREFIX_HASH_BLOCK_SIZE = 1336336u;
uint encode_seed4_packed(uint ordinal) {
    uint d3=ordinal%34u; ordinal/=34u;
    uint d2=ordinal%34u; ordinal/=34u;
    uint d1=ordinal%34u; ordinal/=34u;
    return alphabet_byte(ordinal) | (alphabet_byte(d1)<<8u) |
           (alphabet_byte(d2)<<16u) | (alphabet_byte(d3)<<24u);
}
uint64_t prefix_hash_cache_load(uint row) {
    uint at=4u+row*4u;
    return make_u64(prefix_hash_cache_value(at),prefix_hash_cache_value(at+1u));
}
bool prefix_hash_cache_initialize(uint first, out uint row, out uint suffix_ordinal,
    out uint tail4, out uint64_t prefix_hash) {
    // The host limits each workspace to 2^24 roots: this sum fits uint. One
    // 32-bit constant division per eight roots replaces full 64-bit decoding.
    uint position=prefix_hash_cache_value(0u)+first;
    row=position/PREFIX_HASH_BLOCK_SIZE;
    suffix_ordinal=position-row*PREFIX_HASH_BLOCK_SIZE;
    tail4=encode_seed4_packed(suffix_ordinal);
    prefix_hash=uint64_t(0u);
    if(row>=prefix_hash_cache_value(1u))return false;
    prefix_hash=prefix_hash_cache_load(row);
    return true;
}
bool prefix_hash_cache_advance(inout uint row, inout uint suffix_ordinal,
    inout uint tail4, inout uint64_t prefix_hash) {
    suffix_ordinal++;
    if(suffix_ordinal==PREFIX_HASH_BLOCK_SIZE) {
        suffix_ordinal=0u; row++;
        if(row>=prefix_hash_cache_value(1u))return false;
        prefix_hash=prefix_hash_cache_load(row);
        tail4=0x30303030u;
        return true;
    }
    if(!advance_seed_byte32(tail4,24u))return true;
    if(!advance_seed_byte32(tail4,16u))return true;
    if(!advance_seed_byte32(tail4,8u))return true;
    advance_seed_byte32(tail4,0u);
    return true;
}
#endif

uint64_t identity_permutation(uint length) {
    uint64_t packed = uint64_t(0u);
    for (uint index = 0u; index < length; ++index) {
        packed |= uint64_t(index) << (index * 4u);
    }
    return packed;
}

void permutation_swap(inout uint64_t packed, uint left, uint right) {
    uint left_shift = left * 4u;
    uint right_shift = right * 4u;
    uint64_t left_value = (packed >> left_shift) & uint64_t(0x0fu);
    uint64_t right_value = (packed >> right_shift) & uint64_t(0x0fu);
    uint64_t clear_mask = ~((uint64_t(0x0fu) << left_shift) | (uint64_t(0x0fu) << right_shift));
    packed = (packed & clear_mask) | (left_value << right_shift) | (right_value << left_shift);
}

uint64_t shuffled_pool_order(uint pool_count, inout RngState niche) {
    uint64_t packed = identity_permutation(pool_count);
    for (uint count = pool_count; count > 1u; --count) {
        uint selected = next_int(niche, count);
        permutation_swap(packed, selected, count - 1u);
    }
    return packed;
}

void rarity_slice(uint pool_index, uint requested, uint fallback_index, out uint offset, out uint length) {
    uint rarity;
    if (requested == 1u) rarity = fallback_index == 0u ? 1u : (fallback_index == 1u ? 2u : 3u);
    else if (requested == 2u) rarity = fallback_index == 0u ? 2u : (fallback_index == 1u ? 3u : 1u);
    else rarity = fallback_index == 0u ? 3u : (fallback_index == 1u ? 1u : 2u);
    uint base = 20u + pool_index * 6u + (rarity - 1u) * 2u;
    offset = other_pool_meta.values[base];
    length = other_pool_meta.values[base + 1u];
}

#if N_DIRECT_NESTED == 2
uint roll_card_selected_rarity(uint pool_index, uint requested, inout RngState rewards) {
#else
uint roll_card(uint pool_index, uint ascension, inout RngState rewards) {
    float rare = ascension >= 7u ? 0.0149f : 0.03f;
    float roll = next_float(rewards);
    uint requested = roll < rare ? 3u : (roll < rare + 0.37f ? 2u : 1u);
#endif
    for (uint fallback_index = 0u; fallback_index < 3u; ++fallback_index) {
        uint offset;
        uint length;
        rarity_slice(pool_index, requested, fallback_index, offset, length);
        if (length == 0u) continue;
        uint card_id = other_card_ids.ids[offset + next_int(rewards, length)];
        next_float(rewards); // Game source consumes Rewards.NextFloat for the upgrade roll.
        return card_id;
    }
    return 0xffffffffu;
}

#if N_DIRECT_NESTED == 2
uint roll_card(uint pool_index, uint ascension, inout RngState rewards) {
    float rare = ascension >= 7u ? 0.0149f : 0.03f;
    float roll = next_float(rewards);
    uint requested = roll < rare ? 3u : (roll < rare + 0.37f ? 2u : 1u);
    return roll_card_selected_rarity(pool_index,requested,rewards);
}
#endif

#if (N_DIRECT_NESTED >= 1 && N_DIRECT_NESTED <= 3) || N_DIRECT_NESTED == 11 || N_DIRECT_NESTED == 12 || N_DIRECT_NESTED == 101
bool kaleidoscope_rare_prefix(inout RngState rewards, out uint slot) {
    float rare=plan_value(7u)>=7u ? 0.0149f : 0.03f;
    for(slot=0u;slot<3u;slot++) {
        if(next_float(rewards)<rare) return true;
        if(slot<2u) { next_u64(rewards); next_u64(rewards); }
    }
    return false;
}
#endif

uint permutation_value(uint64_t packed, uint index) {
    return uint((packed >> (index * 4u)) & uint64_t(0x0fu));
}

#if N_DIRECT_NESTED == 2
bool execute_kaleidoscope_body(
#else
bool execute_kaleidoscope_route(
#endif
    uint target_count,
    uint target0,
    uint target1,
    bool predicate_enabled,
    inout RouteRngState state
#if N_DIRECT_NESTED == 2
    , uint first_slot, bool rarity_ready
#endif
    )
{
    uint pool_count = plan_value(9u);
    uint ascension = plan_value(7u);
    bool group0_target0 = false;
    bool group0_target1 = false;
    bool group1_target0 = false;
    bool group1_target1 = false;
    for (uint group = 0u; group < 2u; ++group) {
        uint64_t order = shuffled_pool_order(pool_count, state.niche);
#if N_DIRECT_NESTED == 2
        for (uint item = group == 0u ? first_slot : 0u; item < 3u; ++item) {
            uint pool=permutation_value(order,item);
            uint card = rarity_ready && group==0u && item==first_slot
                ? roll_card_selected_rarity(pool,3u,state.rewards) : roll_card(pool, ascension, state.rewards);
#else
        for (uint item = 0u; item < 3u; ++item) {
            uint card = roll_card(permutation_value(order,item),ascension,state.rewards);
#endif
            bool is0 = card == target0;
            bool is1 = target_count > 1u && card == target1;
            if (group == 0u) {
                group0_target0 = group0_target0 || is0;
                group0_target1 = group0_target1 || is1;
            } else {
                group1_target0 = group1_target0 || is0;
                group1_target1 = group1_target1 || is1;
            }
        }
        if(group==0u && predicate_enabled && target_count==2u && !group0_target0 && !group0_target1) return false;
    }
    if (!predicate_enabled) return true;
    if (target_count == 1u) return group0_target0 || group1_target0;
    return (group0_target0 && group1_target1) || (group0_target1 && group1_target0);
}

bool execute_kaleidoscope_ordered_route(uint first, uint second, inout RouteRngState state) {
    for (uint group = 0u; group < 2u; ++group) {
        uint target = group == 0u ? first : second;
        bool found = target == 0xffffffffu;
        uint64_t order = shuffled_pool_order(plan_value(9u), state.niche);
        for (uint item = 0u; item < 3u; ++item) {
            uint card = roll_card(permutation_value(order, item), plan_value(7u), state.rewards);
            found = found || card == target;
        }
        if (!found) return false;
    }
    return true;
}

#if N_DIRECT_NESTED == 2
bool execute_kaleidoscope_route(uint count,uint t0,uint t1,bool predicate,inout RouteRngState state) {
    return execute_kaleidoscope_body(count,t0,t1,predicate,state,0u,false);
}
#endif

bool execute_leafy_route(
    uint target_count,
    uint target0,
    uint target1,
    bool predicate_enabled,
    inout RouteRngState state)
{
    uint strike_length = plan_value(58u);
    uint defend_length = plan_value(59u);
    if (strike_length == 0u || defend_length == 0u) return false;
    uint first = leafy_strike.ids[next_int(state.transformations, strike_length)];
    uint second = leafy_defend.ids[next_int(state.transformations, defend_length)];
    if (!predicate_enabled) return true;
    if (target_count == 1u) return first == target0 || second == target0;
    return (first == target0 && second == target1) ||
           (first == target1 && second == target0);
}

bool used_value(uint candidate, uint used0, uint used1, uint used2, uint used_count) {
    if (used_count > 0u && candidate == used0) return true;
    if (used_count > 1u && candidate == used1) return true;
    return used_count > 2u && candidate == used2;
}

uint select_unused_meta(
    uint meta_index,
    uint used0,
    uint used1,
    uint used2,
    uint used_count,
    inout RngState rng)
{
    uint offset = other_pool_meta.values[meta_index];
    uint length = other_pool_meta.values[meta_index + 1u];
    if (used_count == 0u) return length == 0u ? 0xffffffffu : other_card_ids.ids[offset + next_int(rng,length)];
    uint available = 0u;
    for (uint index = 0u; index < length; ++index) {
        uint candidate = other_card_ids.ids[offset + index];
        if (!used_value(candidate, used0, used1, used2, used_count)) available++;
    }
    if (available == 0u) return 0xffffffffu;
    uint selected = next_int(rng, available);
    for (uint index = 0u; index < length; ++index) {
        uint candidate = other_card_ids.ids[offset + index];
        if (used_value(candidate, used0, used1, used2, used_count)) continue;
        if (selected == 0u) return candidate;
        selected--;
    }
    return 0xffffffffu;
}

bool execute_arcane_scroll_route(uint target, bool predicate_enabled, inout RouteRngState state) {
    uint selected = select_unused_meta(
        4u, 0xffffffffu, 0xffffffffu, 0xffffffffu, 0u, state.rewards);
    return selected != 0xffffffffu && (!predicate_enabled || selected == target);
}

bool execute_hefty_tablet_route(uint target, bool predicate_enabled, inout RouteRngState state) {
    uint first = select_unused_meta(
        4u, 0xffffffffu, 0xffffffffu, 0xffffffffu, 0u, state.rewards);
    if (first == 0xffffffffu) return false;
    uint second = select_unused_meta(
        4u, first, 0xffffffffu, 0xffffffffu, 1u, state.rewards);
    if (second == 0xffffffffu) return false;
    uint third = select_unused_meta(
        4u, first, second, 0xffffffffu, 2u, state.rewards);
    if (third == 0xffffffffu) return false;
    return !predicate_enabled || first == target || second == target || third == target;
}

uint card_rarity_meta_index(uint pool_meta_base, uint requested, uint fallback_index) {
    uint rarity;
    if (requested == 1u) rarity = fallback_index == 0u ? 1u : (fallback_index == 1u ? 2u : 3u);
    else if (requested == 2u) rarity = fallback_index == 0u ? 2u : (fallback_index == 1u ? 3u : 1u);
    else rarity = fallback_index == 0u ? 3u : (fallback_index == 1u ? 1u : 2u);
    return pool_meta_base + (rarity - 1u) * 2u;
}

#if N_DIRECT_NESTED == 12
uint roll_card_from_meta_selected(uint requested,
#else
uint roll_card_from_meta(
#endif
    uint pool_meta_base,
    uint ascension,
    uint used0,
    uint used1,
    uint used2,
    uint used_count,
    inout RngState rewards)
{
#if N_DIRECT_NESTED != 12
    float rare = ascension >= 7u ? 0.0149f : 0.03f;
    float roll = next_float(rewards);
    uint requested = roll < rare ? 3u : (roll < rare + 0.37f ? 2u : 1u);
#endif
    for (uint fallback_index = 0u; fallback_index < 3u; ++fallback_index) {
        uint selected = select_unused_meta(
            card_rarity_meta_index(pool_meta_base, requested, fallback_index),
            used0, used1, used2, used_count, rewards);
        if (selected == 0xffffffffu) continue;
        next_float(rewards);
        return selected;
    }
    return 0xffffffffu;
}

#if N_DIRECT_NESTED == 12
uint roll_card_from_meta(uint base, uint ascension, uint u0, uint u1, uint u2, uint count, inout RngState rewards) {
    float rare=ascension>=7u ? 0.0149f : 0.03f;
    float roll=next_float(rewards);
    return roll_card_from_meta_selected(roll<rare ? 3u : (roll<rare+0.37f ? 2u : 1u),base,ascension,u0,u1,u2,count,rewards);
}
#endif

#if N_DIRECT_NESTED == 20 || N_DIRECT_NESTED == 21
bool lead_rare_opportunity(RngState rewards) {
    float rare=plan_value(7u)>=7u ? 0.0149f : 0.03f;
    if(next_float(rewards)<rare) return true;
    next_u64(rewards); next_u64(rewards);
    return next_float(rewards)<rare;
}
#endif

#if N_DIRECT_NESTED == 40 || N_DIRECT_NESTED == 41
bool phial_rare_opportunity(RngState potions, uint count) {
    bool first=next_float(potions)<=0.1f;
    if(count==1u && first) return true;
    if(count==2u && !first) return false;
    next_u64(potions);
    return next_float(potions)<=0.1f;
}
#endif

bool execute_lead_paperweight_route(uint target, bool predicate_enabled, inout RouteRngState state) {
    uint ascension = plan_value(7u);
    uint first = roll_card_from_meta(
        6u, ascension, 0xffffffffu, 0xffffffffu, 0xffffffffu, 0u, state.rewards);
    if (first == 0xffffffffu) return false;
    uint second = roll_card_from_meta(
        6u, ascension, first, 0xffffffffu, 0xffffffffu, 1u, state.rewards);
    return second != 0xffffffffu && (!predicate_enabled || first == target || second == target);
}

uint roll_potion_from_meta(
    uint used0,
    uint used_count,
    inout RngState rng)
{
    float roll = next_float(rng);
    uint requested_meta = roll <= 0.1f ? 16u : (roll <= 0.35f ? 14u : 12u);
    return select_unused_meta(
        requested_meta, used0, 0xffffffffu, 0xffffffffu, used_count, rng);
}

#if N_DIRECT_NESTED == 12
bool execute_lost_coffer_body(
#else
bool execute_lost_coffer_route(
#endif
    uint predicate_mask,
    uint card_target,
    uint potion_target,
    bool predicate_enabled,
    inout RouteRngState state
#if N_DIRECT_NESTED == 12
    , uint first_slot, bool rarity_ready
#endif
    )
{
    uint ascension = plan_value(7u);
#if N_DIRECT_NESTED >= 10 && N_DIRECT_NESTED <= 13
    if(predicate_mask==2u) {
        for(uint i=0u;i<9u;i++) next_u64(state.rewards);
        return roll_potion_from_meta(0xffffffffu,0u,state.rewards)==potion_target;
    }
#endif
#if N_DIRECT_NESTED == 12
    // Skipped cards are non-Rare; their identities cannot exclude a later Rare.
    // Their omitted selections still had their RNG calls consumed by the prefix.
    uint first=0xffffffffu, second=0xffffffffu, third=0xffffffffu;
    for(uint i=first_slot;i<3u;i++) {
        uint card=rarity_ready && i==first_slot
            ? roll_card_from_meta_selected(3u,0u,ascension,first,second,0xffffffffu,i,state.rewards)
            : roll_card_from_meta(0u,ascension,first,second,0xffffffffu,i,state.rewards);
        if(card==0xffffffffu) return false;
        if(i==0u) first=card; else if(i==1u) second=card; else third=card;
    }
#else
    uint first = roll_card_from_meta(
        0u, ascension, 0xffffffffu, 0xffffffffu, 0xffffffffu, 0u, state.rewards);
    if (first == 0xffffffffu) return false;
    uint second = roll_card_from_meta(
        0u, ascension, first, 0xffffffffu, 0xffffffffu, 1u, state.rewards);
    if (second == 0xffffffffu) return false;
    uint third = roll_card_from_meta(
        0u, ascension, first, second, 0xffffffffu, 2u, state.rewards);
    if (third == 0xffffffffu) return false;
#endif
#if N_DIRECT_NESTED >= 10 && N_DIRECT_NESTED <= 13
    if(first!=card_target && second!=card_target && third!=card_target) return false;
    if(predicate_mask==1u) return true;
#endif
    uint potion = roll_potion_from_meta(0xffffffffu, 0u, state.rewards);
    if (potion == 0xffffffffu) return false;
    if (!predicate_enabled) return true;
    bool card_pass = (predicate_mask & 1u) == 0u ||
                     first == card_target || second == card_target || third == card_target;
    bool potion_pass = (predicate_mask & 2u) == 0u || potion == potion_target;
    return card_pass && potion_pass;
}

#if N_DIRECT_NESTED == 12
bool execute_lost_coffer_route(uint mask,uint card,uint potion,bool enabled,inout RouteRngState state) {
    return execute_lost_coffer_body(mask,card,potion,enabled,state,0u,false);
}
#endif

bool execute_phial_holster_route(
    uint target_count,
    uint target0,
    uint target1,
    bool predicate_enabled,
    inout RouteRngState state)
{
    uint first = roll_potion_from_meta(0xffffffffu, 0u, state.combat_potions);
    if (first == 0xffffffffu) return false;
    uint second = roll_potion_from_meta(first, 1u, state.combat_potions);
    if (second == 0xffffffffu) return false;
    if (!predicate_enabled) return true;
    if (target_count == 1u) return first == target0 || second == target0;
    return (first == target0 && second == target1) ||
           (first == target1 && second == target0);
}

bool execute_new_leaf_route(uint target, bool predicate_enabled, inout RouteRngState state) {
    uint offset = plan_value(60u);
    uint length = plan_value(61u);
    if (length == 0u) return false;
    uint selected = leafy_defend.ids[offset + next_int(state.niche, length)];
    return !predicate_enabled || selected == target;
}

bool scroll_used_value(
    uint candidate,
    uint used0, uint used1, uint used2, uint used3, uint used4, uint used5,
    uint used_count)
{
    if (used_count > 0u && candidate == used0) return true;
    if (used_count > 1u && candidate == used1) return true;
    if (used_count > 2u && candidate == used2) return true;
    if (used_count > 3u && candidate == used3) return true;
    if (used_count > 4u && candidate == used4) return true;
    return used_count > 5u && candidate == used5;
}

uint select_unused_scroll_meta(
    uint meta_index,
    uint used0, uint used1, uint used2, uint used3, uint used4, uint used5,
    uint used_count,
    inout RngState rewards)
{
    uint offset = other_pool_meta.values[meta_index];
    uint length = other_pool_meta.values[meta_index + 1u];
    uint available = 0u;
    for (uint index = 0u; index < length; ++index) {
        uint candidate = other_card_ids.ids[offset + index];
        if (!scroll_used_value(candidate, used0, used1, used2, used3, used4, used5, used_count)) available++;
    }
    if (available == 0u) return 0xffffffffu;
    uint selected = next_int(rewards, available);
    for (uint index = 0u; index < length; ++index) {
        uint candidate = other_card_ids.ids[offset + index];
        if (scroll_used_value(candidate, used0, used1, used2, used3, used4, used5, used_count)) continue;
        if (selected == 0u) return candidate;
        selected--;
    }
    return 0xffffffffu;
}

bool scroll_bundle_matches(
    uint actual0,
    uint actual1,
    uint actual2,
    uint target_count,
    uint target0,
    uint target1,
    uint target2)
{
    bool used0 = false; bool used1 = false; bool used2 = false;
    for (uint index = 0u; index < target_count; ++index) {
        uint target = index == 0u ? target0 : (index == 1u ? target1 : target2);
        if (!used0 && actual0 == target) used0 = true;
        else if (!used1 && actual1 == target) used1 = true;
        else if (!used2 && actual2 == target) used2 = true;
        else return false;
    }
    return true;
}

#if N_DIRECT_NESTED == 7 || N_DIRECT_NESTED == 8
bool scroll_special_only(inout RngState rewards) {
    if(next_int(rewards,100u)<1u) return true;
    // The first ordinary bundle consumes exactly three successful selection calls.
    next_u64(rewards); next_u64(rewards); next_u64(rewards);
    return next_int(rewards,100u)<1u;
}
#endif

bool execute_scroll_boxes_route(
    uint target_count,
    uint target0,
    uint target1,
    uint target2,
    uint predicate_flags,
    bool predicate_enabled,
    inout RouteRngState state)
{
#if N_DIRECT_NESTED == 8
    return scroll_special_only(state.rewards);
#endif
    uint used0 = 0xffffffffu; uint used1 = 0xffffffffu; uint used2 = 0xffffffffu;
    uint used3 = 0xffffffffu; uint used4 = 0xffffffffu; uint used5 = 0xffffffffu;
    uint used_count = 0u;
    bool card_pass = (predicate_flags & 1u) == 0u;
    bool claw_pass = (predicate_flags & 2u) == 0u;
    bool uses_defect_rule = (plan_value(56u) & 32u) != 0u;
    for (uint bundle = 0u; bundle < 2u; ++bundle) {
        bool claw = uses_defect_rule && next_int(state.rewards, 100u) < 1u;
        if (claw) {
            claw_pass = true;
            continue;
        }
        uint first = select_unused_scroll_meta(
            0u, used0, used1, used2, used3, used4, used5, used_count, state.rewards);
        if (first == 0xffffffffu) return false;
        if (used_count == 0u) used0 = first; else if (used_count == 1u) used1 = first;
        else if (used_count == 2u) used2 = first; else if (used_count == 3u) used3 = first;
        else if (used_count == 4u) used4 = first; else used5 = first;
        used_count++;
        uint second = select_unused_scroll_meta(
            0u, used0, used1, used2, used3, used4, used5, used_count, state.rewards);
        if (second == 0xffffffffu) return false;
        if (used_count == 0u) used0 = second; else if (used_count == 1u) used1 = second;
        else if (used_count == 2u) used2 = second; else if (used_count == 3u) used3 = second;
        else if (used_count == 4u) used4 = second; else used5 = second;
        used_count++;
        uint uncommon = select_unused_scroll_meta(
            2u, used0, used1, used2, used3, used4, used5, used_count, state.rewards);
        if (uncommon == 0xffffffffu) return false;
        if (used_count == 0u) used0 = uncommon; else if (used_count == 1u) used1 = uncommon;
        else if (used_count == 2u) used2 = uncommon; else if (used_count == 3u) used3 = uncommon;
        else if (used_count == 4u) used4 = uncommon; else used5 = uncommon;
        used_count++;
        if ((predicate_flags & 1u) != 0u &&
            scroll_bundle_matches(first, second, uncommon, target_count, target0, target1, target2)) {
            card_pass = true;
        }
    }
    return !predicate_enabled || (card_pass && claw_pass);
}
