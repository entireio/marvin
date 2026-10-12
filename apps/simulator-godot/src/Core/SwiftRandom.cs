using System;
using System.Collections.Generic;

namespace Marvin.Core;

/// <summary>Swift <c>RandomNumberGenerator</c>: a source of uniformly distributed 64-bit values.</summary>
public interface RandomNumberGenerator
{
    ulong next();
}

/// <summary>
/// Swift <c>SystemRandomNumberGenerator</c>. PORT: Swift reads the OS CSPRNG (arc4random_buf);
/// unseeded calls are nondeterministic in both games, so this uses <see cref="System.Random.Shared"/>.
/// </summary>
public struct SystemRandomNumberGenerator : RandomNumberGenerator
{
    public SystemRandomNumberGenerator() { }
    public ulong next()
    {
        Span<byte> bytes = stackalloc byte[8];
        System.Random.Shared.NextBytes(bytes);
        return BitConverter.ToUInt64(bytes);
    }
}

/// <summary>
/// Exact ports of the Swift standard library random algorithms (swiftlang/swift main,
/// stdlib/public/core: Random.swift, FloatingPointRandom.swift, Integers.swift, Bool.swift,
/// CollectionAlgorithms.swift, Collection.swift). Seeded generators produce the same sequences
/// as Swift. Generators are passed by <c>ref</c> like Swift's <c>inout</c>; struct generators are
/// not boxed and advance in place.
/// <list type="table">
/// <item><term>Double.random(in: a...b, using: &amp;g)</term><description><see cref="doubleClosed{R}"/></description></item>
/// <item><term>Double.random(in: a..&lt;b, using: &amp;g)</term><description><see cref="doubleHalfOpen{R}"/></description></item>
/// <item><term>Float.random(in:using:)</term><description><see cref="floatClosed{R}"/>, <see cref="floatHalfOpen{R}"/></description></item>
/// <item><term>Int.random(in:using:)</term><description><see cref="intClosed{R}"/>, <see cref="intHalfOpen{R}"/></description></item>
/// <item><term>UInt64.random(in:using:)</term><description><see cref="uint64Closed{R}"/>, <see cref="uint64HalfOpen{R}"/></description></item>
/// <item><term>Bool.random(using:)</term><description><see cref="boolRandom{R}"/></description></item>
/// <item><term>shuffle(using:) / shuffled(using:)</term><description><see cref="shuffle{T,R}"/>, <see cref="shuffled{T,R}"/></description></item>
/// <item><term>randomElement(using:)</term><description><see cref="randomElement{T,R}(IReadOnlyList{T},ref R,out T)"/></description></item>
/// </list>
/// Each also has an unseeded overload (Swift's form without <c>using:</c>) that uses
/// <see cref="SystemRandomNumberGenerator"/>.
/// </summary>
public static class SwiftRandom
{
    private const string EmptyRange = "Can't get random value with an empty range";
    private const string InfiniteRange = "There is no uniform distribution on an infinite range";
    private const string RangeOrder = "Range requires lowerBound <= upperBound";

    // MARK: RandomNumberGenerator extension methods

    /// <summary><c>generator.next() as UInt32</c>: the low 32 bits (<c>truncatingIfNeeded</c>).</summary>
    public static uint nextUInt32<R>(ref R generator) where R : RandomNumberGenerator => unchecked((uint)generator.next());

    /// <summary><c>generator.next(upperBound:)</c> for UInt64: Lemire's nearly divisionless method.</summary>
    public static ulong next<R>(ref R generator, ulong upperBound) where R : RandomNumberGenerator
    {
        Swift.precondition(upperBound != 0, "upperBound cannot be zero.");
        var random = generator.next();
        var high = Math.BigMul(random, upperBound, out var low);
        if (low < upperBound)
        {
            var t = unchecked(0UL - upperBound) % upperBound;
            while (low < t)
            {
                random = generator.next();
                high = Math.BigMul(random, upperBound, out low);
            }
        }
        return high;
    }

    /// <summary><c>generator.next(upperBound:)</c> for UInt32 (each draw uses the low 32 bits of <c>next()</c>).</summary>
    public static uint next<R>(ref R generator, uint upperBound) where R : RandomNumberGenerator
    {
        Swift.precondition(upperBound != 0, "upperBound cannot be zero.");
        var random = nextUInt32(ref generator);
        var m = (ulong)random * upperBound;
        if (unchecked((uint)m) < upperBound)
        {
            var t = unchecked(0u - upperBound) % upperBound;
            while (unchecked((uint)m) < t)
            {
                random = nextUInt32(ref generator);
                m = (ulong)random * upperBound;
            }
        }
        return (uint)(m >> 32);
    }

    // MARK: Integers (Swift Int is 64-bit)

