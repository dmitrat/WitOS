#include "witos/pe_imports.h"
static WitU32 u32(const WitU8* p){return p[0]|((WitU32)p[1]<<8)|((WitU32)p[2]<<16)|((WitU32)p[3]<<24);}
static WitU64 u64(const WitU8* p){return u32(p)|((WitU64)u32(p+4)<<32);}
static int overlaps(WitU32 a,WitU32 n,WitU32 b,WitU32 m)
{return n&&m&&(WitU64)a+n>b&&(WitU64)b+m>a;}
static int metadata(const WitPeImage* image,WitU32 fileBytes,WitU32 rva,WitU32 bytes,WitU32* raw)
{
    int readonly=0;
    for(WitU32 i=0;i<image->SectionCount;++i){const WitPeSection* s=&image->Sections[i];
        if(s->Flags==WIT_PE_READ&&rva>=s->Rva&&rva-s->Rva<=s->VirtualSize&&bytes<=s->VirtualSize-(rva-s->Rva))readonly=1;
    }
    return readonly&&wit_pe_file_range(image,rva,bytes,raw)&&*raw<=fileBytes&&bytes<=fileBytes-*raw;
}
static int name(const WitU8* file,WitU32 bytes,const WitPeImage* image,WitU32 rva,int module,WitU32* length)
{
    for(WitU32 i=0;i<=WIT_PE_IMPORT_NAME;++i){WitU32 raw;
        if((WitU64)rva+i>0xFFFFFFFFULL||!metadata(image,bytes,rva+i,1,&raw))return 0;
        const WitU8 c=file[raw];
        if(!c){if(!i)return 0;*length=i;return 1;}
        if(c<33||c>126||(module&&(c=='/'||c=='\\'||c==':')))return 0;
    }
    return 0;
}
int wit_pe_imports_overlap(const WitPeImports* imports,WitU32 rva,WitU32 bytes)
{
    if(overlaps(rva,bytes,imports->DirectoryRva,imports->DirectoryBytes))return 1;
    for(WitU32 i=0;i<imports->ModuleCount;++i){const WitPeImportModule* m=&imports->Modules[i];
        const WitU32 tableBytes=(m->SymbolCount+1)*8;
        if(overlaps(rva,bytes,m->NameRva,m->NameBytes+1)||overlaps(rva,bytes,m->LookupRva,tableBytes)||overlaps(rva,bytes,m->IatRva,tableBytes))return 1;
    }
    for(WitU32 i=0;i<imports->SymbolCount;++i){const WitPeImportSymbol* s=&imports->Symbols[i];
        if(s->NameRva&&overlaps(rva,bytes,s->NameRva,s->NameBytes+3))return 1;
    }
    return 0;
}
static int independent_iat(const WitPeImports* imports)
{
    for(WitU32 i=0;i<imports->ModuleCount;++i){const WitPeImportModule* a=&imports->Modules[i];const WitU32 bytes=(a->SymbolCount+1)*8;
        if(overlaps(a->IatRva,bytes,imports->DirectoryRva,imports->DirectoryBytes))return 0;
        for(WitU32 j=0;j<imports->ModuleCount;++j){const WitPeImportModule* b=&imports->Modules[j];const WitU32 other=(b->SymbolCount+1)*8;
            if(overlaps(a->IatRva,bytes,b->NameRva,b->NameBytes+1))return 0;
            if(i!=j&&overlaps(a->IatRva,bytes,b->IatRva,other))return 0;
            if(!(i==j&&a->IatRva==a->LookupRva)&&overlaps(a->IatRva,bytes,b->LookupRva,other))return 0;
        }
        for(WitU32 j=0;j<imports->SymbolCount;++j){const WitPeImportSymbol* s=&imports->Symbols[j];
            if(s->NameRva&&overlaps(a->IatRva,bytes,s->NameRva,s->NameBytes+3))return 0;
        }
    }
    return 1;
}
WitPeStatus wit_pe_imports_validate(const WitU8* file,WitU32 bytes,const WitPeImage* image,WitU32 rva,WitU32 size,WitPeImports* output)
{
    if(!file||!image||!output||(!rva)!=(!size))return WitPeInvalidImage;
    output->DirectoryRva=rva;output->DirectoryBytes=size;output->ModuleCount=output->SymbolCount=0;
    if(!size)return WitPeOk;
    if(size>(WIT_PE_IMPORT_MODULES+1)*20)return WitPeTooLarge;
    WitU32 directory;
    if(size<20||(rva&3)||!metadata(image,bytes,rva,size,&directory))return WitPeInvalidImage;
    int terminated=0;
    for(WitU32 at=0;at+20<=size;at+=20){const WitU8* descriptor=file+directory+at;
        const WitU32 lookup=u32(descriptor),stamp=u32(descriptor+4),chain=u32(descriptor+8),moduleName=u32(descriptor+12),iat=u32(descriptor+16);
        if(!(lookup|stamp|chain|moduleName|iat)){
            for(WitU32 i=at+20;i<size;++i)if(file[directory+i])return WitPeInvalidImage;
            terminated=1;break;
        }
        if(stamp||chain)return WitPeUnsupportedImage;
        if(output->ModuleCount==WIT_PE_IMPORT_MODULES)return WitPeTooLarge;
        if(!moduleName||!iat||(iat&7)||(lookup&7))return WitPeInvalidImage;
        WitPeImportModule* module=&output->Modules[output->ModuleCount++];
        *module=(WitPeImportModule){moduleName,0,lookup?lookup:iat,iat,output->SymbolCount,0};
        if(!name(file,bytes,image,moduleName,1,&module->NameBytes))return WitPeInvalidImage;
        for(;;){const WitU32 offset=module->SymbolCount*8;WitU32 source,target;
            if((WitU64)module->LookupRva+offset>0xFFFFFFFFULL||(WitU64)iat+offset>0xFFFFFFFFULL||
               !metadata(image,bytes,module->LookupRva+offset,8,&source)||!metadata(image,bytes,iat+offset,8,&target))return WitPeInvalidImage;
            const WitU64 thunk=u64(file+source);
            if(u64(file+target)!=thunk)return WitPeInvalidImage;
            if(!thunk)break;
            if(output->SymbolCount==WIT_PE_IMPORT_SYMBOLS)return WitPeTooLarge;
            WitPeImportSymbol* symbol=&output->Symbols[output->SymbolCount++];*symbol=(WitPeImportSymbol){iat+offset,0,0,0};
            if(thunk&0x8000000000000000ULL){
                if(thunk&0x7FFFFFFFFFFF0000ULL)return WitPeInvalidImage;
                symbol->Ordinal=(WitU32)(thunk&65535);
            }else{
                if(thunk>0xFFFFFFFDULL||(thunk&1))return WitPeInvalidImage;
                symbol->NameRva=(WitU32)thunk;WitU32 hint;
                if(!metadata(image,bytes,symbol->NameRva,2,&hint)||!name(file,bytes,image,symbol->NameRva+2,0,&symbol->NameBytes))return WitPeInvalidImage;
            }
            ++module->SymbolCount;
        }
    }
    return terminated&&independent_iat(output)?WitPeOk:WitPeInvalidImage;
}
