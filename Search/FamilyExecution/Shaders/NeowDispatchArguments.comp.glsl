#version 450
// Each Neow invocation processes eight roots, in groups of 64 invocations.
// Generate both commands after the preceding stage's storage-buffer barrier.
layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;
layout(set=0,binding=0,std430) readonly restrict buffer Batch { uint values[]; } batch;
layout(set=0,binding=1,std430) readonly restrict buffer Header { uint values[]; } header;
layout(set=0,binding=2,std430) writeonly restrict buffer Arguments { uint values[]; } arguments;

void main() {
    for (uint stage = 0u; stage < 2u; stage++) {
        // Keep error and overflow evidence intact for the normal host validator.
        // An invalid producer schedules no consumers and cannot read beyond its
        // original allocation. No probability estimate participates in sizing.
        uint count = header.values[3u] == 0u ? min(header.values[6u+stage],batch.values[6u+stage]) : 0u;
        uint offset = stage * 3u;
        arguments.values[offset] = (count + 511u) / 512u;
        arguments.values[offset+1u] = 1u;
        arguments.values[offset+2u] = 1u;
    }
}
