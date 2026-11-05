using System.Globalization;

namespace Sellora.Domain.Inventory;

/// <summary>
/// Pure arithmetic over a product's unit hierarchy: every unit points at the unit it contains ("1 Box contains 10
/// Piece"), the base unit contains nothing. <c>factor(base) = 1</c>, <c>factor(u) = u.InnerQuantity × factor(u.Inner)</c>.
/// Factors are computed here once, when the hierarchy is saved, and stored on each row; transactions read and
/// snapshot the stored factor and never recurse.
/// </summary>
public static class ProductUnitGraph
{
    public const decimal MinFactor = 0.000001m;
    public const decimal MaxFactor = 1_000_000_000m;
    public const decimal MaxInnerQuantity = 1_000_000_000m;
    public const int InnerQuantityScale = 6;

    /// <summary>One unit of the graph. <paramref name="InnerKey"/> null means the base unit.</summary>
    public sealed record Node(Guid Key, Guid? InnerKey, decimal InnerQuantity);

    public enum Problem
    {
        None,
        /// <summary>The node is part of a cycle (including containing itself).</summary>
        Cycle,
        /// <summary>The inner key is not a node of the graph.</summary>
        MissingInner,
        /// <summary>InnerQuantity is not in (0, 1e9] or has more than 6 decimal places.</summary>
        InvalidQuantity,
        /// <summary>The factor would exceed 1e9 (Factor is null) or is below 1e-6 (Factor is set).</summary>
        OutOfRange,
        /// <summary>The node's inner chain has one of the problems above further down; nothing to report on this node.</summary>
        InnerUnresolved,
    }

    /// <summary>Factor and depth (base = 0) of a node; both null when the chain to the base cannot be resolved.</summary>
    public sealed record NodeResult(Guid Key, decimal? Factor, int? Depth, Problem Problem);

    /// <summary>Resolves every node to the base. Each node has a single inner, so following inner keys from any node
    /// either reaches a base, a missing key, or loops; nodes on a loop are reported as <see cref="Problem.Cycle"/>,
    /// nodes leading into one as <see cref="Problem.InnerUnresolved"/>. Iterative, so a long or hostile chain cannot
    /// overflow the stack, and guarded so the decimal arithmetic never overflows or underflows to zero.</summary>
    public static IReadOnlyDictionary<Guid, NodeResult> Compute(IEnumerable<Node> nodes)
    {
        var byKey = new Dictionary<Guid, Node>();
        foreach (var node in nodes) byKey.TryAdd(node.Key, node);

        var results = new Dictionary<Guid, NodeResult>(byKey.Count);
        foreach (var start in byKey.Keys)
        {
            if (results.ContainsKey(start)) continue;

            var path = new List<Guid>();
            var onPath = new Dictionary<Guid, int>();
            var current = start;
            NodeResult tail;
            while (true)
            {
                if (results.TryGetValue(current, out var known)) { tail = known; break; }
                if (onPath.TryGetValue(current, out var loopStart))
                {
                    foreach (var key in path.Skip(loopStart)) results[key] = new NodeResult(key, null, null, Problem.Cycle);
                    path.RemoveRange(loopStart, path.Count - loopStart);
                    tail = results[current];
                    break;
                }

                var node = byKey[current];
                if (node.InnerKey is not { } inner)
                {
                    tail = results[current] = new NodeResult(current, 1m, 0, Problem.None);
                    break;
                }

                onPath[current] = path.Count;
                path.Add(current);
                if (!byKey.ContainsKey(inner))
                {
                    path.RemoveAt(path.Count - 1);
                    tail = results[current] = new NodeResult(current, null, null, Problem.MissingInner);
                    break;
                }
                current = inner;
            }

            for (var i = path.Count - 1; i >= 0; i--)
            {
                var key = path[i];
                tail = results[key] = Combine(byKey[key], tail);
            }
        }

        return results;
    }