    /// <summary><c>Int.random(in: lowerBound..&lt;upperBound, using:)</c>.</summary>
    public static long intHalfOpen<R>(long lowerBound, long upperBound, ref R generator) where R : RandomNumberGenerator
    {
        Swift.precondition(lowerBound <= upperBound, RangeOrder);
        Swift.precondition(lowerBound != upperBound, EmptyRange);
        var delta = unchecked((ulong)(upperBound - lowerBound));
        return unchecked((long)((ulong)lowerBound + next(ref generator, delta)));
    }

    /// <summary><c>Int.random(in: lowerBound...upperBound, using:)</c>.</summary>
    public static long intClosed<R>(long lowerBound, long upperBound, ref R generator) where R : RandomNumberGenerator
    {
        Swift.precondition(lowerBound <= upperBound, RangeOrder);
        var delta = unchecked((ulong)(upperBound - lowerBound));
        if (delta == ulong.MaxValue) return unchecked((long)generator.next());
        delta += 1;
        return unchecked((long)((ulong)lowerBound + next(ref generator, delta)));
    }

    /// <summary><c>UInt64.random(in: lowerBound..&lt;upperBound, using:)</c>.</summary>
    public static ulong uint64HalfOpen<R>(ulong lowerBound, ulong upperBound, ref R generator) where R : RandomNumberGenerator
    {
        Swift.precondition(lowerBound <= upperBound, RangeOrder);
        Swift.precondition(lowerBound != upperBound, EmptyRange);
        return unchecked(lowerBound + next(ref generator, upperBound - lowerBound));
    }

    /// <summary><c>UInt64.random(in: lowerBound...upperBound, using:)</c>.</summary>
    public static ulong uint64Closed<R>(ulong lowerBound, ulong upperBound, ref R generator) where R : RandomNumberGenerator
    {
        Swift.precondition(lowerBound <= upperBound, RangeOrder);
        var delta = upperBound - lowerBound;
        if (delta == ulong.MaxValue) return generator.next();
        delta += 1;
        return unchecked(lowerBound + next(ref generator, delta));
    }

    // MARK: Floating point (BinaryFloatingPoint.random)

    /// <summary><c>Double.random(in: lowerBound..&lt;upperBound, using:)</c>.</summary>
    public static double doubleHalfOpen<R>(double lowerBound, double upperBound, ref R generator) where R : RandomNumberGenerator
    {
        Swift.precondition(lowerBound <= upperBound, RangeOrder);
        Swift.precondition(lowerBound != upperBound, EmptyRange);
        var delta = upperBound - lowerBound;
        Swift.precondition(double.IsFinite(delta), InfiniteRange);
        while (true)
        {
            // RawSignificand (UInt64) is wider than the 53-bit significand: mask a power of two.
            var rand = generator.next() & ((1UL << 53) - 1);
            var unitRandom = (double)rand * (1.1102230246251565e-16 /* 2^-53 = ulpOfOne/2 */);
            var randFloat = delta * unitRandom + lowerBound;
            if (randFloat == upperBound) continue; // Swift recurses with the same generator.
            return randFloat;
        }
    }

    /// <summary><c>Double.random(in: lowerBound...upperBound, using:)</c>.</summary>
    public static double doubleClosed<R>(double lowerBound, double upperBound, ref R generator) where R : RandomNumberGenerator
    {
        Swift.precondition(lowerBound <= upperBound, RangeOrder);
        var delta = upperBound - lowerBound;
        Swift.precondition(double.IsFinite(delta), InfiniteRange);
        const ulong maxSignificand = 1UL << 53;
        var rand = next(ref generator, maxSignificand + 1);
        if (rand == maxSignificand) return upperBound;
        var unitRandom = (double)rand * (1.1102230246251565e-16 /* 2^-53 = ulpOfOne/2 */);
        return delta * unitRandom + lowerBound;
    }

    /// <summary><c>Float.random(in: lowerBound..&lt;upperBound, using:)</c>.</summary>
    public static float floatHalfOpen<R>(float lowerBound, float upperBound, ref R generator) where R : RandomNumberGenerator
    {
        Swift.precondition(lowerBound <= upperBound, RangeOrder);
        Swift.precondition(lowerBound != upperBound, EmptyRange);
        var delta = upperBound - lowerBound;
        Swift.precondition(float.IsFinite(delta), InfiniteRange);
        while (true)
        {
            // RawSignificand is UInt32: next() truncated to 32 bits, masked to the 24-bit significand.
            var rand = nextUInt32(ref generator) & ((1u << 24) - 1);
            var unitRandom = (float)rand * (5.9604645e-08f /* 2^-24 = Float.ulpOfOne/2 */);
            var randFloat = delta * unitRandom + lowerBound;
            if (randFloat == upperBound) continue;
            return randFloat;
        }
    }

