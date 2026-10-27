#version 450
#extension GL_EXT_shader_explicit_arithmetic_types_int64 : require

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

const uint INVALID_ID = 65535u;
const uint SEEDS_PER_INVOCATION = 8u;
const uint CANDIDATES_PER_WORKGROUP = 64u * SEEDS_PER_INVOCATION;

// [0..1] batch base, [2] batch candidate count, [3] input count,
// [4] input mode (0 dense, 1 compact ABI1), [5] output capacity.
layout(set = 0, binding = 0, std430) readonly restrict buffer BatchMetaBuffer { uint values[]; } batch_meta;
layout(set = 0, binding = 1, std430) readonly restrict buffer CompactInputBuffer { uint values[]; } compact_input;
layout(set = 0, binding = 2, std430) readonly restrict buffer MerchantPlanBuffer { uint values[]; } merchant_plan_buffer;
layout(set = 0, binding = 3, std430) readonly restrict buffer MerchantTargetBuffer { uint ids[]; } merchant_target_buffer;
// [0] magic, [1] ABI, [2] output count, [3] overflow, [4] processed, [5] plan tag.
layout(set = 0, binding = 4, std430) restrict buffer OutputHeaderBuffer { uint values[]; } output_header;
layout(set = 0, binding = 5, std430) restrict buffer CompactOutputBuffer { uint values[]; } compact_output;
#ifdef RT2_S_STABLE_ORDERED_COMPACTION
// One uint carries the eight keep bits owned by one Carry8 invocation.
layout(set = 0, binding = 6, std430) restrict buffer KeepMaskBuffer { uint values[]; } keep_masks;
// One count covers the 64 Carry8 invocations / 512 candidates in a workgroup.
layout(set = 0, binding = 7, std430) restrict buffer BlockCountBuffer { uint values[]; } block_counts;
shared uint stable_invocation_counts[64];
#endif

uint64_t s0;
uint64_t s1;
uint64_t s2;
uint64_t s3;

uint64_t make_u64(uint low, uint high) { return uint64_t(low) | (uint64_t(high) << 32u); }
uint64_t rotl64(uint64_t value, uint amount) { return (value << amount) | (value >> (64u - amount)); }
uint64_t splitmix_next(inout uint64_t state) {
    state += make_u64(0x7f4a7c15u, 0x9e3779b9u);
    uint64_t value = state;
    value = (value ^ (value >> 30u)) * make_u64(0x1ce4e5b9u, 0xbf58476du);
    value = (value ^ (value >> 27u)) * make_u64(0x133111ebu, 0x94d049bbu);
    return value ^ (value >> 31u);
}
void rng_initialize(uint64_t seed) {
    uint64_t state = seed;
    s0 = splitmix_next(state); s1 = splitmix_next(state); s2 = splitmix_next(state); s3 = splitmix_next(state);
}
uint64_t next_u64() {
    uint64_t result = rotl64(s1 * uint64_t(5u), 7u) * uint64_t(9u);
    uint64_t temporary = s1 << 17u;
    s2 ^= s0; s3 ^= s1; s1 ^= s2; s0 ^= s3; s2 ^= temporary; s3 = rotl64(s3, 45u);
    return result;
}
void consume_rng() {
    uint64_t temporary = s1 << 17u;
    s2 ^= s0; s3 ^= s1; s1 ^= s2; s0 ^= s3; s2 ^= temporary; s3 = rotl64(s3, 45u);
}
uint next_int(uint bound) {
    // Owner-approved Family Fast primitive: floor((NextUInt64 >> 11) * bound / 2^53).
    // Production Exact remains responsible for the game's binary64 round-to-nearest behavior.
    uint64_t mantissa = next_u64() >> 11u;
    uint mantissa_low = uint(mantissa);
    uint mantissa_high = uint(mantissa >> 32u);
    uint64_t low_product = uint64_t(mantissa_low) * uint64_t(bound);
    uint64_t upper_limb = uint64_t(mantissa_high) * uint64_t(bound) + (low_product >> 32u);
    return uint(upper_limb >> 21u);
}

/*__RT2_VISIBLE_SEED_ROOT_HASH__*/

