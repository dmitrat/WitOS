/* Prepared thread-notification fixture; wired after the current ABI46 gates.
 * The callback queries kernel identity in the main image, so this fixture never
 * substitutes a TLS value for thread identity. No static DLL TLS is claimed. */
extern char __ImageBase;
extern unsigned short LibraryCurrentCs(void);
typedef void (*Notification)(unsigned);
static Notification notify;
static unsigned initialized,attached,detached;
int thread_anchor;int* thread_pointer=&thread_anchor;
int LibraryThreadEntry(void* base,unsigned reason,void* reserved)
{
    if(base!=&__ImageBase||LibraryCurrentCs()!=0x33)return 0;
    if(reason==1){if(reserved||initialized)return 0;initialized=1;return 1;}
    if(!initialized)return 0;
    if(reason==2){++attached;if(notify)notify(2);return 1;}
    if(reason==3){++detached;if(notify)notify(3);return 1;}
    if(reason==0){if(notify)notify(0);initialized=0;return 1;}
    return 0;
}
__declspec(dllexport) int SetNotification(Notification value)
{if(!initialized||notify||!value)return 0;notify=value;return 1;}
__declspec(dllexport) unsigned ThreadCounts(void)
{return (attached<<16)|detached|(unsigned)*thread_pointer;}
