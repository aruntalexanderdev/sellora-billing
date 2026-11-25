using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>One document line as the tax reports see it (TP-5a). Built in SQL: the per-line sums come from correlated sub-selects over the line's tax rows, so each line
/// counts once (taxable value) whatever its number of rows; the reports then GROUP BY in SQL. <see cref="Treatment"/> null marks a legacy line (its one row is the unsplit history row).</summary>
internal sealed class TaxLineFact
{
    public Guid DocId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? StateId { get; set; }
    public PlaceOfSupply? Place { get; set; }
    public TaxTreatment? Treatment { get; set; }

    /// <summary>Σ percent rates of the rate rows: CGST, SGST, UTGST, IGST, VAT, and the one legacy / zero-treatment row (rate 0 for treatments). Cess is reported as an amount.</summary>
    public decimal Rate { get; set; }
    public string? Hsn { get; set; }
    public Guid BaseUnitId { get; set; }
    public decimal BaseQty { get; set; }
    public decimal Taxable { get; set; }
    public decimal Cgst { get; set; }
    public decimal Sgst { get; set; }
    public decimal Utgst { get; set; }
    public decimal Igst { get; set; }
    public decimal Cess { get; set; }

    /// <summary>The line's whole tax (header amount): the unsplit amount of a legacy line.</summary>
    public decimal Tax { get; set; }
}

/// <summary>A GROUP BY result of <see cref="TaxReportQueries.GroupForDocuments"/>.</summary>
internal sealed class TaxDocumentGroup
{
    public Guid BranchId { get; set; }
    public Guid? StateId { get; set; }
    public PlaceOfSupply? Place { get; set; }
    public TaxTreatment? Treatment { get; set; }
    public decimal Rate { get; set; }
    public decimal Taxable { get; set; }
    public decimal Cgst { get; set; }
    public decimal Sgst { get; set; }
    public decimal Utgst { get; set; }
    public decimal Igst { get; set; }
    public decimal Cess { get; set; }
    public decimal Tax { get; set; }
    public int Documents { get; set; }
}

/// <summary>A GROUP BY result of <see cref="TaxReportQueries.GroupForHsn"/>.</summary>
internal sealed class TaxHsnGroup
{
    public string? Hsn { get; set; }
    public Guid BaseUnitId { get; set; }
    public decimal Rate { get; set; }
    public decimal BaseQty { get; set; }
    public decimal Taxable { get; set; }
    public decimal Cgst { get; set; }
    public decimal Sgst { get; set; }
    public decimal Utgst { get; set; }
    public decimal Igst { get; set; }
    public decimal Cess { get; set; }
    public decimal Tax { get; set; }
    public decimal LegacyTax { get; set; }
}

/// <summary>The tax rows of one line, summed (see <see cref="TaxReportQueries.AggregateRows{TRow}"/>).</summary>
internal sealed class LineRowAgg
{
    public Guid LineId { get; set; }
    public TaxTreatment? Treatment { get; set; }
    public decimal Rate { get; set; }
    public decimal Cgst { get; set; }
    public decimal Sgst { get; set; }
    public decimal Utgst { get; set; }
    public decimal Igst { get; set; }
    public decimal Cess { get; set; }
}