bool evaluate_merchant_colorless(uint64_t root) {
    uint slot_count = merchant_plan_buffer.values[0u];
    uint sequence_count = merchant_plan_buffer.values[1u];
    if (slot_count == 0u && sequence_count == 0u) return true;
    uint uncommon_pool_count = merchant_plan_buffer.values[2u];
    uint rare_pool_count = merchant_plan_buffer.values[3u];
    uint player_slot = merchant_plan_buffer.values[4u];
    uint64_t shops_hash = make_u64(merchant_plan_buffer.values[5u], merchant_plan_buffer.values[6u]);
    if (uncommon_pool_count == 0u || rare_pool_count == 0u) return true;
    rng_initialize(root + uint64_t(player_slot) + shops_hash);
    uint uncommon[5];
    uint rare[5];
    uint max_ordinal = 0u;
    for (uint index = 0u; index < slot_count; ++index)
        max_ordinal = max(max_ordinal, merchant_plan_buffer.values[7u + index * 3u]);
    uint sequence_base = 7u + slot_count * 3u;
    for (uint index = 0u; index < sequence_count; ++index)
        max_ordinal = max(max_ordinal, merchant_plan_buffer.values[sequence_base + index * 5u + 1u]);
    for (uint ordinal = 1u; ordinal <= max_ordinal; ++ordinal) {
        for (uint call = 0u; call < 12u; ++call) consume_rng();
        uncommon[ordinal - 1u] = next_int(uncommon_pool_count);
        consume_rng();
        rare[ordinal - 1u] = next_int(rare_pool_count);
        for (uint index = 0u; index < slot_count; ++index) {
            uint base = 7u + index * 3u;
            if (merchant_plan_buffer.values[base] != ordinal) continue;
            uint actual = merchant_plan_buffer.values[base + 1u] == 0u ? uncommon[ordinal - 1u] : rare[ordinal - 1u];
            if (actual != merchant_plan_buffer.values[base + 2u]) return false;
        }
        for (uint index = 0u; index < sequence_count; ++index) {
            uint base = sequence_base + index * 5u;
            uint count = merchant_plan_buffer.values[base + 1u];
            if (count != ordinal) continue;
            uint target_offset = merchant_plan_buffer.values[base + 3u];
            uint target_count = merchant_plan_buffer.values[base + 4u];
            bool ordered = merchant_plan_buffer.values[base + 2u] == 0u;
            if (ordered) {
                for (uint target_index = 0u; target_index < target_count; ++target_index) {
                    uint target = merchant_target_buffer.ids[target_offset + target_index];
                    if (target == INVALID_ID) continue;
                    if (target_index >= count) return false;
                    uint actual = merchant_plan_buffer.values[base] == 0u ? uncommon[target_index] : rare[target_index];
                    if (actual != target) return false;
                }
            } else {
                for (uint target_index = 0u; target_index < target_count; ++target_index) {
                    uint target = merchant_target_buffer.ids[target_offset + target_index];
                    if (target == INVALID_ID) continue;
                    uint requested = 0u;
                    uint actual = 0u;
                    for (uint probe = 0u; probe < target_count; ++probe)
                        if (merchant_target_buffer.ids[target_offset + probe] == target) requested++;
                    for (uint probe = 0u; probe < count; ++probe)
                        if ((merchant_plan_buffer.values[base] == 0u ? uncommon[probe] : rare[probe]) == target) actual++;
                    if (actual < requested) return false;
                }
            }
        }
        // S exports only candidate ordinals, never the terminal Shops state.
        // Reject before the unused price/relic/potion tail; a later merchant
        // still starts after the same complete 28-call continuation.
        if (ordinal < max_ordinal)
            for (uint call = 0u; call < 13u; ++call) consume_rng();
    }
    return true;
}

void append_output(uint64_t logical_ordinal) {
    uint slot = atomicAdd(output_header.values[2u], 1u);
    if (slot >= batch_meta.values[5u]) {
        atomicExchange(output_header.values[3u], 1u);
        return;
    }
    compact_output.values[slot * 2u] = uint(logical_ordinal);
    compact_output.values[slot * 2u + 1u] = uint(logical_ordinal >> 32u);
}

