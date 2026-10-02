#include "witos/package.h"

static WitU32 read32(const WitU8* p)
{ return (WitU32)p[0]|((WitU32)p[1]<<8)|((WitU32)p[2]<<16)|((WitU32)p[3]<<24); }
static WitU64 read64(const WitU8* p)
{ return read32(p)|((WitU64)read32(p+4)<<32); }
static int compare(const WitU8* a,WitU32 an,const WitU8* b,WitU32 bn)
{
    const WitU32 count=an<bn?an:bn;
    for(WitU32 i=0;i<count;++i)if(a[i]!=b[i])return a[i]<b[i]?-1:1;
    return an==bn?0:an<bn?-1:1;
}
static int component(const WitU8* p,WitU32 size)
{ return size&&!(size==1&&p[0]=='.')&&!(size==2&&p[0]=='.'&&p[1]=='.'); }
static int name_valid(const WitU8* p,WitU32 size)
{
    if(!p||!size||size>WIT_PACKAGE_MAX_NAME)return 0;
    WitU32 start=0;
    for(WitU32 i=0;i<size;){
        const WitU32 at=i;const WitU8 c=p[i++];
        if(c<32||c==127||c=='\\'||c==':')return 0;
        if(c=='/'){
            if(!component(p+start,at-start))return 0;
            start=i;continue;
        }
        if(c<128)continue;
        WitU32 remaining,code,min;
        if(c>=0xC2&&c<=0xDF){remaining=1;code=c&31;min=0x80;}
        else if(c>=0xE0&&c<=0xEF){remaining=2;code=c&15;min=0x800;}
        else if(c>=0xF0&&c<=0xF4){remaining=3;code=c&7;min=0x10000;}
        else return 0;
        if(remaining>size-i)return 0;
        while(remaining--){if((p[i]&0xC0)!=0x80)return 0;code=(code<<6)|(p[i++]&63);}
        if(code<min||code>0x10FFFF||(code>=0xD800&&code<=0xDFFF))return 0;
    }
    return component(p+start,size-start);
}
static int find_index(const WitU8* data,WitU32 count,const WitU8* name,WitU32 length,WitU32* index)
{
    WitU32 low=0,high=count;
    while(low<high){
        const WitU32 mid=low+(high-low)/2;const WitU8* entry=data+32+(WitU64)mid*32;
        const int order=compare(name,length,data+read32(entry),read32(entry+4));
        if(!order){*index=mid;return 1;}
        if(order<0)high=mid;else low=mid+1;
    }
    return 0;
}
static int zero_padding(const WitU8* data,WitU64 size,WitU64* cursor)
{
    const WitU64 end=(*cursor+7)&~7ULL; /* cursor is already bounded by 128 MiB. */
    if(end>size)return 0;
    for(WitU64 i=*cursor;i<end;++i)if(data[i])return 0;
    *cursor=end;return 1;
}
WitPackageStatus wit_package_open(const WitU8* data,WitU64 size,WitPackage* output)
{
    static const WitU8 magic[8]={'W','I','T','P','A','K','0','1'};
    if(!data||!output||size<32||size>WIT_PACKAGE_MAX_BYTES||size>~0ULL-(WitU64)data)return WitPackageInvalid;
    for(WitU32 i=0;i<8;++i)if(data[i]!=magic[i])return WitPackageInvalid;
    if(read32(data+8)!=1||read32(data+12)!=32||read32(data+20)!=32||read64(data+24)!=size)return WitPackageInvalid;
    const WitU32 count=read32(data+16);
    if(count>WIT_PACKAGE_MAX_FILES)return WitPackageInvalid;
    WitU64 cursor=32+(WitU64)count*32;
    if(cursor>size)return WitPackageInvalid;
    const WitU8* previous=0;WitU32 previousLength=0;
    for(WitU32 i=0;i<count;++i){
        const WitU8* entry=data+32+(WitU64)i*32;
        const WitU32 at=read32(entry),length=read32(entry+4);
        if(at!=cursor||length>size-cursor||read64(entry+24)||!name_valid(data+cursor,length))return WitPackageInvalid;
        if(previous&&compare(previous,previousLength,data+cursor,length)>=0)return WitPackageInvalid;
        previous=data+cursor;previousLength=length;cursor+=length;
    }
    // Reject file/directory collisions even when a sibling sorts between the
    // file and its descendants (for example a, a.b, a/child).
    for(WitU32 i=0;i<count;++i){
        const WitU8* entry=data+32+(WitU64)i*32;
        const WitU8* name=data+read32(entry);const WitU32 length=read32(entry+4);
        for(WitU32 j=0;j<length;++j)if(name[j]=='/'){
            WitU32 parent;if(find_index(data,count,name,j,&parent))return WitPackageInvalid;
        }
    }
    if(!zero_padding(data,size,&cursor))return WitPackageInvalid;
    for(WitU32 i=0;i<count;++i){
        const WitU8* entry=data+32+(WitU64)i*32;
        const WitU64 at=read64(entry+8),length=read64(entry+16);
        if(at!=cursor||length>size-cursor)return WitPackageInvalid;
        cursor+=length;if(!zero_padding(data,size,&cursor))return WitPackageInvalid;
    }
    if(cursor!=size)return WitPackageInvalid;
    const WitPackage valid={data,size,count};*output=valid;return WitPackageOk;
}
static void entry_at(const WitPackage* package,WitU32 index,WitPackageFile* output)
{
    const WitU8* entry=package->Data+32+(WitU64)index*32;
    const WitPackageFile file={read64(entry+8),read64(entry+16),package->Data+read32(entry),read32(entry+4)};
    *output=file;
}
static int valid_descriptor(const WitPackage* package,WitPackage* verified)
{
    return package&&wit_package_open(package->Data,package->Size,verified)==WitPackageOk&&verified->Count==package->Count;
}
WitPackageStatus wit_package_get(const WitPackage* package,WitU32 index,WitPackageFile* output)
{
    WitPackage verified;
    if(!output||!valid_descriptor(package,&verified))return WitPackageInvalid;
    if(index>=verified.Count)return WitPackageMissing;
    entry_at(&verified,index,output);return WitPackageOk;
}
WitPackageStatus wit_package_find(const WitPackage* package,const WitU8* name,WitU32 length,WitPackageFile* output)
{
    WitPackage verified;
    if(!output||!name_valid(name,length)||!valid_descriptor(package,&verified))return WitPackageInvalid;
    WitU32 low=0,high=verified.Count;
    while(low<high){
        const WitU32 mid=low+(high-low)/2;WitPackageFile file;entry_at(&verified,mid,&file);
        const int order=compare(name,length,file.Name,file.NameLength);
        if(!order){*output=file;return WitPackageOk;}
        if(order<0)high=mid;else low=mid+1;
    }
    return WitPackageMissing;
}

