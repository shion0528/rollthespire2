// Host ABI: [initial suffix, rows, batch base lo, hi], then rows of
// [hash state lo, hi, first8 lo, hi]. No RNG state or query result is cached.
layout(set=0,binding=__RT2_PREFIX_BINDING__,std430) readonly restrict buffer Rt2VisiblePrefix { uint v[]; } rt2_visible_prefix;
uint64_t rt2_prefix_u64(uint lo,uint hi){return uint64_t(lo)|(uint64_t(hi)<<32u);}
uint64_t rt2_prefix_rotl(uint64_t v,uint n){return (v<<n)|(v>>(64u-n));}
uint rt2_prefix_ascii(uint digit){return digit<10u?48u+digit:digit<18u?55u+digit:digit<23u?56u+digit:57u+digit;}
uint rt2_prefix_tail(uint suffix){
 uint d3=suffix%34u;suffix/=34u;uint d2=suffix%34u;suffix/=34u;uint d1=suffix%34u;suffix/=34u;
 return rt2_prefix_ascii(suffix)|(rt2_prefix_ascii(d1)<<8u)|(rt2_prefix_ascii(d2)<<16u)|(rt2_prefix_ascii(d3)<<24u);
}
uint64_t rt2_prefix_row(uint row){uint at=4u+row*4u;return rt2_prefix_u64(rt2_visible_prefix.v[at],rt2_visible_prefix.v[at+1u]);}
uint64_t rt2_prefix_root(uint64_t prefix,uint tail){
 uint64_t p1=rt2_prefix_u64(0x85ebca87u,0x9e3779b1u),p2=rt2_prefix_u64(0x27d4eb4fu,0xc2b2ae3du),p3=rt2_prefix_u64(0x9e3779f9u,0x165667b1u);
 uint64_t h=prefix^(uint64_t(tail)*p1);h=rt2_prefix_rotl(h,23u)*p2+p3;
 h^=h>>33u;h*=p2;h^=h>>29u;h*=p3;return h^(h>>32u);
}
bool rt2_prefix_begin(uint64_t ordinal,out uint row,out uint suffix,out uint tail,out uint64_t prefix){
 row=0u;suffix=0u;tail=0u;prefix=uint64_t(0u);
 uint64_t base=rt2_prefix_u64(rt2_visible_prefix.v[2u],rt2_visible_prefix.v[3u]);
 if(rt2_visible_prefix.v[1u]==0u||ordinal<base||ordinal-base>=uint64_t(16777216u))return false;
 uint position=rt2_visible_prefix.v[0u]+uint(ordinal-base);row=position/1336336u;
 if(row>=rt2_visible_prefix.v[1u])return false;
 suffix=position-row*1336336u;tail=rt2_prefix_tail(suffix);prefix=rt2_prefix_row(row);return true;
}
uint rt2_prefix_next_ascii(uint v,out bool carry){
 carry=false;if(v>=48u&&v<57u)return v+1u;if(v==57u)return 65u;if(v>=65u&&v<72u)return v+1u;
 if(v==72u)return 74u;if(v>=74u&&v<78u)return v+1u;if(v==78u)return 80u;if(v>=80u&&v<90u)return v+1u;
 carry=true;return 48u;
}
void rt2_prefix_advance(inout uint row,inout uint suffix,inout uint tail,inout uint64_t prefix){
 suffix++;
 if(suffix==1336336u){suffix=0u;row++;tail=0x30303030u;prefix=row<rt2_visible_prefix.v[1u]?rt2_prefix_row(row):uint64_t(0u);return;}
 for(int shift=24;shift>=0;shift-=8){bool carry;uint next=rt2_prefix_next_ascii((tail>>uint(shift))&255u,carry);tail=(tail&~(255u<<uint(shift)))|(next<<uint(shift));if(!carry)return;}
}
uint64_t __RT2_ROOT_FUNCTION__(uint64_t ordinal){
 uint row,suffix,tail;uint64_t prefix;
 if(rt2_prefix_begin(ordinal,row,suffix,tail,prefix))return rt2_prefix_root(prefix,tail);
 return rt2_legacy_visible_root(ordinal);
}
