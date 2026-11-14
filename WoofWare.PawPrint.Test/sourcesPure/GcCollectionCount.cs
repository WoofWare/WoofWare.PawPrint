// `TraceSource.TraceEvent` is `[Conditional("TRACE")]`, and the harness compiles without it.
#define TRACE

using System;
using System.Diagnostics;
using System.IO;

// `GC.CollectionCount(int)`, and the `_CollectionCount` InternalCall behind it
// (GCInterface::CollectionCount, comutilnative.cpp), directly and through its commonest framework
// caller: every `TraceSource` and `Switch` constructor prunes its cache of weak references only
// when `GC.CollectionCount(2)` has moved since it last looked.
//
// Nothing here asserts a particular count, because a real runtime may well have collected by
// now; it asserts only what holds on any runtime.
public class TestGcCollectionCount
{
    public static int Main(string[] argv)
    {
        // The managed wrapper rejects a negative generation before the InternalCall, which only
        // asserts it.
        try
        {
            GC.CollectionCount(-1);
            return 1;
        }
        catch (ArgumentOutOfRangeException e)
        {
            if (e.ParamName != "generation") return 2;
        }

        // A collection of a generation is counted as a collection of every younger one too
        // (`update_collection_counts`, gc.cpp), so the counts never increase with the generation.
        // Each count only grows over time, so reading the oldest first keeps that true even if
        // a collection falls between two reads.
        int gen2 = GC.CollectionCount(2);
        int gen1 = GC.CollectionCount(1);
        int gen0 = GC.CollectionCount(0);
        if (gen2 < 0) return 3;
        if (gen1 < gen2) return 4;
        if (gen0 < gen1) return 5;

        // The InternalCall does not check the top end: the GC answers 0 for any generation past
        // its maximum, which is 2 on CoreCLR.
        if (GC.CollectionCount(3) != 0) return 6;
        if (GC.CollectionCount(int.MaxValue) != 0) return 7;

        var writer = new StringWriter();
        var source = new TraceSource("PawPrintCollectionCount", SourceLevels.All);
        source.Listeners.Clear();
        source.Listeners.Add(new TextWriterTraceListener(writer));
        source.TraceEvent(TraceEventType.Warning, 7, "observed");
        source.Flush();
        if (writer.ToString() != "PawPrintCollectionCount Warning: 7 : observed" + Environment.NewLine) return 8;

        // A second source prunes again, now with a cache that holds the first.
        var second = new TraceSource("PawPrintCollectionCountSecond", SourceLevels.Off);
        if (second.Switch.Level != SourceLevels.Off) return 9;

        var flag = new BooleanSwitch("PawPrintCollectionCountSwitch", "a switch", "1");
        if (!flag.Enabled) return 10;

        GC.KeepAlive(source);
        return 0;
    }
}
