using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;

namespace Sellora.Tests.TaxProfiles;

/// <summary>DTO mapping and error mapping of the tax controllers (reads the same data the services wrote).</summary>
public class TaxControllersTests
{
    private static T Ok<T>(ActionResult<T> r) => (T)Assert.IsType<OkObjectResult>(r.Result).Value!;

    private static T Created<T>(ActionResult<T> r) => (T)Assert.IsType<CreatedAtActionResult>(r.Result).Value!;

    private static ProblemShape Problem(IActionResult r)
    {
        var o = Assert.IsType<ObjectResult>(r);
        var details = Assert.IsAssignableFrom<Microsoft.AspNetCore.Mvc.ValidationProblemDetails>(o.Value);
        return new ProblemShape(o.StatusCode, details.Errors.Keys.ToList());
    }

    private sealed record ProblemShape(int? Status, IReadOnlyList<string> Fields);

    private static ProblemShape Problem<T>(ActionResult<T> r) => Problem(r.Result!);

    private static T WithUser<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test")) },
        };
        return controller;
    }

    [Fact]
    public async Task Components_CreateListUpdateStatus_MapDtos_AndErrorsTo400()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var c = WithUser(new TaxComponentsController(w.Db, w.Components));
        TaxComponentsController Fresh() => WithUser(new TaxComponentsController(w.Db, w.Components)); // ModelState lives as long as the instance (one per request)

        var created = Created(await c.Create(new CreateTaxComponentRequest(w.India.Id, "LUXCESS", "Luxury cess", TaxComponentKind.Cess, TaxCalculationMethod.Percent), default));
        Assert.Equal(("LUXCESS", "IN", false), (created.Code, created.CountryCode, created.IsUsed));

        var list = Ok(await c.List(w.India.Id, true, default));
        Assert.Equal(7, list.Count);
        Assert.True(list.Single(x => x.Code == "CGST").IsUsed);
        Assert.False(list.Single(x => x.Code == "LUXCESS").IsUsed);

        var duplicate = Problem(await Fresh().Create(new CreateTaxComponentRequest(w.India.Id, "luxcess", "Another", TaxComponentKind.Cess, TaxCalculationMethod.Percent), default));
        Assert.Equal(["code"], duplicate.Fields);

        var cgst = list.Single(x => x.Code == "CGST");
        var method = Problem(await Fresh().Update(cgst.Id, new UpdateTaxComponentRequest("CGST", "Central GST", TaxComponentKind.Cgst, TaxCalculationMethod.AmountPerBaseUnit), default));
        Assert.Equal(["method"], method.Fields);

        Assert.IsType<NotFoundResult>((await c.Update(Guid.NewGuid(), new UpdateTaxComponentRequest("A", "B", TaxComponentKind.Cess, TaxCalculationMethod.Percent), default)).Result);
        Assert.False(Ok(await c.SetActive(created.Id, new SetActiveRequest(false), default)).IsActive);
        Assert.IsType<NotFoundResult>((await c.Get(Guid.NewGuid(), default)).Result);
    }

    [Fact]
    public async Task Profiles_ListDetailCreateVersionStatusAndSeed()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var c = WithUser(new TaxProfilesController(w.Db, w.Profiles, w.Clock));
        TaxProfilesController Fresh() => WithUser(new TaxProfilesController(w.Db, w.Profiles, w.Clock));

        var list = Ok(await c.List(w.India.Id, true, default));
        Assert.Equal(5, list.Count);
        var gst18 = list.Single(p => p.Code == "GST-18");
        Assert.Equal((18m, true, true, TaxRegime.Gst, "IN"), (gst18.CurrentVersion!.TotalPercent, gst18.CurrentVersion.IsCurrent, gst18.IsAssigned, gst18.Regime, gst18.CountryCode));
        Assert.Equal(3, gst18.CurrentVersion.Components.Count);
        Assert.False(list.Single(p => p.Code == "GST-5").IsAssigned);

        var cgst = (await w.ComponentAsync("CGST")).Id;
        var sgst = (await w.ComponentAsync("SGST")).Id;
        var igst = (await w.ComponentAsync("IGST")).Id;
        TaxVersionComponentRequest[] Set(decimal rate) =>
        [
            new(cgst, TaxApplicability.IntraState, rate / 2, null), new(sgst, TaxApplicability.IntraState, rate / 2, null), new(igst, TaxApplicability.InterState, rate, null),
        ];
        var created = Created(await c.Create(new CreateTaxProfileRequest(w.India.Id, "GST-12", "GST 12%", TaxRegime.Gst, TaxTreatment.Taxable,
            new AddTaxVersionRequest(new DateOnly(2025, 1, 1), Set(12m))), default));
        Assert.Equal((1, 12m), (created.VersionCount, created.CurrentVersion!.TotalPercent));
        Assert.False(created.IsAssigned);

        var version = Ok(await c.AddVersion(created.Id, new AddTaxVersionRequest(new DateOnly(2025, 9, 22), Set(5m)), default));
        Assert.Equal((5m, true, false), (version.TotalPercent, version.IsCurrent, version.IsUsed));
        var detail = Ok(await c.Get(created.Id, default));
        Assert.Equal(2, detail.Versions.Count);
        Assert.Equal(new DateOnly(2025, 9, 22), detail.Versions[0].EffectiveFrom); // newest first
        Assert.Equal(new DateOnly(2025, 9, 21), detail.Versions[1].EffectiveTo);
        Assert.Equal(5m, detail.CurrentVersion!.TotalPercent);

        var future = Problem(await Fresh().AddVersion(created.Id, new AddTaxVersionRequest(TaxWorld.Today.AddDays(1), Set(18m)), default));
        Assert.Equal(["effectiveFrom"], future.Fields);
        var incomplete = Problem(await Fresh().AddVersion(created.Id, new AddTaxVersionRequest(new DateOnly(2026, 1, 1), Set(18m)[..2]), default));
        Assert.Equal(["components"], incomplete.Fields);
        Assert.IsType<NotFoundResult>((await c.AddVersion(Guid.NewGuid(), new AddTaxVersionRequest(new DateOnly(2026, 1, 1), Set(5m)), default)).Result);

        Assert.Equal(["isActive"], Problem(await Fresh().SetActive(gst18.Id, new SetActiveRequest(false), default)).Fields);
        Assert.False(Ok(await c.SetActive(created.Id, new SetActiveRequest(false), default)).IsActive);
        Assert.IsType<NotFoundResult>((await c.Get(Guid.NewGuid(), default)).Result);

        var update = Ok(await c.Update(created.Id, new UpdateTaxProfileRequest("GST-TWELVE", "GST twelve"), default));
        Assert.Equal("GST-TWELVE", update.Code);

        var seed = await c.SeedStandard(default);
        var seeded = Assert.IsType<OkObjectResult>(seed.Result);
        Assert.Equal(0, Assert.IsType<TaxSeedResultDto>(seeded.Value).ProfilesAdded);
    }

    [Fact]
    public async Task Assignments_TreeResolveUpsertDeleteAndClear()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var beverages = await w.AddCategoryAsync("BEV", "Beverages");
        var soft = await w.AddSubcategoryAsync(beverages, "SOFT", "Soft Drinks", "2202");
        var cola = await w.AddProductAsync("COLA", beverages.Id, soft.Id);
        var gst5 = await w.ProfileAsync("GST-5");
        var gst40 = await w.ProfileAsync("GST-40");
        var c = WithUser(new TaxAssignmentsController(w.Db, w.Assignments, w.Resolver));
        TaxAssignmentsController Fresh() => WithUser(new TaxAssignmentsController(w.Db, w.Assignments, w.Resolver));

        var sub = Ok(await c.Upsert(new UpsertTaxAssignmentRequest(TaxScope.Subcategory, soft.Id, w.India.Id, gst40.Id, null), default));
        Assert.Equal(("Soft Drinks", soft.Id, TaxScope.Subcategory), (sub.ScopeName, sub.ScopeId, sub.ScopeType));
        Assert.Null(sub.PurchaseProfile);
        var ownRow = Ok(await c.Upsert(new UpsertTaxAssignmentRequest(TaxScope.Product, cola.Id, w.India.Id, gst5.Id, null), default));

        var tree = Ok(await c.Tree(null, true, default));
        Assert.Equal(("IN", "GST-18"), (tree.CountryCode, tree.CompanyDefault!.SalesProfile.Code));
        var category = Assert.Single(tree.Categories);
        Assert.Null(category.Assignment);
        Assert.Equal("GST-18", category.Effective!.SalesProfile.Code); // inherits the company default
        Assert.Equal(TaxScope.Company, category.Effective.Source);
        var subNode = Assert.Single(category.Subcategories);
        Assert.Equal(("GST-40", TaxScope.Subcategory, "Soft Drinks", "2202", 1), (subNode.Effective!.SalesProfile.Code, subNode.Effective.Source, subNode.Effective.SourceName, subNode.HsnCode, subNode.OverrideCount));
        Assert.Equal(cola.Id, Assert.Single(subNode.Overrides).ProductId);
        var counts = Ok(await c.Tree(null, false, default));
        Assert.Empty(counts.Categories.Single().Subcategories.Single().Overrides);
        Assert.Equal(1, counts.Categories.Single().Subcategories.Single().OverrideCount);

        var resolved = Ok(await c.Resolve(null, cola.Id, null, null, null, null, null, default));
        Assert.Equal((true, "GST-5", TaxScope.Product, "Product", "2202"), (resolved.Resolved, resolved.SalesProfile!.Code, resolved.Source, resolved.SourceName is { } n && n == cola.Name ? "Product" : "?", resolved.HsnCode));
        var form = Ok(await c.Resolve(w.India.Id, null, beverages.Id, soft.Id, "220299", null, null, default)); // an unsaved product
        Assert.Equal(("GST-40", TaxScope.Subcategory, "Soft Drinks", "Product"), (form.SalesProfile!.Code, form.Source, form.SourceName, form.HsnSource));
        var unresolved = Ok(await c.Resolve(w.Uae.Id, null, null, null, null, null, null, default));
        Assert.False(unresolved.Resolved);
        Assert.Equal(["countryId"], Problem((await Fresh().Tree(Guid.NewGuid(), true, default)).Result!).Fields);

        Assert.Equal(["salesProfileId"], Problem(await Fresh().Upsert(new UpsertTaxAssignmentRequest(TaxScope.Category, beverages.Id, w.India.Id, Guid.NewGuid(), null), default)).Fields);
        var company = tree.CompanyDefault;
        Assert.Equal(["id"], Problem(await Fresh().Delete(company.Id, default)).Fields);
        Assert.Equal(204, Assert.IsType<NoContentResult>(await c.Delete(ownRow.Id, default)).StatusCode);
        Assert.IsType<NotFoundResult>(await c.Delete(ownRow.Id, default));

        Assert.Equal(0, Ok(await c.ClearRedundantOverrides(new ClearRedundantOverridesRequest(w.India.Id), default)).Cleared);
        Assert.Equal(["countryId"], Problem(await Fresh().ClearRedundantOverrides(new ClearRedundantOverridesRequest(Guid.NewGuid()), default)).Fields);
    }
}
