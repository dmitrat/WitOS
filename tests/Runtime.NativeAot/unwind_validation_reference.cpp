#include "unwind_environment.h"
#include "unwind_validation.witos.h"
#include <string.h>
static unsigned checks;
static void put32(unsigned char* p,unsigned value){memcpy(p,&value,4);}
extern "C" bool metadata_tests()
{
    auto memory=(unsigned char*)VirtualAlloc(nullptr,8192,MEM_RESERVE,PAGE_NOACCESS);
    if(!memory||!VirtualAlloc(memory,4096,MEM_COMMIT,PAGE_READWRITE))return false;
    WitUserImageInfo image={};image.Version=WIT_IMAGE_INFO_VERSION;image.Size=sizeof(image);image.Base=(WitU64)memory;image.ImageSize=8192;
    image.RangeCount=2;image.Ranges[0]={64,64,64,WIT_IMAGE_INFO_READ|WIT_IMAGE_INFO_EXECUTE};image.Ranges[1]={256,3584,3584,WIT_IMAGE_INFO_READ};
    image.UnwindRva=3584;image.UnwindSize=12;
    const unsigned char baseline[]={1,5,2,0,5,0x32,1,0x30};
    auto reset=[&](){memset(memory,0,4096);memcpy(memory+256,baseline,8);put32(memory+3584,64);put32(memory+3588,80);put32(memory+3592,256);};
    auto check=[&](WitUnwindValidation expected){WitUnwindRecord record;memset(&record,0x5A,sizeof(record));
        auto got=wit_unwind_validate_function(&image,3584,&record);++checks;
        if(got!=expected){printf("metadata FAIL case=%u expected=%u actual=%u\n",checks,expected,got);return false;}
        if(got!=WitUnwindValid)for(size_t i=0;i<sizeof(record);++i)if(((const unsigned char*)&record)[i]!=0x5A)return false;
        return true;};
    reset();if(!check(WitUnwindValid)||wit_unwind_validate_image(&image)!=WitUnwindValid)return false;
    reset();memory[256]=3;if(!check(WitUnwindUnsupported))return false;
    reset();memory[256]=0x29;if(!check(WitUnwindBadFormat))return false;
    reset();memory[258]=1;if(!check(WitUnwindValid))return false; // One complete ALLOC_SMALL opcode.
    reset();memory[258]=1;memory[261]=1;if(!check(WitUnwindBadFormat))return false; // Truncated ALLOC_LARGE.
    reset();memory[260]=6;if(!check(WitUnwindBadFormat))return false;
    reset();memory[261]=0xF;if(!check(WitUnwindUnsupported))return false;
    reset();memory[259]=5;if(!check(WitUnwindBadFormat))return false;
    reset();memory[256]=9;put32(memory+264,64);if(!check(WitUnwindValid))return false;
    put32(memory+264,256);if(!check(WitUnwindBadRange))return false;
    reset();memory[256]=0x21;memory[257]=0;memory[258]=0;put32(memory+260,64);put32(memory+264,80);put32(memory+268,256);
    if(!check(WitUnwindCycle))return false;
    reset();for(unsigned i=0;i<34;++i){const unsigned rva=256+i*20;memory[rva]=i==33?1:0x21;memory[rva+1]=memory[rva+2]=memory[rva+3]=0;
        put32(memory+rva+4,64);put32(memory+rva+8,80);put32(memory+rva+12,rva+20);}
    if(!check(WitUnwindQuota))return false;
    reset();const unsigned char version2[]={2,2,4,0,3,0x16,0,6,2,0x60,1,0x70};memcpy(memory+256,version2,sizeof(version2));
    if(!check(WitUnwindValid))return false;memory[260]=0;if(!check(WitUnwindBadFormat))return false;
    reset();image.Ranges[1].Flags|=WIT_IMAGE_INFO_WRITE;if(!check(WitUnwindBadRange))return false;image.Ranges[1].Flags=WIT_IMAGE_INFO_READ;
    reset();put32(memory+3592,4092);memory[4092]=1;memory[4094]=255;
    image.RangeCount=3;image.Ranges[2]={4092,4,4,WIT_IMAGE_INFO_READ};if(!check(WitUnwindBadRange))return false;
    image.RangeCount=2;reset();image.Base=~0ULL-100;if(!check(WitUnwindBadFormat))return false;image.Base=(WitU64)memory;
    reset();put32(memory+3592,0xFFFFFFFC);if(!check(WitUnwindBadRange))return false;
    reset();image.Base=0;if(!check(WitUnwindBadFormat))return false;image.Base=(WitU64)memory;
    reset();memory[256]=0x21;memory[257]=1;memory[258]=1;memory[259]=5;memory[260]=1;memory[261]=3;
    put32(memory+264,64);put32(memory+268,80);put32(memory+272,320);memory[320]=1;memory[323]=5;
    if(!check(WitUnwindBadFormat))return false; // Child SET_FPREG cannot repair malformed primary prolog metadata.
    reset();WitUnwindStackRange stack={(WitU64)memory,(WitU64)memory+4096};unsigned char output[16];
    memset(output,0x5A,sizeof(output));
    if(!wit_unwind_read_stack(&stack,(WitU64)memory+4088,output,8))return false;
    memset(output,0x5A,sizeof(output));
    if(wit_unwind_read_stack(&stack,(WitU64)memory+4089,output,8)||wit_unwind_read_stack(&stack,~0ULL,output,8)||
        wit_unwind_read_stack(&stack,(WitU64)memory,output,32))return false;
    for(auto c:output)if(c!=0x5A)return false;
    if(!wit_unwind_read_code(&image,(WitU64)memory+64,output,1))return false;
    memset(output,0x5A,sizeof(output));
    if(wit_unwind_read_code(&image,(WitU64)memory+256,output,1)||wit_unwind_read_code(&image,(WitU64)memory+127,output,2)||
        wit_unwind_read_code(&image,~0ULL,output,1))return false;
    for(auto c:output)if(c!=0x5A)return false;
    puts("PASS: 8 bounded unwind read cases");
    if(!VirtualFree(memory,0,MEM_RELEASE))return false;
    printf("PASS: %u bounded unwind metadata cases\n",checks);return true;
}
extern "C" bool describe_host_image(const void* address,WitUserImageInfo* output)
{
    auto base=(const unsigned char*)address;auto dos=(IMAGE_DOS_HEADER*)base;
    auto nt=(IMAGE_NT_HEADERS64*)(base+dos->e_lfanew);auto sections=IMAGE_FIRST_SECTION(nt);
    auto& image=*output;image={};image.Version=WIT_IMAGE_INFO_VERSION;image.Size=sizeof(image);image.Base=(WitU64)base;image.ImageSize=nt->OptionalHeader.SizeOfImage;
    if(nt->FileHeader.NumberOfSections>WIT_IMAGE_INFO_MAX_RANGES)return false;
    image.RangeCount=nt->FileHeader.NumberOfSections;
    for(unsigned i=0;i<image.RangeCount;++i){auto& r=image.Ranges[i];const auto& s=sections[i];r.Rva=s.VirtualAddress;r.Size=s.Misc.VirtualSize;r.InitializedSize=s.SizeOfRawData<r.Size?s.SizeOfRawData:r.Size;
        r.Flags=WIT_IMAGE_INFO_READ|((s.Characteristics&IMAGE_SCN_MEM_WRITE)?WIT_IMAGE_INFO_WRITE:0)|((s.Characteristics&IMAGE_SCN_MEM_EXECUTE)?WIT_IMAGE_INFO_EXECUTE:0);}
    image.UnwindRva=nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXCEPTION].VirtualAddress;
    image.UnwindSize=nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXCEPTION].Size;
    return true;
}
extern "C" bool validate_host_image(const char* path)
{
    HMODULE resource=path?LoadLibraryExA(path,nullptr,LOAD_LIBRARY_AS_IMAGE_RESOURCE):nullptr;
    if(path&&!resource)return false;
    auto base=path?(unsigned char*)((uintptr_t)resource&~(uintptr_t)3):(unsigned char*)GetModuleHandleW(nullptr);
    WitUserImageInfo image={};if(!describe_host_image(base,&image))return false;
    const auto status=wit_unwind_validate_image(&image);
    if(status!=WitUnwindValid){
        printf("host image metadata rejected: %u\n",status);
        for(unsigned offset=0;offset<image.UnwindSize;offset+=12){WitUnwindRecord record;auto one=wit_unwind_validate_function(&image,image.UnwindRva+offset,&record);
            if(one!=WitUnwindValid){auto f=(RUNTIME_FUNCTION*)(base+image.UnwindRva+offset);printf("entry=%u begin=%x end=%x unwind=%x status=%u bytes:",offset/12,f->BeginAddress,f->EndAddress,f->UnwindData,one);
                for(unsigned i=0;i<24;++i)printf(" %02x",base[f->UnwindData+i]);puts("");break;}}
        return false;
    }
    printf("PASS: complete host image metadata (%u entries)\n",image.UnwindSize/12);
    if(resource)FreeLibrary(resource);return true;
}