    private static NodeResult Combine(Node node, NodeResult inner)
    {
        if (!IsValidInnerQuantity(node.InnerQuantity)) return new NodeResult(node.Key, null, null, Problem.InvalidQuantity);
        if (inner.Factor is not { } innerFactor || innerFactor < MinFactor || inner.Depth is not { } innerDepth)
        {
            return new NodeResult(node.Key, null, null, Problem.InnerUnresolved);
        }
        if (node.InnerQuantity > MaxFactor / innerFactor) return new NodeResult(node.Key, null, null, Problem.OutOfRange);

        var factor = node.InnerQuantity * innerFactor;
        return new NodeResult(node.Key, factor, innerDepth + 1, factor < MinFactor ? Problem.OutOfRange : Problem.None);
    }

    /// <summary>0 &lt; quantity ≤ 1e9 with at most 6 decimal places (the column is numeric(18,6)).</summary>
    public static bool IsValidInnerQuantity(decimal quantity) =>
        quantity > 0 && quantity <= MaxInnerQuantity && Math.Round(quantity, InnerQuantityScale) == quantity;

    /// <summary><c>factor = innerQuantity × innerFactor</c>, for builders that add one row at a time (seed, tests).</summary>
    public static decimal Factor(decimal innerQuantity, decimal innerFactor) => innerQuantity * innerFactor;

    /// <summary><c>qty × factor(from) / factor(to)</c>.</summary>
    public static decimal Convert(decimal quantity, decimal fromFactor, decimal toFactor) => quantity * fromFactor / toFactor;

    /// <summary>True when the factor is a whole number of the base unit's smallest step, i.e.
    /// <c>factor × 10^baseDecimalPlaces</c> is an integer (generalised "integer factor over a 0-dp base" rule).</summary>
    public static bool IsWholeForBase(decimal factor, short baseDecimalPlaces) =>
        Math.Round(factor, Math.Clamp((int)baseDecimalPlaces, 0, 28)) == factor;

    /// <summary>Sort position: the base row is 0, then every other row ranked by factor ascending, then depth, then
    /// name. Rows without a factor go last.</summary>
    public static IReadOnlyDictionary<Guid, short> SortOrders(IEnumerable<(Guid Key, bool IsBase, decimal? Factor, int? Depth, string Name)> rows)
    {
        var ordered = rows
            .OrderByDescending(r => r.IsBase)
            .ThenBy(r => r.Factor is null)
            .ThenBy(r => r.Factor ?? 0m)
            .ThenBy(r => r.Depth ?? int.MaxValue)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Key)
            .ToList();
        var result = new Dictionary<Guid, short>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++) result[ordered[i].Key] = (short)Math.Min(i, short.MaxValue);
        return result;
    }

    /// <summary>Equivalence chain from a unit down to the base, e.g. "1 Full Carton = 2 Half Carton = 10 Box = 100 Piece".
    /// <paramref name="innerOf"/> returns the inner key (null at the base), <paramref name="factorOf"/> the factor and
    /// <paramref name="nameOf"/> the display name. Returns null if a factor on the way is unknown.</summary>
    public static string? Chain(Guid key, Func<Guid, Guid?> innerOf, Func<Guid, decimal?> factorOf, Func<Guid, string> nameOf)
    {
        if (factorOf(key) is not { } top) return null;

        var parts = new List<string> { $"1 {nameOf(key)}" };
        var seen = new HashSet<Guid> { key };
        var current = innerOf(key);
        while (current is { } inner)
        {
            if (!seen.Add(inner) || factorOf(inner) is not { } innerFactor || innerFactor == 0) return null;
            parts.Add($"{FormatQuantity(top / innerFactor)} {nameOf(inner)}");
            current = innerOf(inner);
        }
        return string.Join(" = ", parts);
    }

    /// <summary>Invariant, without trailing zeros, at most 10 decimals (e.g. 0.2, 1000, 0.001).</summary>
    public static string FormatQuantity(decimal value) =>
        Math.Round(value, 10).ToString("0.##########", CultureInfo.InvariantCulture);
}
