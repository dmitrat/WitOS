#include "witos/pe.h"
static WitU16 u16(const WitU8* p){return (WitU16)(p[0]|((WitU16)p[1]<<8));}
static WitU32 u32(const WitU8* p){return u16(p)|((WitU32)u16(p+2)<<16);}
static int within(WitU32 at,WitU32 size,WitU32 base,WitU32 bytes)
{return at>=base&&at-base<=bytes&&size<=bytes-(at-base);}
static int metadata(const WitPeImage* image,WitU32 at,WitU32 bytes,WitU32* raw)
{return within(at,bytes,image->ExportRva,image->ExportSize)&&wit_pe_file_range(image,at,bytes,raw);}
static int text(const WitU8* file,const WitPeImage* image,WitU32 at,const WitU8** value,WitU32* length)
{
    WitU32 raw;if(!metadata(image,at,1,&raw))return 0;
    WitU32 count=0;
    while(count<=WIT_PE_EXPORT_NAME_MAX){
        WitU32 byte;if(!metadata(image,at+count,1,&byte))return 0;
        const WitU8 c=file[byte];if(!c){if(!count)return 0;*value=file+raw;*length=count;return 1;}
        if(c<33||c>126)return 0;++count;
    }
    return 0;
}
static int compare(const WitU8* a,WitU32 an,const WitU8* b,WitU32 bn)
{
    const WitU32 count=an<bn?an:bn;
    for(WitU32 i=0;i<count;++i)if(a[i]!=b[i])return a[i]<b[i]?-1:1;
    return an==bn?0:an<bn?-1:1;
}
static int mapped(const WitPeImage* image,WitU32 rva)
{
    for(WitU32 i=0;i<image->SectionCount;++i){const WitPeSection* s=&image->Sections[i];
        if(rva>=s->Rva&&rva-s->Rva<s->VirtualSize&&(s->Flags&WIT_PE_READ))
            return !(s->Flags&WIT_PE_EXECUTE)||rva-s->Rva<s->RawSize;
    }
    return 0;
}
WitPeStatus wit_pe_exports_validate(const WitU8* file,WitPeImage* image)
{
    image->ExportBase=image->ExportCount=image->ExportNames=0;
    image->ExportFunctionsRva=image->ExportNamesRva=image->ExportOrdinalsRva=0;
    if(!image->ExportSize)return WitPeOk;
    WitU32 raw;
    if(image->ExportSize<40||!wit_pe_file_range(image,image->ExportRva,image->ExportSize,&raw))return WitPeInvalidImage;
    int readonly=0;
    for(WitU32 i=0;i<image->SectionCount;++i){const WitPeSection* s=&image->Sections[i];
        if(s->Flags==WIT_PE_READ&&within(image->ExportRva,image->ExportSize,s->Rva,s->VirtualSize))readonly=1;
    }
    if(!readonly||u32(file+raw))return WitPeInvalidImage;
    const WitU8* name;WitU32 length;
    if(!text(file,image,u32(file+raw+12),&name,&length))return WitPeInvalidImage;
    image->ExportBase=u32(file+raw+16);image->ExportCount=u32(file+raw+20);image->ExportNames=u32(file+raw+24);
    image->ExportFunctionsRva=u32(file+raw+28);image->ExportNamesRva=u32(file+raw+32);image->ExportOrdinalsRva=u32(file+raw+36);
    if(!image->ExportCount||image->ExportCount>WIT_PE_EXPORT_CAPACITY||image->ExportNames>WIT_PE_EXPORT_CAPACITY)return WitPeTooLarge;
    if(image->ExportBase>~0U-(image->ExportCount-1))return WitPeInvalidImage;
    WitU32 functions,names=0,ordinals=0;
    if((image->ExportFunctionsRva&3)||!metadata(image,image->ExportFunctionsRva,image->ExportCount*4,&functions))return WitPeInvalidImage;
    if(image->ExportNames&&((image->ExportNamesRva&3)||(image->ExportOrdinalsRva&1)||
        !metadata(image,image->ExportNamesRva,image->ExportNames*4,&names)||!metadata(image,image->ExportOrdinalsRva,image->ExportNames*2,&ordinals)))return WitPeInvalidImage;
    for(WitU32 i=0;i<image->ExportCount;++i){const WitU32 rva=u32(file+functions+i*4);
        if(!rva)continue;
        if(within(rva,1,image->ExportRva,image->ExportSize))return WitPeUnsupportedImage; // Forwarder, not executable code.
        if(!mapped(image,rva))return WitPeInvalidImage;
    }
    const WitU8* previous=0;WitU32 previousLength=0;
    for(WitU32 i=0;i<image->ExportNames;++i){
        const WitU32 ordinal=u16(file+ordinals+i*2);
        if(ordinal>=image->ExportCount||!u32(file+functions+ordinal*4)||!text(file,image,u32(file+names+i*4),&name,&length))return WitPeInvalidImage;
        if(previous&&compare(previous,previousLength,name,length)>=0)return WitPeInvalidImage;
        previous=name;previousLength=length;
    }
    return WitPeOk;
}
WitPeStatus wit_pe_export_find(const WitU8* file,const WitPeImage* image,const char* name,WitU32 length,WitU32 ordinal,WitU32* output)
{
    if(!file||!image||!output||(name&&(!length||length>WIT_PE_EXPORT_NAME_MAX))||(!name&&length))return WitPeInvalidImage;
    WitU32 index=~0U,raw;
    if(!image->ExportSize){*output=0;return WitPeOk;}
    if(name){
        if(!image->ExportNames){*output=0;return WitPeOk;}
        for(WitU32 i=0;i<length;++i)if((WitU8)name[i]<33||(WitU8)name[i]>126)return WitPeInvalidImage;
        WitU32 names,ordinals;if(!metadata(image,image->ExportNamesRva,image->ExportNames*4,&names)||!metadata(image,image->ExportOrdinalsRva,image->ExportNames*2,&ordinals))return WitPeInvalidImage;
        WitU32 low=0,high=image->ExportNames;
        while(low<high){const WitU32 mid=low+(high-low)/2;const WitU8* entry;WitU32 bytes;
            if(!text(file,image,u32(file+names+mid*4),&entry,&bytes))return WitPeInvalidImage;
            const int order=compare((const WitU8*)name,length,entry,bytes);
            if(!order){index=u16(file+ordinals+mid*2);break;}if(order<0)high=mid;else low=mid+1;
        }
    }else if(ordinal>=image->ExportBase&&ordinal-image->ExportBase<image->ExportCount)index=ordinal-image->ExportBase;
    if(index==~0U){*output=0;return WitPeOk;}
    if(index>=image->ExportCount||!metadata(image,image->ExportFunctionsRva+index*4,4,&raw))return WitPeInvalidImage;
    *output=u32(file+raw);return WitPeOk;
}
