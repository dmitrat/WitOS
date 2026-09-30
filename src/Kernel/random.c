#include "witos/random.h"
#include "witos/platform.h"
static WitU32 key[8];
static int ready;
static WitU64 generation;
WitU64 wit_random_generation(void) { return generation; }
static void wipe(void* pointer,WitU32 bytes) { volatile WitU8* p=(volatile WitU8*)pointer;while(bytes--)*p++=0; }
static WitU32 load(const WitU8* p) {return (WitU32)p[0]|((WitU32)p[1]<<8)|((WitU32)p[2]<<16)|((WitU32)p[3]<<24);}
static WitU32 rotate(WitU32 n,WitU32 bits) {return (n<<bits)|(n>>(32-bits));}
static void quarter(WitU32* x,WitU32 a,WitU32 b,WitU32 c,WitU32 d) {
    x[a]+=x[b];x[d]=rotate(x[d]^x[a],16);x[c]+=x[d];x[b]=rotate(x[b]^x[c],12);
    x[a]+=x[b];x[d]=rotate(x[d]^x[a],8);x[c]+=x[d];x[b]=rotate(x[b]^x[c],7);
}
static void block(const WitU32* input_key,WitU32 counter,const WitU32* nonce,WitU8* output) {
    WitU32 original[16],x[16];
    original[0]=0x61707865U;original[1]=0x3320646EU;original[2]=0x79622D32U;original[3]=0x6B206574U;
    for(WitU32 i=0;i<8;++i)original[4+i]=input_key[i];
    original[12]=counter;for(WitU32 i=0;i<3;++i)original[13+i]=nonce[i];
    for(WitU32 i=0;i<16;++i)x[i]=original[i];
    for(WitU32 round=0;round<10;++round){
        quarter(x,0,4,8,12);quarter(x,1,5,9,13);quarter(x,2,6,10,14);quarter(x,3,7,11,15);
        quarter(x,0,5,10,15);quarter(x,1,6,11,12);quarter(x,2,7,8,13);quarter(x,3,4,9,14);
    }
    for(WitU32 i=0;i<16;++i){WitU32 value=x[i]+original[i];for(WitU32 j=0;j<4;++j)output[4*i+j]=(WitU8)(value>>(8*j));}
    wipe(original,sizeof(original));wipe(x,sizeof(x));
}
int wit_random_initialize(WitU8* seed) {
    WitU8 any=0;
    if(ready||!seed)return 0;
    for(WitU32 i=0;i<WIT_RANDOM_KEY_BYTES;++i)any|=seed[i];
    if(!any)return 0;
    for(WitU32 i=0;i<8;++i)key[i]=load(seed+4*i);
    wipe(seed,WIT_RANDOM_KEY_BYTES);ready=1;return 1;
}
int wit_random_fill(WitU8* output,WitU32 bytes) {
    static const WitU32 nonce[3]={0,0,0};
    WitU8 next[64],data[64];
    if(!ready||bytes>WIT_RANDOM_BLOCK_BYTES||(!output&&bytes)||generation==~0ULL)return 0;
    if(!bytes)return 1;
    // Domain separation: block 0 is never returned and supplies the next key.
    // Block 1 supplies output. Erase the old key after every bounded request.
    block(key,0,nonce,next);block(key,1,nonce,data);
    for(WitU32 i=0;i<8;++i)key[i]=load(next+4*i);
    ++generation;
    for(WitU32 i=0;i<bytes;++i)output[i]=data[i];
    wipe(next,sizeof(next));wipe(data,sizeof(data));return 1;
}
void wit_random_self_test(void) {
    // RFC 8439 section 2.3.2. Public known-answer data, never a live seed.
    static const WitU8 expected[64]={
        0x10,0xf1,0xe7,0xe4,0xd1,0x3b,0x59,0x15,0x50,0x0f,0xdd,0x1f,0xa3,0x20,0x71,0xc4,
        0xc7,0xd1,0xf4,0xc7,0x33,0xc0,0x68,0x03,0x04,0x22,0xaa,0x9a,0xc3,0xd4,0x6c,0x4e,
        0xd2,0x82,0x64,0x46,0x07,0x9f,0xaa,0x09,0x14,0xc2,0xd7,0x05,0xd9,0x8b,0x02,0xa2,
        0xb5,0x12,0x9c,0xd1,0xde,0x16,0x4e,0xb9,0xcb,0xd0,0x83,0xe8,0xa2,0x50,0x3c,0x4e};
    const WitU32 nonce[3]={0x09000000U,0x4a000000U,0};
    WitU32 test_key[8];WitU8 output[64];
    for(WitU32 i=0;i<8;++i)test_key[i]=(4*i)|((4*i+1)<<8)|((4*i+2)<<16)|((4*i+3)<<24);
    block(test_key,1,nonce,output);
    for(WitU32 i=0;i<64;++i)if(output[i]!=expected[i])wit_panic("ChaCha20 known-answer failure");
    wipe(test_key,sizeof(test_key));wipe(output,sizeof(output));
    wit_console_write("[TEST-PASS] Random.ChaCha20Vector\n");
}
