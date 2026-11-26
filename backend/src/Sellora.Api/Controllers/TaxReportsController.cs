using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Authorization;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>
/// TP-5a tax reports (docs/features/tax-profiles.md, "TP-5 design"): the GST summary (GSTR-1 / GSTR-3B shaped), the HSN summary (GSTR-1 table 12) and the VAT summary.
/// Read-only. Documents are taken by their header date (returns by return date), cancelled ones are left out, amounts are in the base currency, and each line's taxable value is
/// counted once. Branches of a GST country feed the GST and HSN reports, branches of any other country the VAT report. Everything is grouped in SQL.
/// </summary>
[ApiController]
[Route("api/v1/reports/tax")]
[Authorize(Policy = "Reports.Tax")]
[CompanyScopeRead]
public class TaxReportsController(SelloraDbContext db) : ControllerBase
{
    private const int MaxDays = 366;

    private static readonly TaxReportLimitationDto NoPartyGstin = new("NoPartyGstin",
        "Customers carry no GSTIN yet, so every outward supply is shown as B2C (GSTR-1 B2CS). B2B invoices cannot be separated.");
    private static readonly TaxReportLimitationDto InputTaxAsInvoiced = new("InputTaxAsInvoiced",
        "Input tax is shown as invoiced on purchases, not as eligible ITC. Apply the eligibility and reversal rules when filing GSTR-3B.");
    private static readonly TaxReportLimitationDto NoPartyTaxRegistration = new("NoPartyTaxRegistration",
        "Customers and suppliers carry no tax registration number yet, so supplies cannot be split into B2B and B2C.");
    private static readonly TaxReportLimitationDto HsnNotRecorded = new("HsnNotRecorded",
        "Some lines predate HSN snapshots (or the product had none) and are listed under \"HSN not recorded\".");