/// <summary>
/// The line sources of the tax reports (TP-5a): sales lines and purchase lines by the header date, return lines by the return date, cancelled documents excluded, base-currency
/// amounts (purchase lines and their rows are stored in the base currency). Return lines take place of supply, state, GSTIN branch and HSN from the original line. Query filters
/// (company, branch read scope) stay on; <paramref name="branchIds"/> narrows to the branches of one tax regime (and to the requested branch).
/// Each source aggregates the period's tax rows per line once (a hash aggregate) and left-joins that to the lines: one pass over the rows, not a sub-select per line.
/// </summary>
internal static class TaxReportQueries
{
    /// <summary>The rows of the lines in scope, summed per line. <see cref="LineRowAgg.Treatment"/> is the one treatment all rows of a line share (null = legacy row);
    /// <see cref="LineRowAgg.Rate"/> is the sum of the percent rates of the CGST, SGST, UTGST, IGST and VAT rows and of the one legacy row (treatment rows carry rate 0); cess stays an amount.</summary>
    private static IQueryable<LineRowAgg> AggregateRows<TRow>(IQueryable<TRow> rows, System.Linq.Expressions.Expression<Func<TRow, Guid>> lineId) where TRow : LineTaxBase =>
        rows.GroupBy(lineId).Select(g => new LineRowAgg
        {
            LineId = g.Key,
            Treatment = g.Max(t => t.Treatment),
            Rate = g.Sum(t => t.Method == TaxCalculationMethod.Percent
                && (t.Kind == TaxComponentKind.Cgst || t.Kind == TaxComponentKind.Sgst || t.Kind == TaxComponentKind.Utgst || t.Kind == TaxComponentKind.Igst
                    || t.Kind == TaxComponentKind.Vat || t.TaxComponentId == null) ? (t.RatePercent ?? 0m) : 0m),
            Cgst = g.Sum(t => t.Kind == TaxComponentKind.Cgst && t.TaxComponentId != null ? t.TaxAmount : 0m),
            Sgst = g.Sum(t => t.Kind == TaxComponentKind.Sgst && t.TaxComponentId != null ? t.TaxAmount : 0m),
            Utgst = g.Sum(t => t.Kind == TaxComponentKind.Utgst && t.TaxComponentId != null ? t.TaxAmount : 0m),
            Igst = g.Sum(t => t.Kind == TaxComponentKind.Igst && t.TaxComponentId != null ? t.TaxAmount : 0m),
            Cess = g.Sum(t => t.Kind == TaxComponentKind.Cess && t.TaxComponentId != null ? t.TaxAmount : 0m),
        });

    public static IQueryable<TaxLineFact> SalesLines(SelloraDbContext db, DateOnly dateFrom, DateOnly dateTo, IReadOnlyCollection<Guid> branchIds)
    {
        var rows = AggregateRows(
            db.SalesInvoiceLineTaxes.Where(t => t.Line.SalesInvoice.Status != SalesStatus.Cancelled && t.Line.SalesInvoice.InvoiceDate >= dateFrom && t.Line.SalesInvoice.InvoiceDate <= dateTo
                && branchIds.Contains(t.Line.SalesInvoice.BranchId)),
            t => t.SalesInvoiceLineId);
        return from l in db.SalesInvoiceLines
                .Where(l => l.SalesInvoice.Status != SalesStatus.Cancelled && l.SalesInvoice.InvoiceDate >= dateFrom && l.SalesInvoice.InvoiceDate <= dateTo && branchIds.Contains(l.SalesInvoice.BranchId))
            join a in rows on l.Id equals a.LineId into aggs
            from a in aggs.DefaultIfEmpty()
            select new TaxLineFact
            {
                DocId = l.SalesInvoiceId,
                BranchId = l.SalesInvoice.BranchId,
                StateId = l.SalesInvoice.PlaceOfSupplyStateId,
                Place = l.PlaceOfSupply,
                Treatment = a.Treatment,
                Rate = (decimal?)a.Rate ?? 0m,
                Hsn = l.HsnCode,
                BaseUnitId = l.Product.BaseUnitId,
                BaseQty = l.BaseQty,
                Taxable = l.TaxableValue,
                Cgst = (decimal?)a.Cgst ?? 0m,
                Sgst = (decimal?)a.Sgst ?? 0m,
                Utgst = (decimal?)a.Utgst ?? 0m,
                Igst = (decimal?)a.Igst ?? 0m,
                Cess = (decimal?)a.Cess ?? 0m,
                Tax = l.TaxAmount,
            };
    }

    public static IQueryable<TaxLineFact> SalesReturnLines(SelloraDbContext db, DateOnly dateFrom, DateOnly dateTo, IReadOnlyCollection<Guid> branchIds)
    {
        var rows = AggregateRows(
            db.SalesReturnLineTaxes.Where(t => t.Line.SalesReturn.ReturnDate >= dateFrom && t.Line.SalesReturn.ReturnDate <= dateTo && branchIds.Contains(t.Line.SalesInvoiceLine.SalesInvoice.BranchId)),
            t => t.SalesReturnLineId);
        return from l in db.SalesReturnLines
                .Where(l => l.SalesReturn.ReturnDate >= dateFrom && l.SalesReturn.ReturnDate <= dateTo && branchIds.Contains(l.SalesInvoiceLine.SalesInvoice.BranchId))
            join a in rows on l.Id equals a.LineId into aggs
            from a in aggs.DefaultIfEmpty()
            select new TaxLineFact
            {
                DocId = l.SalesReturnId,
                BranchId = l.SalesInvoiceLine.SalesInvoice.BranchId,
                StateId = l.SalesInvoiceLine.SalesInvoice.PlaceOfSupplyStateId,
                Place = l.PlaceOfSupply ?? l.SalesInvoiceLine.PlaceOfSupply,
                Treatment = a.Treatment,
                Rate = (decimal?)a.Rate ?? 0m,
                Hsn = l.SalesInvoiceLine.HsnCode,
                BaseUnitId = l.Product.BaseUnitId,
                BaseQty = l.BaseQty,
                Taxable = l.TaxableValue,
                Cgst = (decimal?)a.Cgst ?? 0m,
                Sgst = (decimal?)a.Sgst ?? 0m,
                Utgst = (decimal?)a.Utgst ?? 0m,
                Igst = (decimal?)a.Igst ?? 0m,
                Cess = (decimal?)a.Cess ?? 0m,
                Tax = l.TaxAmount,
            };
    }

