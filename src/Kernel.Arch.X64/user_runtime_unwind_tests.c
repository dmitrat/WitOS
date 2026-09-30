#include "user.h"
#if defined(WITOS_TEST_RUNTIME_CONFIG)
#include "witos/platform.h"
#include "runtime_unwind_image.h"
static WitU8 altered[sizeof(wit_runtime_unwind_image)];
static WitPeImage original,validated;
static WitU32 targets[WIT_PE_MAX_RELOCATIONS+1];
static WitUserProcess rejected;
static void require(int ok,const char* message){if(!ok)wit_panic(message);}
static WitU32 read32(const WitU8* p){return p[0]|((WitU32)p[1]<<8)|((WitU32)p[2]<<16)|((WitU32)p[3]<<24);}
static WitU64 read64(const WitU8* p){return read32(p)|((WitU64)read32(p+4)<<32);}
static void put16(WitU32 at,WitU32 value){altered[at]=(WitU8)value;altered[at+1]=(WitU8)(value>>8);}
static void put32(WitU32 at,WitU32 value){for(WitU32 i=0;i<4;++i)altered[at+i]=(WitU8)(value>>(8*i));}
static void put64(WitU32 at,WitU64 value){for(WitU32 i=0;i<8;++i)altered[at+i]=(WitU8)(value>>(8*i));}
static WitU32 raw(WitU32 rva,WitU32 size){WitU32 offset;require(wit_pe_file_range(&original,rva,size,&offset),"Runtime metadata test RVA missing");return offset;}
static void reset(void){for(WitU32 i=0;i<sizeof(wit_runtime_unwind_image);++i)altered[i]=wit_runtime_unwind_image[i];}
static void reject(WitPageAllocator* pages,WitPeStatus expected,WitU32 profile)
{
    const WitU64 before=wit_pages_free_count(pages);
    const WitPeStatus status=wit_user_create_pe_profile(&rejected,pages,0,altered,sizeof(wit_runtime_unwind_image),WIT_USER_IMAGE_BASE,0,profile);
    if(status!=expected){wit_console_write("Runtime metadata rejection actual/expected: ");wit_console_write_u64(status);wit_console_write("/");wit_console_write_u64(expected);wit_console_write("\n");}
    require(status==expected&&!rejected.Space.Root&&!rejected.Space.OwnedCount&&!rejected.Handles.Count&&wit_pages_free_count(pages)==before,
        "Runtime metadata rejection allocated or published a component");
}
void wit_user_runtime_unwind_metadata_self_test(WitPageAllocator* pages)
{
    require(sizeof(wit_runtime_unwind_image)<=sizeof(altered)&&wit_pe_validate_profile(wit_runtime_unwind_image,sizeof(wit_runtime_unwind_image),&original,WIT_PE_UNWIND_RUNTIME)==WitPeOk,
        "Actual runtime unwind image invalid before negative tests");
    const WitU32 optional=read32(wit_runtime_unwind_image+60)+24,sections=optional+240;
    const WitU32 directory=raw(original.UnwindRva,original.UnwindSize);
    const WitU32 infoRva=read32(wit_runtime_unwind_image+directory+8),info=raw(infoRva,4);
    const WitU32 extent=(4+(WitU32)wit_runtime_unwind_image[info+2]*2+3)&~3U;
    require((wit_runtime_unwind_image[info]>>3)&3,"Compiler fixture lost its GS handler metadata");
    reset();reject(pages,WitPeUnsupportedImage,2);
    reset();put32(optional+116+3*8,(WIT_PE_RUNTIME_UNWIND_ENTRIES+1)*12);reject(pages,WitPeTooLarge,WIT_PE_UNWIND_RUNTIME);
    reset();put32(optional+116+3*8,12);reject(pages,WitPeUnsupportedImage,0); // Plain profile still refuses the first real GS record.
    reset();altered[info]=3;reject(pages,WitPeUnsupportedImage,WIT_PE_UNWIND_RUNTIME);
    reset();put32(info+extent,original.UnwindRva);reject(pages,WitPeInvalidImage,WIT_PE_UNWIND_RUNTIME);
    reset();for(WitU32 i=0;i<12;++i){WitU8 swap=altered[directory+i];altered[directory+i]=altered[directory+12+i];altered[directory+12+i]=swap;}
    reject(pages,WitPeInvalidImage,WIT_PE_UNWIND_RUNTIME);
    reset();put32(directory+12,read32(altered+directory)+1);reject(pages,WitPeInvalidImage,WIT_PE_UNWIND_RUNTIME);
    WitU32 chain=0,ro=0;
    for(WitU32 i=0;i<original.UnwindSize/12;++i){WitU32 at=raw(read32(wit_runtime_unwind_image+directory+i*12+8),4);if((wit_runtime_unwind_image[at]>>3)==4){chain=at;break;}}
    require(chain!=0,"Runtime fixture contains no chained unwind record");
    reset();altered[chain]|=8;reject(pages,WitPeInvalidImage,WIT_PE_UNWIND_RUNTIME);
    reset();{
        const WitU32 bytes=(4+(WitU32)altered[chain+2]*2+3)&~3U;
        WitU32 chainRva=0;for(WitU32 i=0;i<original.SectionCount;++i){const WitPeSection* s=&original.Sections[i];if(chain>=s->RawOffset&&chain-s->RawOffset<s->RawSize)chainRva=s->Rva+chain-s->RawOffset;}
        put32(chain+bytes+8,chainRva);reject(pages,WitPeInvalidImage,WIT_PE_UNWIND_RUNTIME);
    }
    for(WitU32 i=0;i<original.SectionCount;++i)if(infoRva>=original.Sections[i].Rva&&infoRva-original.Sections[i].Rva<original.Sections[i].VirtualSize)ro=i;
    reset();put32(sections+ro*40+36,read32(altered+sections+ro*40+36)|0x80000000U);reject(pages,WitPeInvalidImage,WIT_PE_UNWIND_RUNTIME);
    reset();{
        const WitPeSection* s=&original.Sections[ro];const WitU32 size=s->VirtualSize<s->RawSize?s->VirtualSize:s->RawSize;
        const WitU32 last=(s->Rva+size-4)&~3U,offset=raw(last,4);
        put32(directory+8,last);altered[offset]=1;altered[offset+1]=0;altered[offset+2]=255;altered[offset+3]=0;
        reject(pages,WitPeInvalidImage,WIT_PE_UNWIND_RUNTIME);
    }
    // Isolate relocation exclusion: make the first metadata qword a valid VA.
    // Before adding a relocation, every other parser check must accept the image.
    reset();const WitU64 preferred=0x100000000ULL;
    const WitU32 relocation=raw(original.RelocRva,original.RelocSize);
    WitU32 targetCount=0;
    for(WitU32 consumed=0;consumed<original.RelocSize;){
        const WitU32 page=read32(altered+relocation+consumed),length=read32(altered+relocation+consumed+4);
        for(WitU32 i=8;i<length;i+=2){const WitU32 item=altered[relocation+consumed+i]|((WitU32)altered[relocation+consumed+i+1]<<8);
            if(item>>12){const WitU32 target=page+(item&4095);WitU32 offset=raw(target,8);put64(offset,preferred+read64(altered+offset)-original.PreferredBase);targets[targetCount++]=target;}}
        consumed+=length;
    }
    put64(optional+24,preferred);put64(info,preferred+1);
    require(wit_pe_validate_profile(altered,sizeof(wit_runtime_unwind_image),&validated,WIT_PE_UNWIND_RUNTIME)==WitPeOk,"Relocation metadata control image invalid");
    WitU32 relocSection=0;
    for(WitU32 i=0;i<original.SectionCount;++i)if(original.RelocRva>=original.Sections[i].Rva&&original.RelocRva-original.Sections[i].Rva<original.Sections[i].VirtualSize)relocSection=i;
    const WitPeSection* section=&original.Sections[relocSection];
    const WitU32 controlTarget=infoRva+8;
    put64(raw(controlTarget,8),preferred+1);
    WitU32 insert=0;while(insert<targetCount&&targets[insert]<controlTarget)++insert;
    require(targetCount<WIT_PE_MAX_RELOCATIONS&&(insert==targetCount||targets[insert]!=controlTarget),"Relocation control target already used");
    for(WitU32 i=targetCount;i>insert;--i)targets[i]=targets[i-1];targets[insert]=controlTarget;++targetCount;
    WitU32 used=0,index=0,controlEntry=0;
    while(index<targetCount){
        const WitU32 page=targets[index]&~4095U,begin=used;
        require(used+12<=section->RawSize,"Relocation control table exceeds initialized section");
        put32(relocation+used,page);used+=8;
        while(index<targetCount&&(targets[index]&~4095U)==page){
            require(used+2<=section->RawSize,"Relocation control entries exceed initialized section");
            if(targets[index]==controlTarget)controlEntry=relocation+used;
            put16(relocation+used,0xA000U|(targets[index]&4095U));used+=2;++index;
        }
        if(used&3){require(used+2<=section->RawSize,"Relocation control padding exceeds section");put16(relocation+used,0);used+=2;}
        put32(relocation+begin+4,used-begin);
    }
    require(controlEntry&&original.RelocRva==section->Rva,"Relocation control section layout changed");
    put32(sections+relocSection*40+8,used>section->VirtualSize?used:section->VirtualSize);put32(optional+116+5*8,used);
    require(wit_pe_validate_profile(altered,sizeof(wit_runtime_unwind_image),&validated,WIT_PE_UNWIND_RUNTIME)==WitPeOk,
        "Sorted relocation outside metadata must be accepted");
    // Change only the DIR64 offset: both source qwords are valid image VAs,
    // block/target ordering is unchanged, but this target overlaps a validated header.
    require((infoRva&~4095U)==(controlTarget&~4095U),"Relocation controls cross a page");
    put16(controlEntry,0xA000U|(infoRva&4095U));
    reject(pages,WitPeInvalidImage,WIT_PE_UNWIND_RUNTIME);
    wit_console_write("[TEST-PASS] User.RuntimeUnwindMetadataRejection\n");
}
#endif