    [HttpGet("gst-summary")]
    public async Task<ActionResult<GstSummaryDto>> GstSummary(
        [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo, [FromQuery] Guid? branchId, CancellationToken ct)
    {
        if (ValidatePeriod(dateFrom, dateTo) is { } invalid) return invalid;
        if (await this.DenyUnlessAllowedAsync(db, branchId, ct) is { } denied) return denied;
        var (from, to) = (dateFrom!.Value, dateTo!.Value);

        var context = await LoadContextAsync(wantGst: true, branchId, ct);
        var ids = context.BranchIds;
        var sections = new (GstReportSection Section, TaxDocumentKind Kind, IQueryable<TaxLineFact> Facts)[]
        {
            (GstReportSection.Outward, TaxDocumentKind.Sale, TaxReportQueries.SalesLines(db, from, to, ids)),
            (GstReportSection.OutwardReturn, TaxDocumentKind.SaleReturn, TaxReportQueries.SalesReturnLines(db, from, to, ids)),
            (GstReportSection.Inward, TaxDocumentKind.Purchase, TaxReportQueries.PurchaseLines(db, from, to, ids)),
            (GstReportSection.InwardReturn, TaxDocumentKind.PurchaseReturn, TaxReportQueries.PurchaseReturnLines(db, from, to, ids)),
        };

        var groups = new Dictionary<GstReportSection, List<TaxDocumentGroup>>();
        var documents = new Dictionary<GstReportSection, Dictionary<Guid, int>>();
        foreach (var (section, kind, facts) in sections)
        {
            groups[section] = ids.Count == 0 ? [] : await TaxReportQueries.GroupForDocuments(facts, ct);
            documents[section] = ids.Count == 0 ? [] : await TaxReportQueries.DocumentCountsAsync(db, kind, from, to, ids, ct);
        }

        var stateIds = groups.Values.SelectMany(g => g).Where(g => g.StateId != null).Select(g => g.StateId!.Value).Distinct().ToList();
        var states = await LoadStatesAsync(stateIds, ct);

        // Merge the branches that share a GSTIN: a row is section x GSTIN x state x supply type x treatment x rate.
        var rows = new List<GstSummaryRowDto>();
        foreach (var (section, list) in groups)
        {
            rows.AddRange(list
                .GroupBy(g => (Gstin: context.GstinOf(g.BranchId), g.StateId, g.Place, g.Treatment, g.Rate))
                .Select(m =>
                {
                    var legacy = m.Key.Treatment is null;
                    var state = m.Key.StateId is { } sid && states.TryGetValue(sid, out var s) ? s : (Name: (string?)null, Code: (string?)null);
                    return new GstSummaryRowDto(
                        section, m.Key.Gstin, m.Key.StateId, state.Name, state.Code, m.Key.Place, m.Key.Treatment, m.Key.Rate, legacy,
                        m.Sum(g => g.Taxable),
                        legacy ? 0m : m.Sum(g => g.Cgst), legacy ? 0m : m.Sum(g => g.Sgst + g.Utgst), legacy ? 0m : m.Sum(g => g.Igst), legacy ? 0m : m.Sum(g => g.Cess),
                        legacy ? m.Sum(g => g.Tax) : 0m,
                        m.Sum(g => g.Documents)); // distinct per branch, branches never share documents
                }));
        }
        rows = rows.OrderBy(r => r.Section).ThenBy(r => r.Gstin, StringComparer.Ordinal).ThenBy(r => r.PlaceOfSupplyStateName, StringComparer.Ordinal)
            .ThenBy(r => r.SupplyType).ThenBy(r => r.IsLegacy).ThenBy(r => r.Treatment).ThenBy(r => r.RatePercent).ToList();

        var blocks = rows.Select(r => r.Gstin).Concat(documents.Values.SelectMany(d => d.Keys).Select(context.GstinOf)).Distinct().OrderBy(g => g, StringComparer.Ordinal)
            .Select(gstin =>
            {
                TaxReportAmountsDto Sum(GstReportSection s) => Amounts(rows.Where(r => r.Section == s && r.Gstin == gstin));
                var sectionTotals = Enum.GetValues<GstReportSection>().Select(s => new GstSectionTotalDto(
                    s, Sum(s), documents[s].Where(d => context.GstinOf(d.Key) == gstin).Sum(d => d.Value))).ToList();
                return new GstGstinBlockDto(gstin, sectionTotals,
                    Subtract(Sum(GstReportSection.Outward), Sum(GstReportSection.OutwardReturn)),
                    Subtract(Sum(GstReportSection.Inward), Sum(GstReportSection.InwardReturn)));
            }).ToList();

        return Ok(new GstSummaryDto(from, to, branchId, context.CurrencyCode, rows, blocks, [NoPartyGstin, InputTaxAsInvoiced]));
    }

    [HttpGet("hsn-summary")]
    public async Task<ActionResult<HsnSummaryDto>> HsnSummary(
        [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo, [FromQuery] HsnDirection direction = HsnDirection.Sales, [FromQuery] Guid? branchId = null, CancellationToken ct = default)
    {
        if (ValidatePeriod(dateFrom, dateTo) is { } invalid) return invalid;
        if (await this.DenyUnlessAllowedAsync(db, branchId, ct) is { } denied) return denied;
        var (from, to) = (dateFrom!.Value, dateTo!.Value);

        var context = await LoadContextAsync(wantGst: true, branchId, ct);
        var ids = context.BranchIds;
        var sales = direction == HsnDirection.Sales;
        var lines = ids.Count == 0 ? [] : await TaxReportQueries.GroupForHsn(
            sales ? TaxReportQueries.SalesLines(db, from, to, ids) : TaxReportQueries.PurchaseLines(db, from, to, ids), ct);
        var returns = ids.Count == 0 ? [] : await TaxReportQueries.GroupForHsn(
            sales ? TaxReportQueries.SalesReturnLines(db, from, to, ids) : TaxReportQueries.PurchaseReturnLines(db, from, to, ids), ct);

        // Net of returns, merged per HSN x base unit x rate. Quantities add up only inside one such group: the same HSN and the same base unit (F5).
        var net = new Dictionary<(string? Hsn, Guid Unit, decimal Rate), TaxHsnGroup>();
        void Add(TaxHsnGroup g, int sign)
        {
            var key = (g.Hsn, g.BaseUnitId, g.Rate);
            if (!net.TryGetValue(key, out var n)) net[key] = n = new TaxHsnGroup { Hsn = g.Hsn, BaseUnitId = g.BaseUnitId, Rate = g.Rate };
            n.BaseQty += sign * g.BaseQty;
            n.Taxable += sign * g.Taxable;
            n.Cgst += sign * g.Cgst;
            n.Sgst += sign * (g.Sgst + g.Utgst);
            n.Igst += sign * g.Igst;
            n.Cess += sign * g.Cess;
            n.Tax += sign * g.Tax;
            n.LegacyTax += sign * g.LegacyTax;
        }
        foreach (var g in lines) Add(g, 1);
        foreach (var g in returns) Add(g, -1);

        var unitIds = net.Keys.Select(k => k.Unit).Distinct().ToList();
        var units = await db.Units.AsNoTracking().Where(u => unitIds.Contains(u.Id)).Select(u => new { u.Id, u.ShortCode, u.Uqc, u.DecimalPlaces }).ToDictionaryAsync(u => u.Id, ct);

        var rows = net.Values
            .Select(n =>
            {
                var unit = units[n.BaseUnitId];
                return new HsnSummaryRowDto(
                    n.Hsn, n.BaseUnitId, unit.ShortCode, string.IsNullOrEmpty(unit.Uqc) ? unit.ShortCode : unit.Uqc, unit.DecimalPlaces, n.Rate, n.BaseQty,
                    n.Taxable, n.Cgst, n.Sgst, n.Igst, n.Cess, n.LegacyTax, n.Taxable + n.Tax);
            })
            .OrderBy(r => r.HsnCode is null).ThenBy(r => r.HsnCode, StringComparer.Ordinal).ThenBy(r => r.UnitCode, StringComparer.Ordinal).ThenBy(r => r.RatePercent)
            .ToList();

        var limitations = rows.Any(r => r.HsnCode is null) ? new List<TaxReportLimitationDto> { HsnNotRecorded } : [];
        var totals = new HsnTotalsDto(rows.Sum(r => r.TaxableValue), rows.Sum(r => r.TotalValue - r.TaxableValue), rows.Sum(r => r.TotalValue));
        return Ok(new HsnSummaryDto(from, to, branchId, direction, context.CurrencyCode, rows, totals, limitations));
    }

    [HttpGet("vat-summary")]
    public async Task<ActionResult<VatSummaryDto>> VatSummary(
        [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo, [FromQuery] Guid? branchId, CancellationToken ct)
    {
        if (ValidatePeriod(dateFrom, dateTo) is { } invalid) return invalid;
        if (await this.DenyUnlessAllowedAsync(db, branchId, ct) is { } denied) return denied;
        var (from, to) = (dateFrom!.Value, dateTo!.Value);

        var context = await LoadContextAsync(wantGst: false, branchId, ct);
        var ids = context.BranchIds;
        var sections = new (VatReportSection Section, TaxDocumentKind Kind, IQueryable<TaxLineFact> Facts)[]
        {
            (VatReportSection.Output, TaxDocumentKind.Sale, TaxReportQueries.SalesLines(db, from, to, ids)),
            (VatReportSection.OutputReturn, TaxDocumentKind.SaleReturn, TaxReportQueries.SalesReturnLines(db, from, to, ids)),
            (VatReportSection.Input, TaxDocumentKind.Purchase, TaxReportQueries.PurchaseLines(db, from, to, ids)),
            (VatReportSection.InputReturn, TaxDocumentKind.PurchaseReturn, TaxReportQueries.PurchaseReturnLines(db, from, to, ids)),
        };

        var groups = new Dictionary<VatReportSection, List<TaxDocumentGroup>>();
        var documents = new Dictionary<VatReportSection, int>();
        foreach (var (section, kind, facts) in sections)
        {
            groups[section] = ids.Count == 0 ? [] : await TaxReportQueries.GroupForDocuments(facts, ct);
            documents[section] = ids.Count == 0 ? 0 : (await TaxReportQueries.DocumentCountsAsync(db, kind, from, to, ids, ct)).Values.Sum();
        }

        var regionIds = ids.Select(context.RegionOf).Where(r => r != null).Select(r => r!.Value).Distinct().ToList();
        var states = await LoadStatesAsync(regionIds, ct);

        var rows = new List<VatSummaryRowDto>();
        foreach (var (section, list) in groups)
        {
            rows.AddRange(list
                .GroupBy(g => (Category: VatCategoryOf(g.Treatment), Region: context.RegionOf(g.BranchId), g.Rate))
                .Select(m => new VatSummaryRowDto(
                    section, m.Key.Category, m.Key.Region, m.Key.Region is { } r && states.TryGetValue(r, out var s) ? s.Name : null, m.Key.Rate,
                    m.Sum(g => g.Taxable), m.Sum(g => g.Tax), m.Sum(g => g.Documents))));
        }
        rows = rows.OrderBy(r => r.Section).ThenBy(r => r.Category).ThenBy(r => r.RegionName, StringComparer.Ordinal).ThenBy(r => r.RatePercent).ToList();

        VatSectionTotalDto Total(VatReportSection s) => new(s, rows.Where(r => r.Section == s).Sum(r => r.TaxableValue), rows.Where(r => r.Section == s).Sum(r => r.VatAmount), documents[s]);
        var totals = Enum.GetValues<VatReportSection>().Select(Total).ToList();
        VatNetDto Net(VatReportSection plus, VatReportSection minus) => new(
            totals.Single(t => t.Section == plus).TaxableValue - totals.Single(t => t.Section == minus).TaxableValue,
            totals.Single(t => t.Section == plus).VatAmount - totals.Single(t => t.Section == minus).VatAmount);

        return Ok(new VatSummaryDto(from, to, branchId, context.CurrencyCode, rows, totals,
            Net(VatReportSection.Output, VatReportSection.OutputReturn), Net(VatReportSection.Input, VatReportSection.InputReturn), [NoPartyTaxRegistration]));
    }

    // ------------------------------------------------------------------ helpers

    private ActionResult? ValidatePeriod(DateOnly? dateFrom, DateOnly? dateTo)
    {
        if (dateFrom is null) ModelState.AddModelError("dateFrom", "Choose the start date of the period.");
        if (dateTo is null) ModelState.AddModelError("dateTo", "Choose the end date of the period.");
        if (dateFrom is { } f && dateTo is { } t)
        {
            if (t < f) ModelState.AddModelError("dateTo", "The end date cannot be before the start date.");
            else if (t.DayNumber - f.DayNumber + 1 > MaxDays) ModelState.AddModelError("dateTo", $"The period cannot be longer than {MaxDays} days.");
        }
        return ModelState.IsValid ? null : ValidationProblem(ModelState);
    }

    /// <summary>VAT bucket of a treatment: nil-rated counts as exempt, non-taxable as out of scope, history (no treatment) as legacy.</summary>
    private static VatCategory VatCategoryOf(TaxTreatment? treatment) => treatment switch
    {
        null => VatCategory.Legacy,
        TaxTreatment.Taxable => VatCategory.StandardRated,
        TaxTreatment.ZeroRated => VatCategory.ZeroRated,
        TaxTreatment.Exempt or TaxTreatment.NilRated => VatCategory.Exempt,
        _ => VatCategory.OutOfScope,
    };

    private static TaxReportAmountsDto Amounts(IEnumerable<GstSummaryRowDto> rows)
    {
        var list = rows.ToList();
        decimal cgst = list.Sum(r => r.Cgst), sgst = list.Sum(r => r.SgstUtgst), igst = list.Sum(r => r.Igst), cess = list.Sum(r => r.Cess), legacy = list.Sum(r => r.LegacyTax);
        return new TaxReportAmountsDto(list.Sum(r => r.TaxableValue), cgst, sgst, igst, cess, legacy, cgst + sgst + igst + cess + legacy);
    }

    private static TaxReportAmountsDto Subtract(TaxReportAmountsDto a, TaxReportAmountsDto b) => new(
        a.TaxableValue - b.TaxableValue, a.Cgst - b.Cgst, a.SgstUtgst - b.SgstUtgst, a.Igst - b.Igst, a.Cess - b.Cess, a.LegacyTax - b.LegacyTax, a.TotalTax - b.TotalTax);

    private async Task<Dictionary<Guid, (string? Name, string? Code)>> LoadStatesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        return (await db.States.AsNoTracking().Where(s => ids.Contains(s.Id)).Select(s => new { s.Id, s.Name, s.GstStateCode }).ToListAsync(ct))
            .ToDictionary(s => s.Id, s => (Name: (string?)s.Name, Code: s.GstStateCode));
    }

    /// <summary>The company's branches of one tax regime (a GST country, or any other for VAT; the branch's country, else the company's), with their GSTIN or tax registration and state.</summary>
    private async Task<ReportContext> LoadContextAsync(bool wantGst, Guid? branchId, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var settings = await db.BusinessSettings.AsNoTracking().Where(s => s.CompanyId == companyId)
            .Select(s => new { s.Gstin, s.StateId, s.CountryId, Currency = s.BaseCurrency.Code }).SingleAsync(ct);
        var branches = await db.Branches.AsNoTracking().Where(b => b.CompanyId == companyId).Select(b => new { b.Id, b.Gstin, b.StateId, b.CountryId }).ToListAsync(ct);

        var countryIds = branches.Select(b => b.CountryId).Append(settings.CountryId).Where(c => c != null).Select(c => c!.Value).Distinct().ToList();
        var codes = await db.Countries.AsNoTracking().Where(c => countryIds.Contains(c.Id)).Select(c => new { c.Id, c.Code }).ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        bool IsGst(Guid? countryId) => countryId is not { } id || !codes.TryGetValue(id, out var code) || string.Equals(code, "IN", StringComparison.OrdinalIgnoreCase); // no country: the legacy GST default

        var info = branches
            .Where(b => IsGst(b.CountryId ?? settings.CountryId) == wantGst && (branchId is null || b.Id == branchId))
            .ToDictionary(b => b.Id, b => new BranchInfo(b.Gstin ?? settings.Gstin, b.StateId ?? settings.StateId));
        return new ReportContext(settings.Currency, info);
    }

    private sealed record BranchInfo(string? Registration, Guid? RegionId);

    private sealed class ReportContext(string currencyCode, Dictionary<Guid, BranchInfo> branches)
    {
        public string CurrencyCode { get; } = currencyCode;
        public IReadOnlyList<Guid> BranchIds { get; } = branches.Keys.ToList();
        public string? GstinOf(Guid branchId) => branches.TryGetValue(branchId, out var b) ? b.Registration : null;
        public Guid? RegionOf(Guid branchId) => branches.TryGetValue(branchId, out var b) ? b.RegionId : null;
    }
}