    public static IQueryable<TaxLineFact> PurchaseLines(SelloraDbContext db, DateOnly dateFrom, DateOnly dateTo, IReadOnlyCollection<Guid> branchIds)
    {
        var rows = AggregateRows(
            db.PurchaseOrderLineTaxes.Where(t => t.Line.PurchaseOrder.Status != PurchaseStatus.Cancelled && t.Line.PurchaseOrder.PurchaseDate >= dateFrom && t.Line.PurchaseOrder.PurchaseDate <= dateTo
                && branchIds.Contains(t.Line.PurchaseOrder.BranchId)),
            t => t.PurchaseOrderLineId);
        return from l in db.PurchaseOrderLines
                .Where(l => l.PurchaseOrder.Status != PurchaseStatus.Cancelled && l.PurchaseOrder.PurchaseDate >= dateFrom && l.PurchaseOrder.PurchaseDate <= dateTo && branchIds.Contains(l.PurchaseOrder.BranchId))
            join a in rows on l.Id equals a.LineId into aggs
            from a in aggs.DefaultIfEmpty()
            select new TaxLineFact
            {
                DocId = l.PurchaseOrderId,
                BranchId = l.PurchaseOrder.BranchId,
                StateId = l.PurchaseOrder.PlaceOfSupplyStateId,
                Place = l.PlaceOfSupply,
                Treatment = a.Treatment,
                Rate = (decimal?)a.Rate ?? 0m,
                Hsn = l.HsnCode,
                BaseUnitId = l.Product.BaseUnitId,
                BaseQty = l.BaseQty,
                Taxable = l.TaxableValue,
                Cgst = (decimal?)a.Cgst ?? 0m,
                Sgst = (decimal?)a.Sgst ?? 0m,
                Utgst = (decimal?)a.Utgst ?? 0m,
                Igst = (decimal?)a.Igst ?? 0m,
                Cess = (decimal?)a.Cess ?? 0m,
                Tax = l.TaxAmount,
            };
    }

    public static IQueryable<TaxLineFact> PurchaseReturnLines(SelloraDbContext db, DateOnly dateFrom, DateOnly dateTo, IReadOnlyCollection<Guid> branchIds)
    {
        var rows = AggregateRows(
            db.PurchaseReturnLineTaxes.Where(t => t.Line.PurchaseReturn.ReturnDate >= dateFrom && t.Line.PurchaseReturn.ReturnDate <= dateTo && branchIds.Contains(t.Line.PurchaseOrderLine.PurchaseOrder.BranchId)),
            t => t.PurchaseReturnLineId);
        return from l in db.PurchaseReturnLines
                .Where(l => l.PurchaseReturn.ReturnDate >= dateFrom && l.PurchaseReturn.ReturnDate <= dateTo && branchIds.Contains(l.PurchaseOrderLine.PurchaseOrder.BranchId))
            join a in rows on l.Id equals a.LineId into aggs
            from a in aggs.DefaultIfEmpty()
            select new TaxLineFact
            {
                DocId = l.PurchaseReturnId,
                BranchId = l.PurchaseOrderLine.PurchaseOrder.BranchId,
                StateId = l.PurchaseOrderLine.PurchaseOrder.PlaceOfSupplyStateId,
                Place = l.PlaceOfSupply ?? l.PurchaseOrderLine.PlaceOfSupply,
                Treatment = a.Treatment,
                Rate = (decimal?)a.Rate ?? 0m,
                Hsn = l.PurchaseOrderLine.HsnCode,
                BaseUnitId = l.Product.BaseUnitId,
                BaseQty = l.BaseQty,
                Taxable = l.TaxableValue,
                Cgst = (decimal?)a.Cgst ?? 0m,
                Sgst = (decimal?)a.Sgst ?? 0m,
                Utgst = (decimal?)a.Utgst ?? 0m,
                Igst = (decimal?)a.Igst ?? 0m,
                Cess = (decimal?)a.Cess ?? 0m,
                Tax = l.TaxAmount,
            };
    }