static int descendant(const WitPackageFile* file,const WitU8* directory,WitU32 length)
{
    return !length||(file->NameLength>length&&file->Name[length]=='/'&&compare(directory,length,file->Name,length)==0);
}
static WitPackageStatus stat_verified(const WitPackage* package,const WitU8* name,WitU32 length,WitPackageNode* output)
{
    if(!length){const WitPackageNode root={package->Data,0,WIT_PACKAGE_DIRECTORY,0};*output=root;return WitPackageOk;}
    WitU32 index;
    if(find_index(package->Data,package->Count,name,length,&index)){
        WitPackageFile file;entry_at(package,index,&file);
        const WitPackageNode node={file.Name,file.NameLength,WIT_PACKAGE_FILE,file.Length};*output=node;return WitPackageOk;
    }
    for(WitU32 i=0;i<package->Count;++i){
        WitPackageFile file;entry_at(package,i,&file);
        if(descendant(&file,name,length)){const WitPackageNode node={file.Name,length,WIT_PACKAGE_DIRECTORY,0};*output=node;return WitPackageOk;}
    }
    return WitPackageMissing;
}
WitPackageStatus wit_package_stat(const WitPackage* package,const WitU8* name,WitU32 length,WitPackageNode* output)
{
    WitPackage verified;
    if(!output||(length&&!name_valid(name,length))||!valid_descriptor(package,&verified))return WitPackageInvalid;
    return stat_verified(&verified,name,length,output);
}
WitPackageStatus wit_package_list(const WitPackage* package,const WitU8* name,WitU32 length,WitU32 cursor,WitPackageNode* output,WitU32* next)
{
    WitPackage verified;WitPackageNode directory;
    if(!output||!next||(length&&!name_valid(name,length))||!valid_descriptor(package,&verified)||cursor>verified.Count)return WitPackageInvalid;
    const WitPackageStatus status=stat_verified(&verified,name,length,&directory);
    if(status!=WitPackageOk)return status;
    if(directory.Kind!=WIT_PACKAGE_DIRECTORY)return WitPackageNotDirectory;
    for(WitU32 i=cursor;i<verified.Count;++i){
        WitPackageFile file;entry_at(&verified,i,&file);
        if(!descendant(&file,name,length))continue;
        const WitU32 start=length?length+1:0;WitU32 end=start;
        while(end<file.NameLength&&file.Name[end]!='/')++end;
        const int isDirectory=end<file.NameLength;
        WitPackageNode found={file.Name+start,end-start,isDirectory?WIT_PACKAGE_DIRECTORY:WIT_PACKAGE_FILE,isDirectory?0:file.Length};
        WitU32 after=i+1;
        if(isDirectory)while(after<verified.Count){
            WitPackageFile following;entry_at(&verified,after,&following);
            if(!descendant(&following,file.Name,end))break;
            ++after;
        }
        *output=found;*next=after;return WitPackageOk;
    }
    return WitPackageEnd;
}
