using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Hoard.Desktop.Infrastructure;

/// <summary>
/// Debug-only leak tripwire. Heavy per-screen objects (a Board page VM, its view) <see cref="Track"/> themselves at
/// construction and <see cref="MarkDead"/> themselves when their life is over (VM disposed, view detached). On each
/// new construction the canary counts same-type instances that are <b>dead-but-still-reachable</b> — the only state
/// that actually means "leak". Counting merely-alive instances would cry wolf: the nav stack legitimately keeps every
/// drilled-into ancestor board alive (folders nest to any depth), and a freshly-detached view is garbage that simply
/// hasn't been collected yet. To rule the latter out, a forced full GC runs before warning — but only on the rare
/// near-trip path, and only in DEBUG builds. A warning therefore means: an object whose life ended is still strongly
/// rooted after collection — the masonry-board leak signature — caught the day it's introduced.
///
/// Weak references only (the canary can never cause retention), and every call compiles out of release builds via
/// <see cref="ConditionalAttribute"/>.
/// </summary>
internal static class LeakCanary
{
    private const int DeadAliveWarnThreshold = 2; // one straggler mid-teardown can be timing; two is a pattern
    private static readonly object Gate = new();
    private static readonly List<Entry> Tracked = new();

    private sealed class Entry(object instance)
    {
        public readonly WeakReference Ref = new(instance);
        public readonly string Type = instance.GetType().Name;
        public bool Dead; // life ended (disposed/detached) — should be collectable now
    }

    /// <summary>Register a newly-constructed heavy object and check whether prior DEAD instances are leaking.</summary>
    [Conditional("DEBUG")]
    public static void Track(object instance)
    {
        string type;
        int deadAlive;
        lock (Gate)
        {
            Tracked.RemoveAll(e => !e.Ref.IsAlive);
            type = instance.GetType().Name;
            deadAlive = Tracked.Count(e => e.Dead && e.Type == type);
            Tracked.Add(new Entry(instance));
        }
        if (deadAlive < DeadAliveWarnThreshold) return;

        // Near-trip: most "dead but alive" entries are just garbage awaiting collection. Settle the question with a
        // real collection (blocking, finalizers drained) before accusing anything — rare path, DEBUG-only.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        lock (Gate)
        {
            Tracked.RemoveAll(e => !e.Ref.IsAlive);
            deadAlive = Tracked.Count(e => e.Dead && e.Type == type);
        }
        if (deadAlive >= DeadAliveWarnThreshold)
            Serilog.Log.Warning(
                "[LEAK-CANARY] {Count} {Type} instance(s) survived a full GC after their life ended (disposed/" +
                "detached) — something is still rooting dead screens", deadAlive, type);
    }

    /// <summary>Mark a tracked object's life as over (VM disposed / view detached): from now on it surviving GC is
    /// evidence of a leak, not of legitimate use.</summary>
    [Conditional("DEBUG")]
    public static void MarkDead(object instance)
    {
        lock (Gate)
        {
            foreach (var e in Tracked)
                if (ReferenceEquals(e.Ref.Target, instance)) { e.Dead = true; return; }
        }
    }
}
