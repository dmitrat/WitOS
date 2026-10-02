#include <stdio.h>
#include <string.h>
#include "directory.h"
static int oracle(const char* name,const char* pattern)
{
    if(!*pattern)return !*name;
    if(*pattern=='*')return oracle(name,pattern+1)||(*name&&oracle(name+1,pattern));
    return *name&&(*pattern=='?'||*pattern==*name)&&oracle(name+1,pattern+1);
}
static void word(char* output,unsigned length,unsigned number,const char* alphabet,unsigned count)
{for(unsigned i=0;i<length;++i){output[i]=alphabet[number%count];number/=count;}output[length]=0;}
int main(void)
{
    unsigned cases=0;char name[6],pattern[6];
    for(unsigned size=1;size<=5;++size)for(unsigned value=0;value<(1U<<size);++value){
        word(name,size,value,"ab",2);
        for(unsigned length=1;length<=5;++length)for(unsigned p=0;p<(1U<<(length*2));++p){
            word(pattern,length,p,"ab?*",4);
            if(wit_directory_match(name,size,pattern,length)!=oracle(name,pattern)){printf("FAIL name=%s pattern=%s\n",name,pattern);return 1;}++cases;
        }
    }
    const char unicode[]={ (char)-50,(char)-69,(char)-16,(char)-97,(char)-103,(char)-126 };
    if(wit_directory_match(unicode,sizeof(unicode),"??",2)!=1||wit_directory_match(unicode,sizeof(unicode),"?",1)!=0)return 2;
    if(wit_directory_match("A",1,"a",1)!=0||wit_directory_match("a",1,"a/b",3)!=-1||wit_directory_match("a",1,"",0)!=-1)return 3;
    WitNativeDirectory directory;if(wit_native_directory_open("/",1,&directory)!=WIT_STATUS_OK)return 4;
    if(wit_native_cwd_set("/dir",4)!=WIT_STATUS_OK)return 5; // Open iterator retains root.
    WitStorageInfo info;WitU32 found=99;
    if(wit_native_directory_next(&directory,"*.json",6,0,&info,&found)!=WIT_STATUS_OK||found!=1||info.NameBytes!=15||memcmp(info.Name,"alpha.deps.json",15))return 6;
    if(wit_native_directory_next(&directory,"*.json",6,0,&info,&found)!=WIT_STATUS_OK||found!=1||info.NameBytes!=9||memcmp(info.Name,"beta.json",9))return 7;
    memset(&info,0xA5,sizeof(info));unsigned char unchanged[sizeof(info)];memcpy(unchanged,&info,sizeof(info));
    if(wit_native_directory_next(&directory,"*.json",6,0,&info,&found)!=WIT_STATUS_OK||found||memcmp(&info,unchanged,sizeof(info)))return 8;
    const WitU32 cursor=directory.Cursor;found=99;
    if(wit_native_directory_next(&directory,"a/b",3,0,&info,&found)!=WIT_STATUS_INVALID_ARGUMENT||found!=99||directory.Cursor!=cursor||memcmp(&info,unchanged,sizeof(info)))return 9;
    if(wit_native_directory_open("sub",3,&directory)!=WIT_STATUS_OK||wit_native_directory_next(&directory,"*",1,0,&info,&found)!=WIT_STATUS_OK||found)return 10;
    if(wit_native_directory_open("/file",5,&directory)!=WIT_STATUS_WRONG_TYPE||wit_native_directory_open("/missing",8,&directory)!=WIT_STATUS_NOT_FOUND)return 11;
    printf("PASS: %u independent wildcard comparisons, Unicode scalar matching and native iterator contracts\n",cases);return 0;
}