    /// <summary><c>Float.random(in: lowerBound...upperBound, using:)</c>.</summary>
    public static float floatClosed<R>(float lowerBound, float upperBound, ref R generator) where R : RandomNumberGenerator
    {
        Swift.precondition(lowerBound <= upperBound, RangeOrder);
        var delta = upperBound - lowerBound;
        Swift.precondition(float.IsFinite(delta), InfiniteRange);
        const uint maxSignificand = 1u << 24;
        var rand = next(ref generator, maxSignificand + 1);
        if (rand == maxSignificand) return upperBound;
        var unitRandom = (float)rand * (5.9604645e-08f /* 2^-24 = Float.ulpOfOne/2 */);
        return delta * unitRandom + lowerBound;
    }

    // MARK: Bool

    /// <summary><c>Bool.random(using:)</c>: <c>(generator.next() &gt;&gt; 17) &amp; 1 == 0</c>.</summary>
    public static bool boolRandom<R>(ref R generator) where R : RandomNumberGenerator => ((generator.next() >> 17) & 1) == 0;

    // MARK: Collections

    /// <summary><c>MutableCollection.shuffle(using:)</c> (Fisher-Yates from the front).</summary>
    public static void shuffle<T, R>(IList<T> collection, ref R generator) where R : RandomNumberGenerator
    {
        var count = collection.Count;
        if (count <= 1) return;
        var amount = count;
        var currentIndex = 0;
        while (amount > 1)
        {
            var random = (int)intHalfOpen(0, amount, ref generator);
            amount -= 1;
            var other = currentIndex + random;
            if (other != currentIndex) (collection[currentIndex], collection[other]) = (collection[other], collection[currentIndex]);
            currentIndex += 1;
        }
    }

    /// <summary><c>Sequence.shuffled(using:)</c>: a shuffled copy.</summary>
    public static T[] shuffled<T, R>(IEnumerable<T> sequence, ref R generator) where R : RandomNumberGenerator
    {
        var result = new List<T>(sequence).ToArray();
        shuffle(result, ref generator);
        return result;
    }

    /// <summary><c>Collection.randomElement(using:)</c>; false (Swift <c>nil</c>) when empty.</summary>
    public static bool randomElement<T, R>(IReadOnlyList<T> collection, ref R generator, out T element) where R : RandomNumberGenerator
    {
        if (collection.Count == 0) { element = default; return false; }
        var random = (int)intHalfOpen(0, collection.Count, ref generator);
        element = collection[random];
        return true;
    }

    /// <summary><c>Collection.randomElement(using:)!</c>; throws when empty.</summary>
    public static T randomElement<T, R>(IReadOnlyList<T> collection, ref R generator) where R : RandomNumberGenerator
    {
        if (!randomElement(collection, ref generator, out var element)) throw new InvalidOperationException("Unexpectedly found nil while unwrapping an Optional value");
        return element;
    }

    // MARK: Unseeded forms (SystemRandomNumberGenerator)

    public static long intHalfOpen(long lowerBound, long upperBound) { var g = new SystemRandomNumberGenerator(); return intHalfOpen(lowerBound, upperBound, ref g); }
    public static long intClosed(long lowerBound, long upperBound) { var g = new SystemRandomNumberGenerator(); return intClosed(lowerBound, upperBound, ref g); }
    public static ulong uint64HalfOpen(ulong lowerBound, ulong upperBound) { var g = new SystemRandomNumberGenerator(); return uint64HalfOpen(lowerBound, upperBound, ref g); }
    public static ulong uint64Closed(ulong lowerBound, ulong upperBound) { var g = new SystemRandomNumberGenerator(); return uint64Closed(lowerBound, upperBound, ref g); }
    public static double doubleHalfOpen(double lowerBound, double upperBound) { var g = new SystemRandomNumberGenerator(); return doubleHalfOpen(lowerBound, upperBound, ref g); }
    public static double doubleClosed(double lowerBound, double upperBound) { var g = new SystemRandomNumberGenerator(); return doubleClosed(lowerBound, upperBound, ref g); }
    public static float floatHalfOpen(float lowerBound, float upperBound) { var g = new SystemRandomNumberGenerator(); return floatHalfOpen(lowerBound, upperBound, ref g); }
    public static float floatClosed(float lowerBound, float upperBound) { var g = new SystemRandomNumberGenerator(); return floatClosed(lowerBound, upperBound, ref g); }
    public static bool boolRandom() { var g = new SystemRandomNumberGenerator(); return boolRandom(ref g); }
    public static void shuffle<T>(IList<T> collection) { var g = new SystemRandomNumberGenerator(); shuffle(collection, ref g); }
    public static T[] shuffled<T>(IEnumerable<T> sequence) { var g = new SystemRandomNumberGenerator(); return shuffled(sequence, ref g); }
    public static bool randomElement<T>(IReadOnlyList<T> collection, out T element) { var g = new SystemRandomNumberGenerator(); return randomElement(collection, ref g, out element); }
    public static T randomElement<T>(IReadOnlyList<T> collection) { var g = new SystemRandomNumberGenerator(); return randomElement(collection, ref g); }
}
