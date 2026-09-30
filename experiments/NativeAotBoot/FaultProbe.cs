using System.Runtime.CompilerServices;

namespace WitOS.NativeAotBoot;

internal static class FaultProbe
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe int Read(int* address) => *address;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe void Write(int* address, int value) => *address = value;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Divide(int numerator, int denominator) => numerator / denominator;

    private sealed class Root(int value) { internal readonly int Value = value; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Root MakeRoot(int value) => new(value);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Collect(Root root, Exception error)
    {
        int before = GC.CollectionCount(0);
        GC.Collect();
        bool valid = GC.CollectionCount(0) > before && root.Value == 731 && error is not null;
        GC.KeepAlive(error);
        GC.KeepAlive(root);
        return valid;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static unsafe bool Run()
    {
        var root = MakeRoot(731);
        int caught = 0, finalized = 0;
        for (int i = 0; i < 6; ++i)
        {
            int kind = i % 3;
            try
            {
                if (kind == 0) _ = Read((int*)0);
                else if (kind == 1) Write((int*)0, 123);
                else _ = Divide(42, 0);
                return false;
            }
            catch (NullReferenceException error)
            {
                if (kind == 2 || !Collect(root, error)) return false;
                ++caught;
            }
            catch (DivideByZeroException error)
            {
                if (kind != 2 || !Collect(root, error)) return false;
                ++caught;
            }
            finally { ++finalized; }
        }
        GC.KeepAlive(root);
        return caught == 6 && finalized == 6 && root.Value == 731;
    }
}
