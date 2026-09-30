extern "C" void wit_gs_reference_corrupt(volatile unsigned char*,unsigned);
extern "C" __declspec(noinline) unsigned wit_gs_reference_protected(unsigned tamper)
{
    volatile unsigned char buffer[128];
    for(unsigned i=0;i<128;++i)buffer[i]=(unsigned char)(i^0x5a);
    wit_gs_reference_corrupt(buffer,tamper);
    return buffer[13]^(13^0x5a);
}