    /// <summary>GROUP BY branch x state x supply type x treatment x rate: the shape of the GST and VAT summaries. One SQL statement.</summary>
    public static Task<List<TaxDocumentGroup>> GroupForDocuments(IQueryable<TaxLineFact> facts, CancellationToken ct) =>
        facts.GroupBy(f => new { f.BranchId, f.StateId, f.Place, f.Treatment, f.Rate })
            .Select(g => new TaxDocumentGroup
            {
                BranchId = g.Key.BranchId, StateId = g.Key.StateId, Place = g.Key.Place, Treatment = g.Key.Treatment, Rate = g.Key.Rate,
                Taxable = g.Sum(f => f.Taxable), Cgst = g.Sum(f => f.Cgst), Sgst = g.Sum(f => f.Sgst), Utgst = g.Sum(f => f.Utgst), Igst = g.Sum(f => f.Igst),
                Cess = g.Sum(f => f.Cess), Tax = g.Sum(f => f.Tax),
                Documents = g.Select(f => f.DocId).Distinct().Count(),
            })
            .ToListAsync(ct);

    /// <summary>GROUP BY HSN x base unit x rate: the shape of the HSN summary. One SQL statement.</summary>
    public static Task<List<TaxHsnGroup>> GroupForHsn(IQueryable<TaxLineFact> facts, CancellationToken ct) =>
        facts.GroupBy(f => new { f.Hsn, f.BaseUnitId, f.Rate })
            .Select(g => new TaxHsnGroup
            {
                Hsn = g.Key.Hsn, BaseUnitId = g.Key.BaseUnitId, Rate = g.Key.Rate,
                BaseQty = g.Sum(f => f.BaseQty), Taxable = g.Sum(f => f.Taxable), Cgst = g.Sum(f => f.Cgst), Sgst = g.Sum(f => f.Sgst), Utgst = g.Sum(f => f.Utgst),
                Igst = g.Sum(f => f.Igst), Cess = g.Sum(f => f.Cess), Tax = g.Sum(f => f.Tax),
                LegacyTax = g.Sum(f => f.Treatment == null ? f.Tax : 0m),
            })
            .ToListAsync(ct);

    /// <summary>Distinct documents per branch from the headers (sales invoices or purchases by date, returns by return date; cancelled excluded).</summary>
    public static async Task<Dictionary<Guid, int>> DocumentCountsAsync(SelloraDbContext db, TaxDocumentKind kind, DateOnly dateFrom, DateOnly dateTo, IReadOnlyCollection<Guid> branchIds, CancellationToken ct)
    {
        var rows = kind switch
        {
            TaxDocumentKind.Sale => await db.SalesInvoices.Where(d => d.Status != SalesStatus.Cancelled && d.InvoiceDate >= dateFrom && d.InvoiceDate <= dateTo && branchIds.Contains(d.BranchId))
                .GroupBy(d => d.BranchId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct),
            TaxDocumentKind.SaleReturn => await db.SalesReturns.Where(d => d.ReturnDate >= dateFrom && d.ReturnDate <= dateTo && branchIds.Contains(d.SalesInvoice.BranchId))
                .GroupBy(d => d.SalesInvoice.BranchId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct),
            TaxDocumentKind.Purchase => await db.PurchaseOrders.Where(d => d.Status != PurchaseStatus.Cancelled && d.PurchaseDate >= dateFrom && d.PurchaseDate <= dateTo && branchIds.Contains(d.BranchId))
                .GroupBy(d => d.BranchId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct),
            _ => await db.PurchaseReturns.Where(d => d.ReturnDate >= dateFrom && d.ReturnDate <= dateTo && branchIds.Contains(d.PurchaseOrder.BranchId))
                .GroupBy(d => d.PurchaseOrder.BranchId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct),
        };
        return rows.ToDictionary(r => r.Key, r => r.N);
    }
}

internal enum TaxDocumentKind
{
    Sale,
    SaleReturn,
    Purchase,
    PurchaseReturn,
}
