using System.Runtime.CompilerServices;

namespace WitOS.Experiments;

// Independently authored port-boundary checks. Standard upstream CoreLib only;
// they execute on the hosted reference until the guest runtime exists.
internal static class RuntimeBoundaryChecks
{
    private sealed class Payload(int id)
    {
        internal readonly int Id = id;
        internal Payload? Link;
    }

    private class BaseRoot
    {
        internal Payload? Inherited;
    }

    private sealed class DerivedRoot : BaseRoot
    {
        internal Envelope Nested;
    }

    private struct Envelope
    {
        internal Payload? Item;
        internal long Stamp;
    }

    private static class ClosedRoots<T>
    {
        internal static Envelope Slot;
    }

    private sealed class FirstTag;
    private sealed class SecondTag;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void CompositeRoots()
    {
        var root = MakeGraph();

        var matrix = new Envelope[3, 2];
        for (var row = 0; row < 3; ++row)
            for (var column = 0; column < 2; ++column)
                matrix[row, column] = new Envelope { Item = new Payload(400 + row * 2 + column), Stamp = row + column };
        var jagged = MakeJaggedRoots();
        ClosedRoots<FirstTag>.Slot = new Envelope { Item = new Payload(501), Stamp = 11 };
        ClosedRoots<SecondTag>.Slot = new Envelope { Item = new Payload(502), Stamp = 22 };

        try
        {
            ref var interior = ref MakeInteriorRoot();
            Collect();
            Require(root.Inherited!.Id == 301 && root.Nested.Item!.Id == 302 &&
                root.Nested.Stamp == 0x1122334455667788 &&
                root.Nested.Item.Link!.Id == 303 && ReferenceEquals(root.Nested.Item.Link.Link, root.Nested.Item), "inherited/nested/cyclic roots");
            for (var row = 0; row < 3; ++row)
                for (var column = 0; column < 2; ++column)
                    Require(matrix[row, column].Item!.Id == 400 + row * 2 + column &&
                        matrix[row, column].Stamp == row + column, "multidimensional struct roots");
            Require(jagged[0][1] is null && jagged[0][0]!.Id == 801 &&
                jagged[1][0]!.Id == 802, "jagged roots");
            Require(ClosedRoots<FirstTag>.Slot.Item!.Id == 501 && ClosedRoots<FirstTag>.Slot.Stamp == 11 &&
                ClosedRoots<SecondTag>.Slot.Item!.Id == 502 && ClosedRoots<SecondTag>.Slot.Stamp == 22,
                "closed generic static roots");
            Require(interior.Item!.Id == 601 && interior.Stamp == 33, "interior ref root");
            interior.Item = new Payload(602);
            interior.Stamp = 34;
            Collect();
            Require(interior.Item.Id == 602 && interior.Stamp == 34, "updated interior ref root");
            GC.KeepAlive(root);
            GC.KeepAlive(matrix);
            GC.KeepAlive(jagged);
        }
        finally
        {
            ClosedRoots<FirstTag>.Slot = default;
            ClosedRoots<SecondTag>.Slot = default;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static DerivedRoot MakeGraph()
    {
        var first = new Payload(302);
        first.Link = new Payload(303) { Link = first };
        return new DerivedRoot
        {
            Inherited = new Payload(301),
            Nested = new Envelope { Item = first, Stamp = 0x1122334455667788 }
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ref Envelope MakeInteriorRoot()
    {
        var array = new Envelope[19];
        array[7] = new Envelope { Item = new Payload(601), Stamp = 33 };
        // The caller receives only a managed interior ref, never the array.
        return ref array[7];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Payload?[][] MakeJaggedRoots() =>
        [new Payload?[] { new Payload(801), null }, new Payload?[] { new Payload(802) }];

    private sealed class Signal(Payload payload) : Exception
    {
        internal readonly Payload Payload = payload;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void UnwindRoots()
    {
        var live = new Payload(701) { Link = new Payload(702) };
        var signal = new Signal(live);
        var order = new List<int>();
        var caught = false;
        try
        {
            ThrowThroughCleanup(signal, live, order);
        }
        catch (Signal error) when (Filter(error, live, order))
        {
            // Filters run on the first pass, before inner finally cleanup.
            Require(order.Count == 2 && order[0] == 1 && order[1] == 2, "filter/finally order");
            Collect();
            Require(ReferenceEquals(error, signal) && ReferenceEquals(error.Payload, live) &&
                live.Link!.Id == 702, "exception/local roots in catch");
            order.Add(3);
            caught = true;
        }
        finally
        {
            Collect();
            Require(live.Id == 701 && live.Link!.Id == 702, "roots after unwind");
            order.Add(4);
        }
        Require(caught && order.Count == 4 && order[3] == 4, "unwind completion");

        try { RethrowAfterInnerException(signal); }
        catch (Signal error)
        {
            Require(ReferenceEquals(error, signal) && error.Payload.Link!.Id == 702, "rethrow identity/root");
            GC.KeepAlive(signal);
            GC.KeepAlive(live);
            return;
        }
        throw new InvalidOperationException("Expected rethrow did not occur.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowThroughCleanup(Signal signal, Payload root, List<int> order)
    {
        var local = new Envelope { Item = new Payload(703), Stamp = 73 };
        try { throw signal; }
        finally
        {
            Collect();
            Require(local.Item!.Id == 703 && local.Stamp == 73 && root.Id == 701, "roots in finally funclet");
            order.Add(2);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Filter(Signal signal, Payload root, List<int> order)
    {
        Collect();
        Require(ReferenceEquals(signal.Payload, root) && root.Link!.Id == 702 && order.Count == 0,
            "roots in exception filter");
        order.Add(1);
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RethrowAfterInnerException(Signal signal)
    {
        try { throw signal; }
        catch (Signal)
        {
            try { throw new ArgumentException("inner marker"); }
            catch (ArgumentException) { Collect(); }
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Collect()
    {
        var before = GC.CollectionCount(GC.MaxGeneration);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        Require(GC.CollectionCount(GC.MaxGeneration) > before, "collection did not run");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Runtime boundary: " + name);
    }
}
