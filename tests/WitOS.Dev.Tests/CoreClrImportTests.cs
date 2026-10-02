using System.Buffers.Binary;
using System.Text;
using WitOS.Dev;

internal static class CoreClrImportTests
{
    internal static Task RunAsync(string directory)
    {
        var path = Path.Combine(directory, "imports.pe");
        byte[] Image()
        {
            var bytes = new byte[1536];
            void U16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
            void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
            void U64(int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), value);
            U16(0,0x5A4D);U32(60,128);U32(128,0x4550);U16(132,0x8664);U16(134,1);U16(148,240);U16(150,0x2002);
            const int optional=152;
            U16(optional,0x20b);U64(optional+24,0x180000000);U32(optional+32,4096);U32(optional+36,512);
            U32(optional+56,8192);U32(optional+60,512);U16(optional+68,3);U32(optional+108,16);
            U32(optional+112+8,0x1000);U32(optional+116+8,40);
            U32(optional+112+13*8,0x1100);U32(optional+116+13*8,64);
            const int section=optional+240;
            Encoding.ASCII.GetBytes(".rdata").CopyTo(bytes,section);U32(section+8,1024);U32(section+12,4096);
            U32(section+16,1024);U32(section+20,512);U32(section+36,0x40000040);
            U32(512,0x1080);U32(524,0x1060);U32(528,0x1080);
            Encoding.ASCII.GetBytes("direct.dll\0").CopyTo(bytes,608);
            U64(640,0x10b0);U64(648,0x8000000000000007);Encoding.ASCII.GetBytes("Now\0").CopyTo(bytes,690);
            U32(768,1);U32(772,0x1180);U32(780,0x11a0);U32(784,0x11a0);
            Encoding.ASCII.GetBytes("late.dll\0").CopyTo(bytes,896);U64(928,0x11c0);
            Encoding.ASCII.GetBytes("Later\0").CopyTo(bytes,962);
            return bytes;
        }
        void Check(bool value,string why){if(!value)throw new InvalidOperationException(why);}
        var good=Image();File.WriteAllBytes(path,good);
        var result=NativeImports.Inspect(path);
        Check(result.DirectImports.Single().Library=="direct.dll"&&result.DirectImports[0].Symbols.SequenceEqual(new[]{"Now","ordinal:7"}),"Direct imports/ordinal changed");
        Check(result.DelayImports.Single().Library=="late.dll"&&result.DelayImports[0].Symbols.Single()=="Later","Delayed imports lost");
        foreach(var (name,change) in new (string,Action<byte[]>)[]{
            ("absolute delay descriptor", b=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(768),0)),
            ("unterminated delay descriptors", b=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(152+116+13*8),32)),
            ("missing delayed name", b=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(772),0)),
            ("out-of-image delayed thunk", b=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(784),0xfffffffc)),
            ("direct malformed null descriptor", b=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(536),1))}){
            var bad=Image();change(bad);File.WriteAllBytes(path,bad);bool rejected=false;
            try{_ = NativeImports.Inspect(path);}catch(Exception error)when(error is InvalidDataException or OverflowException){rejected=true;}
            Check(rejected,"Malformed import inventory accepted: "+name);
        }
        return Task.CompletedTask;
    }
}