void process_candidate(uint input_index) {
    uint input_count = batch_meta.values[3u];
    if (input_index >= input_count) return;

    uint64_t logical_ordinal = uint64_t(input_index);
    if (batch_meta.values[4u] != 0u) {
        logical_ordinal = make_u64(compact_input.values[input_index * 2u], compact_input.values[input_index * 2u + 1u]);
    }
    if (logical_ordinal >= uint64_t(batch_meta.values[2u])) return;
    uint64_t global_ordinal = make_u64(batch_meta.values[0u], batch_meta.values[1u]) + logical_ordinal;
    if (evaluate_merchant_colorless(root_hash_for_ordinal(global_ordinal))) append_output(logical_ordinal);
}

void record_processed_workgroup() {
    if (gl_LocalInvocationIndex != 0u) return;
    uint first_input = gl_WorkGroupID.x * CANDIDATES_PER_WORKGROUP;
    uint input_count = batch_meta.values[3u];
    if (first_input >= input_count) return;
    uint admitted = min(CANDIDATES_PER_WORKGROUP, input_count - first_input);
    atomicAdd(output_header.values[4u], admitted);
}

void main() {
    record_processed_workgroup();
    uint first_input = gl_GlobalInvocationID.x * SEEDS_PER_INVOCATION;
#ifdef RT2_S_STABLE_ORDERED_COMPACTION
    uint keep_mask = 0u;
    if (first_input < batch_meta.values[3u] && batch_meta.values[4u] == 0u) {
        uint64_t global_ordinal = make_u64(batch_meta.values[0u], batch_meta.values[1u]) + uint64_t(first_input);
        uint64_t seed_first8;
        uint seed_tail4;
        encode_seed12_packed(global_ordinal, seed_first8, seed_tail4);
        for (uint offset = 0u; offset < SEEDS_PER_INVOCATION; ++offset) {
            uint input_index = first_input + offset;
            if (input_index >= batch_meta.values[3u] || uint64_t(input_index) >= uint64_t(batch_meta.values[2u])) break;
            if (evaluate_merchant_colorless(root_hash_for_seed12_packed(seed_first8, seed_tail4)))
                keep_mask |= 1u << offset;
            if (offset + 1u < SEEDS_PER_INVOCATION) advance_seed12_packed(seed_first8, seed_tail4);
        }
    }

    keep_masks.values[gl_GlobalInvocationID.x] = keep_mask;
    stable_invocation_counts[gl_LocalInvocationIndex] = uint(bitCount(keep_mask));
    barrier();
    if (gl_LocalInvocationIndex == 0u) {
        uint block_count = 0u;
        for (uint invocation = 0u; invocation < 64u; ++invocation)
            block_count += stable_invocation_counts[invocation];
        block_counts.values[gl_WorkGroupID.x] = block_count;
        atomicAdd(output_header.values[2u], block_count);
    }
    return;
#endif
    if (first_input >= batch_meta.values[3u]) return;
    if (batch_meta.values[4u] == 0u) {
        uint64_t global_ordinal = make_u64(batch_meta.values[0u], batch_meta.values[1u]) + uint64_t(first_input);
        uint64_t seed_first8;
        uint seed_tail4;
        encode_seed12_packed(global_ordinal, seed_first8, seed_tail4);
        for (uint offset = 0u; offset < SEEDS_PER_INVOCATION; ++offset) {
            uint input_index = first_input + offset;
            if (input_index >= batch_meta.values[3u] || uint64_t(input_index) >= uint64_t(batch_meta.values[2u])) break;
            if (evaluate_merchant_colorless(root_hash_for_seed12_packed(seed_first8, seed_tail4)))
                append_output(uint64_t(input_index));
            if (offset + 1u < SEEDS_PER_INVOCATION) advance_seed12_packed(seed_first8, seed_tail4);
        }
        return;
    }
    for (uint offset = 0u; offset < SEEDS_PER_INVOCATION; ++offset) {
        uint input_index = first_input + offset;
        if (input_index >= batch_meta.values[3u]) break;
        process_candidate(input_index);
    }
}
