using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.Api.BranchApiWorld;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// FI Phase 08 (FI-D16..FI-D18) over the real pipeline on PostgreSQL: category / subcategory master CRUD and its 400 field mapping, concurrent
/// duplicate creates (unique index -> 400, never 500), authorization (Inventory.Categories writes, any member reads), company isolation,
/// product classification validation + mirror, includeInactive / productCount, and the categoryId / subcategoryId / legacy category filters
/// on products, stock overview, current-stock report and the POS catalog.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ProductCategoriesApiTests(PostgresFixture postgres)
{
    private const string Route = "/api/v1/product-categories";

    private static string Tag() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private static Guid IdOf(JsonElement e) => e.GetProperty("id").GetGuid();
    private static Guid[] ItemIds(JsonElement paged) => paged.GetProperty("items").EnumerateArray().Select(IdOf).ToArray();

    private static object Body(string code, string name, int sort = 0) => new { code, name, sortOrder = sort };

    private Task<BranchApiWorld> WorldAsync() => BranchApiWorld.GetAsync(postgres);

    private static async Task<JsonElement> OkAsync(HttpResponseMessage r) => await RestrictedUserWorld.JsonOkAsync(r);

    private static async Task<(Guid Id, string Code, string Name)> NewCategoryAsync(BranchApiWorld w, string? prefix = null, HttpClient? client = null)
    {
        var tag = Tag();
        var name = (prefix ?? "Cat") + " " + tag;
        var response = await w.SendAsync(HttpMethod.Post, Route, Body("c" + tag, name), client: client);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (IdOf(await JsonAsync(response)), "C" + tag, name);
    }

    private static async Task<(Guid Id, string Code, string Name)> NewSubAsync(BranchApiWorld w, Guid categoryId, string? prefix = null)
    {
        var tag = Tag();
        var name = (prefix ?? "Sub") + " " + tag;
        var response = await w.SendAsync(HttpMethod.Post, $"{Route}/{categoryId}/subcategories", Body("s" + tag, name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (IdOf(await JsonAsync(response)), "S" + tag, name);
    }

    private static async Task<Guid> SetActiveAsync(BranchApiWorld w, string route, bool active)
    {
        var response = await w.SendAsync(HttpMethod.Post, route + "/status", new SetActiveRequest(active));
        Check(response, "status " + route);
        return IdOf(await JsonAsync(response));
    }

    private static async Task<JsonElement> ListAsync(BranchApiWorld w, bool includeInactive) =>
        await OkAsync(await w.SendAsync(HttpMethod.Get, $"{Route}?includeInactive={(includeInactive ? "true" : "false")}"));

    private static JsonElement CategoryIn(JsonElement list, Guid id) => list.EnumerateArray().Single(c => IdOf(c) == id);

    private static async Task AssertFieldAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field.ToLowerInvariant(), await ErrorFieldsAsync(response));
    }

    // ---- product helpers -------------------------------------------------------------------------------------------------------

    private async Task<(Guid BaseUnitId, Guid TaxId)> PenMastersAsync(BranchApiWorld w)
    {
        var pen = await OkAsync(await w.SendAsync(HttpMethod.Get, $"/api/v1/products/{w.ProductId}"));
        return (pen.GetProperty("baseUnitId").GetGuid(), pen.GetProperty("taxId").GetGuid());
    }

    private async Task<object> ProductBodyAsync(BranchApiWorld w, string sku, string name, Guid? categoryId, Guid? subcategoryId)
    {
        var (baseUnit, tax) = await PenMastersAsync(w);
        return new UpsertProductRequest(sku, name, categoryId, subcategoryId, baseUnit, 0m, [new UpsertProductUnitRequest(null, baseUnit, null, null, null, null)]);
    }

    private async Task<HttpResponseMessage> CreateProductAsync(BranchApiWorld w, Guid? categoryId, Guid? subcategoryId, string? name = null, HttpClient? client = null) =>
        await w.SendAsync(HttpMethod.Post, "/api/v1/products", await ProductBodyAsync(w, "C8-" + Tag(), name ?? "Prod " + Tag(), categoryId, subcategoryId), client: client);

    private async Task<Guid> NewProductAsync(BranchApiWorld w, Guid? categoryId, Guid? subcategoryId, string? name = null)
    {
        var response = await CreateProductAsync(w, categoryId, subcategoryId, name);
        Check(response, "create product");
        return IdOf(await JsonAsync(response));
    }

    private async Task<HttpResponseMessage> UpdateProductAsync(BranchApiWorld w, Guid id, Guid? categoryId, Guid? subcategoryId)
    {
        var detail = await OkAsync(await w.SendAsync(HttpMethod.Get, $"/api/v1/products/{id}"));
        var (baseUnit, tax) = await PenMastersAsync(w);
        return await w.SendAsync(HttpMethod.Put, $"/api/v1/products/{id}", new UpsertProductRequest(detail.GetProperty("sku").GetString()!, detail.GetProperty("name").GetString()!,
            categoryId, subcategoryId, baseUnit, 0m, [new UpsertProductUnitRequest(detail.GetProperty("units")[0].GetProperty("id").GetGuid(), baseUnit, null, null, null, null)]));
    }

    private async Task<Guid> OtherCompanyCategoryAsync(BranchApiWorld w, bool withSubcategory = false)
    {
        var company = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await using var db = w.Host.Database.CreateDbContext();
        db.Companies.Add(new Company { Id = company, Code = "X" + Tag(), Name = "Other " + Tag() });
        await db.SaveChangesAsync();
        await using var other = w.Host.Database.CreateDbContext(company);
        other.ProductCategories.Add(new ProductCategory { CompanyId = company, Id = categoryId, Code = "OTH", Name = "Other cat", CreatedAt = DateTime.UtcNow });
        await other.SaveChangesAsync();
        if (withSubcategory)
        {
            SubcategoryOfOther = Guid.NewGuid();
            other.ProductSubcategories.Add(new ProductSubcategory { CompanyId = company, Id = SubcategoryOfOther, CategoryId = categoryId, Code = "OS", Name = "Other sub", CreatedAt = DateTime.UtcNow });
            await other.SaveChangesAsync();
        }
        return categoryId;
    }

    private Guid SubcategoryOfOther;

    // ---- 1. CRUD ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_CategoryAndSubcategory_UpperCasesCodeTrimsNameAndRoundTripsThroughUpdateAndStatus()
    {
        var w = await WorldAsync();
        var tag = Tag();
        var created = await w.SendAsync(HttpMethod.Post, Route, Body(" c" + tag.ToLowerInvariant() + " ", "  Cat " + tag + "  ", 7));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var cat = await JsonAsync(created);
        Assert.Equal("C" + tag, cat.GetProperty("code").GetString());
        Assert.Equal("Cat " + tag, cat.GetProperty("name").GetString());
        Assert.Equal(7, cat.GetProperty("sortOrder").GetInt32());
        Assert.True(cat.GetProperty("isActive").GetBoolean());
        var id = IdOf(cat);

        var sub = await w.SendAsync(HttpMethod.Post, $"{Route}/{id}/subcategories", Body("s" + tag.ToLowerInvariant(), "Sub " + tag, 2));
        Assert.Equal(HttpStatusCode.Created, sub.StatusCode);
        var subJson = await JsonAsync(sub);
        Assert.Equal("S" + tag, subJson.GetProperty("code").GetString());
        Assert.Equal(id, subJson.GetProperty("categoryId").GetGuid());
        var subId = IdOf(subJson);

        var updated = await OkAsync(await w.SendAsync(HttpMethod.Put, $"{Route}/{id}", Body("x" + tag.ToLowerInvariant(), "Renamed " + tag, 9)));
        Assert.Equal("X" + tag, updated.GetProperty("code").GetString());
        Assert.Equal("Renamed " + tag, updated.GetProperty("name").GetString());
        Assert.Equal(9, updated.GetProperty("sortOrder").GetInt32());
        Assert.Equal(subId, updated.GetProperty("subcategories")[0].GetProperty("id").GetGuid());

        var updatedSub = await OkAsync(await w.SendAsync(HttpMethod.Put, $"{Route}/{id}/subcategories/{subId}", Body("y" + tag.ToLowerInvariant(), "Sub2 " + tag, 3)));
        Assert.Equal("Y" + tag, updatedSub.GetProperty("code").GetString());

        // Saving with the unchanged own code/name is not a duplicate.
        Check(await w.SendAsync(HttpMethod.Put, $"{Route}/{id}", Body("X" + tag, "Renamed " + tag, 9)), "idempotent update");

        await SetActiveAsync(w, $"{Route}/{id}/subcategories/{subId}", false);
        var off = await OkAsync(await w.SendAsync(HttpMethod.Post, $"{Route}/{id}/status", new SetActiveRequest(false)));
        Assert.False(off.GetProperty("isActive").GetBoolean());
        var on = await OkAsync(await w.SendAsync(HttpMethod.Post, $"{Route}/{id}/status", new SetActiveRequest(true)));
        Assert.True(on.GetProperty("isActive").GetBoolean());
    }

    [Theory]
    [InlineData("", "Name", 0, "code")]
    [InlineData("  ", "Name", 0, "code")]
    [InlineData("bad code!", "Name", 0, "code")]
    [InlineData("-LEAD", "Name", 0, "code")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU", "Name", 0, "code")]
    [InlineData("OK1", "", 0, "name")]
    [InlineData("OK1", "   ", 0, "name")]
    [InlineData("OK1", "123456789012345678901234567890123456789012345678901234567890X", 0, "name")]
    [InlineData("OK1", "Name", -1, "sortOrder")]
    public async Task Create_InvalidInput_Is400WithTheFieldKey(string code, string name, int sort, string field)
    {
        var w = await WorldAsync();
        await AssertFieldAsync(await w.SendAsync(HttpMethod.Post, Route, Body(code, name, sort)), field);
        var (id, _, _) = await NewCategoryAsync(w);
        await AssertFieldAsync(await w.SendAsync(HttpMethod.Post, $"{Route}/{id}/subcategories", Body(code, name, sort)), field);
        await AssertFieldAsync(await w.SendAsync(HttpMethod.Put, $"{Route}/{id}", Body(code, name, sort)), field);
    }

    [Fact]
    public async Task Create_DuplicateCodeOrCaseAndSpaceVariantName_Is400OnTheMatchingField()
    {
        var w = await WorldAsync();
        var (id, code, name) = await NewCategoryAsync(w);
        var tag = Tag();

        await AssertFieldAsync(await w.SendAsync(HttpMethod.Post, Route, Body(code.ToLowerInvariant(), "Other " + tag)), "code");
        await AssertFieldAsync(await w.SendAsync(HttpMethod.Post, Route, Body("D" + tag, name.ToUpperInvariant())), "name");
        await AssertFieldAsync(await w.SendAsync(HttpMethod.Post, Route, Body("D" + tag, "   " + name.ToLowerInvariant() + "  ")), "name");

        // Update of another category onto these values is refused as well.
        var (other, _, _) = await NewCategoryAsync(w);
        await AssertFieldAsync(await w.SendAsync(HttpMethod.Put, $"{Route}/{other}", Body(code, "Fresh " + tag)), "code");
        await AssertFieldAsync(await w.SendAsync(HttpMethod.Put, $"{Route}/{other}", Body("E" + tag, name.ToLowerInvariant())), "name");

        // Subcategories: unique within the category only.
        var (sub, subCode, subName) = await NewSubAsync(w, id);
        await AssertFieldAsync(await w.SendAsync(HttpMethod.Post, $"{Route}/{id}/subcategories", Body(subCode.ToLowerInvariant(), "Zed " + tag)), "code");
        await AssertFieldAsync(await w.SendAsync(HttpMethod.Post, $"{Route}/{id}/subcategories", Body("Z" + tag, " " + subName.ToUpperInvariant())), "name");
        Check(await w.SendAsync(HttpMethod.Post, $"{Route}/{other}/subcategories", Body(subCode, subName)), "same sub code/name under another category is allowed");
        Assert.NotEqual(Guid.Empty, sub);
    }

    [Fact]
    public async Task Create_TwoConcurrentPostsWithTheSameCodeOrName_ExactlyOne201AndOne400()
    {
        var w = await WorldAsync();
        for (var round = 0; round < 3; round++)
        {
            string t1 = Tag(), t2 = Tag(), t3 = Tag();
            // Same code AND name, same code only, same name only.
            var pairs = new[] { (Body("K" + t1, "N " + t1), Body("K" + t1, "N " + t1)), (Body("K" + t2, "N1 " + t2), Body("K" + t2, "N2 " + t2)), (Body("K1" + t3, "NN " + t3), Body("K2" + t3, "nn " + t3)) };
            foreach (var (first, second) in pairs)
            {
                var results = await Task.WhenAll(w.SendAsync(HttpMethod.Post, Route, first), w.SendAsync(HttpMethod.Post, Route, second));
                var codes = results.Select(r => (int)r.StatusCode).Order().ToArray();
                Assert.True(codes.SequenceEqual([201, 400]), $"round {round}: {string.Join(",", codes)}");
            }
        }

        var (catId, _, _) = await NewCategoryAsync(w);
        var subTag = Tag();
        var subs = await Task.WhenAll(
            w.SendAsync(HttpMethod.Post, $"{Route}/{catId}/subcategories", Body("Q" + subTag, "Sub " + subTag)),
            w.SendAsync(HttpMethod.Post, $"{Route}/{catId}/subcategories", Body("Q" + subTag, "Sub " + subTag)));
        Assert.Equal([201, 400], subs.Select(r => (int)r.StatusCode).Order().ToArray());
    }

    // ---- 2. routes -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Subcategory_ViaAnotherCategorysRoute_Is404ForUpdateAndStatus()
    {
        var w = await WorldAsync();
        var (a, _, _) = await NewCategoryAsync(w);
        var (b, _, _) = await NewCategoryAsync(w);
        var (sub, _, subName) = await NewSubAsync(w, a);

        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Put, $"{Route}/{b}/subcategories/{sub}", Body("ZZ" + Tag(), "X" + Tag()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Post, $"{Route}/{b}/subcategories/{sub}/status", new SetActiveRequest(false))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Post, $"{Route}/{Guid.NewGuid()}/subcategories", Body("ZY" + Tag(), "Y" + Tag()))).StatusCode);

        var unchanged = CategoryIn(await ListAsync(w, true), a).GetProperty("subcategories")[0];
        Assert.Equal(subName, unchanged.GetProperty("name").GetString());
        Assert.True(unchanged.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task SubcategoryStatus_ActivatingUnderInactiveCategory_Is400OnIsActive()
    {
        var w = await WorldAsync();
        var (cat, _, _) = await NewCategoryAsync(w);
        var (sub, _, _) = await NewSubAsync(w, cat);
        var subRoute = $"{Route}/{cat}/subcategories/{sub}";
        await SetActiveAsync(w, subRoute, false);
        await SetActiveAsync(w, $"{Route}/{cat}", false);

        await AssertFieldAsync(await w.SendAsync(HttpMethod.Post, subRoute + "/status", new SetActiveRequest(true)), "isActive");
        Assert.False(CategoryIn(await ListAsync(w, true), cat).GetProperty("subcategories")[0].GetProperty("isActive").GetBoolean());

        await SetActiveAsync(w, $"{Route}/{cat}", true);
        Assert.True((await w.SendAsync(HttpMethod.Post, subRoute + "/status", new SetActiveRequest(true))).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Delete_NoRouteExists_Is404Or405()
    {
        var w = await WorldAsync();
        var (cat, _, _) = await NewCategoryAsync(w);
        var (sub, _, _) = await NewSubAsync(w, cat);
        foreach (var route in new[] { $"{Route}/{cat}", $"{Route}/{cat}/subcategories/{sub}", Route })
        {
            var status = (await w.SendAsync(HttpMethod.Delete, route)).StatusCode;
            Assert.True(status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"DELETE {route} -> {(int)status}");
        }
        Assert.Equal(cat, IdOf(CategoryIn(await ListAsync(w, true), cat)));
    }

    // ---- 3. authorization ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Authorization_WithoutInventoryCategories_ReadsAre200AndEveryWriteIs403()
    {
        var w = await WorldAsync();
        var (cat, _, _) = await NewCategoryAsync(w);
        var (sub, _, _) = await NewSubAsync(w, cat);
        using var limited = await w.Host.LimitedClientAsync(w.Admin, "Inventory.View");

        Assert.Equal(HttpStatusCode.OK, (await w.SendAsync(HttpMethod.Get, Route, client: limited)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.SendAsync(HttpMethod.Post, Route, Body("F" + Tag(), "F" + Tag()), client: limited)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.SendAsync(HttpMethod.Put, $"{Route}/{cat}", Body("F" + Tag(), "F" + Tag()), client: limited)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.SendAsync(HttpMethod.Post, $"{Route}/{cat}/status", new SetActiveRequest(false), client: limited)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.SendAsync(HttpMethod.Post, $"{Route}/{cat}/subcategories", Body("F" + Tag(), "F" + Tag()), client: limited)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.SendAsync(HttpMethod.Put, $"{Route}/{cat}/subcategories/{sub}", Body("F" + Tag(), "F" + Tag()), client: limited)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.SendAsync(HttpMethod.Post, $"{Route}/{cat}/subcategories/{sub}/status", new SetActiveRequest(false), client: limited)).StatusCode);

        using var anonymous = w.Host.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.SendAsync(HttpMethod.Get, Route, client: anonymous)).StatusCode);

        using var writer = await w.Host.LimitedClientAsync(w.Admin, "Inventory.Categories");
        Assert.Equal(HttpStatusCode.Created, (await w.SendAsync(HttpMethod.Post, Route, Body("W" + Tag(), "W" + Tag()), client: writer)).StatusCode);
    }

    [Fact]
    public async Task Authorization_AdministratorCanWriteAndBranchScopedUserReadsTheCompanyCatalog()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres);
        var w = r.World;
        var (cat, _, name) = await NewCategoryAsync(w);
        var (branch, _) = await w.NewBranchAsync();
        var user = await r.CreateAtAsync(branch);

        var list = await OkAsync(await w.SendAsync(HttpMethod.Get, Route + "?includeInactive=true", client: user.Client));
        Assert.Equal(name, CategoryIn(list, cat).GetProperty("name").GetString());
        var scoped = await OkAsync(await w.SendAsync(HttpMethod.Get, Route, branch: branch, client: user.Client));
        Assert.Contains(cat, scoped.EnumerateArray().Select(IdOf));
    }

    // ---- 4. company isolation --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OtherCompanysCategory_IsNeverListedAnd404OnUpdateAndStatus()
    {
        var w = await WorldAsync();
        var foreign = await OtherCompanyCategoryAsync(w, withSubcategory: true);
        var foreignSub = SubcategoryOfOther;

        var list = await ListAsync(w, true);
        Assert.DoesNotContain(foreign, list.EnumerateArray().Select(IdOf));
        Assert.DoesNotContain(foreignSub, list.EnumerateArray().SelectMany(c => c.GetProperty("subcategories").EnumerateArray()).Select(IdOf));
        Assert.DoesNotContain("Other cat", await (await w.SendAsync(HttpMethod.Get, Route + "?includeInactive=true")).Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Put, $"{Route}/{foreign}", Body("ZZ" + Tag(), "Z" + Tag()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Post, $"{Route}/{foreign}/status", new SetActiveRequest(false))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Post, $"{Route}/{foreign}/subcategories", Body("ZY" + Tag(), "Y" + Tag()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Put, $"{Route}/{foreign}/subcategories/{foreignSub}", Body("ZX" + Tag(), "X" + Tag()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Post, $"{Route}/{foreign}/subcategories/{foreignSub}/status", new SetActiveRequest(false))).StatusCode);

        await using var db = w.Host.Database.CreateDbContext();
        var row = await db.ProductCategories.IgnoreQueryFilters().SingleAsync(c => c.Id == foreign);
        Assert.True(row.IsActive);
        Assert.Equal("Other cat", row.Name);
    }

    [Fact]
    public async Task SameCodeAndNameInAnotherCompany_DoNotCollideWithThisCompany()
    {
        var w = await WorldAsync();
        var foreign = await OtherCompanyCategoryAsync(w);
        Assert.NotEqual(Guid.Empty, foreign);
        // "OTH" / "Other cat" exist in the other company only; this company may use them.
        var code = "OTH";
        var response = await w.SendAsync(HttpMethod.Post, Route, Body(code, "Other cat"));
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.BadRequest);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            // Only legitimate when a previous run of this test in the shared host already created them in THIS company.
            await using var db = w.Host.Database.CreateDbContext();
            Assert.True(await db.ProductCategories.AnyAsync(c => c.Code == code));
        }
    }

    [Fact]
    public async Task Product_WithAnotherCompanysCategoryOrSubcategory_Is400AndChangesNothing()
    {
        var w = await WorldAsync();
        var foreign = await OtherCompanyCategoryAsync(w, withSubcategory: true);
        var foreignSub = SubcategoryOfOther;

        await AssertFieldAsync(await CreateProductAsync(w, foreign, null), "categoryId");
        await AssertFieldAsync(await CreateProductAsync(w, foreign, foreignSub), "categoryId");

        var (own, _, _) = await NewCategoryAsync(w);
        await AssertFieldAsync(await CreateProductAsync(w, own, foreignSub), "subcategoryId");

        var product = await NewProductAsync(w, own, null);
        await AssertFieldAsync(await UpdateProductAsync(w, product, foreign, null), "categoryId");
        var detail = await OkAsync(await w.SendAsync(HttpMethod.Get, $"/api/v1/products/{product}"));
        Assert.Equal(own, detail.GetProperty("categoryId").GetGuid());
    }

    // ---- 5. product classification ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Product_ValidClassification_PersistsAndDetailShowsIdsNamesAndLegacyMirror()
    {
        var w = await WorldAsync();
        var (cat, _, catName) = await NewCategoryAsync(w);
        var (sub, _, subName) = await NewSubAsync(w, cat);

        var created = await w.SendAsync(HttpMethod.Post, "/api/v1/products", await ProductBodyAsync(w, "C8-" + Tag(), "Classified " + Tag(), cat, sub));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = IdOf(await JsonAsync(created));

        var detail = await OkAsync(await w.SendAsync(HttpMethod.Get, $"/api/v1/products/{id}"));
        Assert.Equal(cat, detail.GetProperty("categoryId").GetGuid());
        Assert.Equal(catName, detail.GetProperty("categoryName").GetString());
        Assert.Equal(sub, detail.GetProperty("subcategoryId").GetGuid());
        Assert.Equal(subName, detail.GetProperty("subcategoryName").GetString());
        Assert.Equal(catName, detail.GetProperty("category").GetString());

        await using (var db = w.Host.Database.CreateDbContext())
        {
            var row = await db.Products.SingleAsync(p => p.Id == id);
            Assert.Equal(cat, row.CategoryId);
            Assert.Equal(sub, row.SubcategoryId);
            Assert.Equal(catName, row.Category);
        }

        // Update to a category without subcategory clears the subcategory; to no category clears both.
        var (cat2, _, cat2Name) = await NewCategoryAsync(w);
        Check(await UpdateProductAsync(w, id, cat2, null), "update to cat2");
        var moved = await OkAsync(await w.SendAsync(HttpMethod.Get, $"/api/v1/products/{id}"));
        Assert.Equal(cat2Name, moved.GetProperty("category").GetString());
        Assert.Equal(JsonValueKind.Null, moved.GetProperty("subcategoryId").ValueKind);
        Check(await UpdateProductAsync(w, id, null, null), "clear");
        var cleared = await OkAsync(await w.SendAsync(HttpMethod.Get, $"/api/v1/products/{id}"));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("categoryId").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("category").ValueKind);
    }

    [Fact]
    public async Task Product_SubcategoryOfAnotherCategoryOrWithoutCategory_Is400OnSubcategoryId()
    {
        var w = await WorldAsync();
        var (a, _, _) = await NewCategoryAsync(w);
        var (b, _, _) = await NewCategoryAsync(w);
        var (subOfB, _, _) = await NewSubAsync(w, b);

        await AssertFieldAsync(await CreateProductAsync(w, a, subOfB), "subcategoryId");
        await AssertFieldAsync(await CreateProductAsync(w, null, subOfB), "subcategoryId");
        await AssertFieldAsync(await CreateProductAsync(w, a, Guid.NewGuid()), "subcategoryId");
        await AssertFieldAsync(await CreateProductAsync(w, Guid.NewGuid(), null), "categoryId");

        var product = await NewProductAsync(w, a, null);
        await AssertFieldAsync(await UpdateProductAsync(w, product, a, subOfB), "subcategoryId");
        await AssertFieldAsync(await UpdateProductAsync(w, product, null, subOfB), "subcategoryId");
    }

    [Fact]
    public async Task Product_InactiveMasters_RefusedWhenNewlyAssignedButKeptWhenUnchangedOnUpdate()
    {
        var w = await WorldAsync();
        var (cat, _, _) = await NewCategoryAsync(w);
        var (sub, _, _) = await NewSubAsync(w, cat);
        var (inactiveCat, _, _) = await NewCategoryAsync(w);
        var (inactiveCatSub, _, _) = await NewSubAsync(w, inactiveCat);
        var product = await NewProductAsync(w, cat, sub);
        var legacyProduct = await NewProductAsync(w, inactiveCat, inactiveCatSub);

        // Deactivate the product's subcategory: kept on update, refused on create / a different product.
        await SetActiveAsync(w, $"{Route}/{cat}/subcategories/{sub}", false);
        Assert.Equal(HttpStatusCode.OK, (await UpdateProductAsync(w, product, cat, sub)).StatusCode);
        await AssertFieldAsync(await CreateProductAsync(w, cat, sub), "subcategoryId");
        var other = await NewProductAsync(w, cat, null);
        await AssertFieldAsync(await UpdateProductAsync(w, other, cat, sub), "subcategoryId");

        // Deactivate a category: same for the category.
        await SetActiveAsync(w, $"{Route}/{inactiveCat}/subcategories/{inactiveCatSub}", false);
        await SetActiveAsync(w, $"{Route}/{inactiveCat}", false);
        Assert.Equal(HttpStatusCode.OK, (await UpdateProductAsync(w, legacyProduct, inactiveCat, inactiveCatSub)).StatusCode);
        await AssertFieldAsync(await CreateProductAsync(w, inactiveCat, null), "categoryId");
        await AssertFieldAsync(await UpdateProductAsync(w, other, inactiveCat, null), "categoryId");
    }

    [Fact]
    public async Task RenameCategory_UpdatesProductDetailAndTheDbMirror_AndProductSearchByCategoryNameMatches()
    {
        var w = await WorldAsync();
        var (cat, _, _) = await NewCategoryAsync(w);
        var (cat2, _, _) = await NewCategoryAsync(w);
        var p1 = await NewProductAsync(w, cat, null);
        var p2 = await NewProductAsync(w, cat, null);
        var untouched = await NewProductAsync(w, cat2, null);

        var newName = "Renamed " + Tag();
        var code = (await OkAsync(await w.SendAsync(HttpMethod.Get, Route + "?includeInactive=true"))).EnumerateArray().Single(c => IdOf(c) == cat).GetProperty("code").GetString()!;
        Check(await w.SendAsync(HttpMethod.Put, $"{Route}/{cat}", Body(code, newName)), "rename");

        foreach (var id in new[] { p1, p2 })
        {
            var detail = await OkAsync(await w.SendAsync(HttpMethod.Get, $"/api/v1/products/{id}"));
            Assert.Equal(newName, detail.GetProperty("categoryName").GetString());
            Assert.Equal(newName, detail.GetProperty("category").GetString());
        }
        await using (var db = w.Host.Database.CreateDbContext())
        {
            Assert.Equal([newName, newName], await db.Products.Where(p => p.CategoryId == cat).Select(p => p.Category!).ToArrayAsync());
            Assert.NotEqual(newName, (await db.Products.SingleAsync(p => p.Id == untouched)).Category);
        }

        var found = ItemIds(await OkAsync(await w.SendAsync(HttpMethod.Get, $"/api/v1/products?search={Uri.EscapeDataString(newName)}&pageSize=100")));
        Assert.Equivalent(new[] { p1, p2 }, found);
        var foundLower = ItemIds(await OkAsync(await w.SendAsync(HttpMethod.Get, $"/api/v1/products?search={Uri.EscapeDataString(newName.ToLowerInvariant())}&pageSize=100")));
        Assert.Equivalent(new[] { p1, p2 }, foundLower);
    }

    // ---- 6. includeInactive and productCount -----------------------------------------------------------------------------------

    [Fact]
    public async Task List_IncludeInactive_FiltersCategoriesAndNestedSubcategories()
    {
        var w = await WorldAsync();
        var (activeCat, _, _) = await NewCategoryAsync(w);
        var (activeSub, _, _) = await NewSubAsync(w, activeCat);
        var (inactiveSub, _, _) = await NewSubAsync(w, activeCat);
        var (inactiveCat, _, _) = await NewCategoryAsync(w);
        var (subOfInactiveCat, _, _) = await NewSubAsync(w, inactiveCat);
        await SetActiveAsync(w, $"{Route}/{activeCat}/subcategories/{inactiveSub}", false);
        await SetActiveAsync(w, $"{Route}/{inactiveCat}/subcategories/{subOfInactiveCat}", false);
        await SetActiveAsync(w, $"{Route}/{inactiveCat}", false);

        var activeOnly = await ListAsync(w, false);
        Assert.DoesNotContain(inactiveCat, activeOnly.EnumerateArray().Select(IdOf));
        var shown = CategoryIn(activeOnly, activeCat);
        Assert.Equal([activeSub], shown.GetProperty("subcategories").EnumerateArray().Select(IdOf).ToArray());
        Assert.All(activeOnly.EnumerateArray(), c => Assert.True(c.GetProperty("isActive").GetBoolean()));

        var defaultList = await OkAsync(await w.SendAsync(HttpMethod.Get, Route));
        Assert.DoesNotContain(inactiveCat, defaultList.EnumerateArray().Select(IdOf));

        var all = await ListAsync(w, true);
        Assert.Equivalent(new[] { activeSub, inactiveSub }, CategoryIn(all, activeCat).GetProperty("subcategories").EnumerateArray().Select(IdOf).ToArray());
        Assert.Equal([subOfInactiveCat], CategoryIn(all, inactiveCat).GetProperty("subcategories").EnumerateArray().Select(IdOf).ToArray());
        Assert.False(CategoryIn(all, inactiveCat).GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task ProductCount_CountsInactiveProductsAndTracksSubcategoryAndCategoryAssignments()
    {
        var w = await WorldAsync();
        var (cat, _, _) = await NewCategoryAsync(w);
        var (sub, _, _) = await NewSubAsync(w, cat);
        var p1 = await NewProductAsync(w, cat, sub);
        var p2 = await NewProductAsync(w, cat, null);
        var p3 = await NewProductAsync(w, cat, sub);
        Check(await w.SendAsync(HttpMethod.Post, $"/api/v1/products/{p3}/status", new SetActiveRequest(false)), "deactivate product");
        Assert.NotEqual(p1, p2);

        var c = CategoryIn(await ListAsync(w, true), cat);
        Assert.Equal(3, c.GetProperty("productCount").GetInt32());
        Assert.Equal(2, c.GetProperty("subcategories")[0].GetProperty("productCount").GetInt32());

        // Empty category.
        var (empty, _, _) = await NewCategoryAsync(w);
        Assert.Equal(0, CategoryIn(await ListAsync(w, true), empty).GetProperty("productCount").GetInt32());

        // Single-resource responses carry the same counts.
        var put = await OkAsync(await w.SendAsync(HttpMethod.Post, $"{Route}/{cat}/status", new SetActiveRequest(true)));
        Assert.Equal(3, put.GetProperty("productCount").GetInt32());
    }

    // ---- 7. filters ------------------------------------------------------------------------------------------------------------

    private sealed record FilterWorld(BranchApiWorld W, Guid Cat, string CatName, Guid Sub, Guid OtherCat, Guid InSub, Guid InCatOnly, Guid Other);

    private async Task<FilterWorld> BuildFilterWorldAsync()
    {
        var w = await WorldAsync();
        var (cat, _, catName) = await NewCategoryAsync(w);
        var (sub, _, _) = await NewSubAsync(w, cat);
        var (other, _, _) = await NewCategoryAsync(w);
        var inSub = await NewProductAsync(w, cat, sub);
        var inCatOnly = await NewProductAsync(w, cat, null);
        var otherProduct = await NewProductAsync(w, other, null);
        return new FilterWorld(w, cat, catName, sub, other, inSub, inCatOnly, otherProduct);
    }

    [Fact]
    public async Task Filters_ProductsList_ByCategoryIdSubcategoryIdAndLegacyCategoryName()
    {
        var f = await BuildFilterWorldAsync();
        var w = f.W;
        async Task<Guid[]> Ids(string q) => ItemIds(await OkAsync(await w.SendAsync(HttpMethod.Get, "/api/v1/products?pageSize=100&" + q)));

        Assert.Equivalent(new[] { f.InSub, f.InCatOnly }, await Ids($"categoryId={f.Cat}"));
        Assert.Equal([f.InSub], await Ids($"subcategoryId={f.Sub}"));
        Assert.Equal([f.InSub], await Ids($"categoryId={f.Cat}&subcategoryId={f.Sub}"));
        Assert.Equivalent(new[] { f.InSub, f.InCatOnly }, await Ids($"category={Uri.EscapeDataString(f.CatName)}"));
        Assert.Equal([f.Other], await Ids($"categoryId={f.OtherCat}"));
        Assert.Empty(await Ids($"categoryId={f.OtherCat}&subcategoryId={f.Sub}"));
        Assert.Empty(await Ids($"categoryId={Guid.NewGuid()}"));
    }

    [Fact]
    public async Task Filters_StockOverview_WithAndWithoutCompanyScope()
    {
        var f = await BuildFilterWorldAsync();
        var w = f.W;
        foreach (var scope in new[] { "", "&scope=company" })
        {
            async Task<JsonElement[]> Items(string q) =>
                (await OkAsync(await w.SendAsync(HttpMethod.Get, "/api/v1/stock/overview?pageSize=100" + scope + "&" + q))).GetProperty("items").EnumerateArray().ToArray();
            static Guid[] Pids(JsonElement[] items) => items.Select(i => i.GetProperty("productId").GetGuid()).ToArray();

            Assert.Equivalent(new[] { f.InSub, f.InCatOnly }, Pids(await Items($"categoryId={f.Cat}")));
            var bySub = await Items($"subcategoryId={f.Sub}");
            Assert.Equal([f.InSub], Pids(bySub));
            Assert.Equal(f.Cat, bySub[0].GetProperty("categoryId").GetGuid());
            Assert.False(string.IsNullOrEmpty(bySub[0].GetProperty("subcategoryName").GetString()), "subcategoryName on " + scope);
            Assert.Equivalent(new[] { f.InSub, f.InCatOnly }, Pids(await Items($"category={Uri.EscapeDataString(f.CatName)}")));
            Assert.Empty(await Items($"categoryId={Guid.NewGuid()}"));
        }
    }

    [Fact]
    public async Task Filters_CurrentStockReport_ByCategoryAndSubcategory_AndCategoriesListsActiveMasterNames()
    {
        var f = await BuildFilterWorldAsync();
        var w = f.W;
        foreach (var scope in new[] { "", "&scope=company" })
        {
            async Task<Guid[]> Ids(string q) =>
                (await OkAsync(await w.SendAsync(HttpMethod.Get, "/api/v1/reports/current-stock?pageSize=100" + scope + "&" + q))).GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("productId").GetGuid()).ToArray();

            Assert.Equivalent(new[] { f.InSub, f.InCatOnly }, await Ids($"categoryId={f.Cat}"));
            Assert.Equal([f.InSub], await Ids($"subcategoryId={f.Sub}"));
            Assert.Equivalent(new[] { f.InSub, f.InCatOnly }, await Ids($"category={Uri.EscapeDataString(f.CatName)}"));
            Assert.Empty(await Ids($"categoryId={f.OtherCat}&subcategoryId={f.Sub}"));
        }

        var inactiveName = (await NewCategoryAsync(w)).Name;
        var (inactiveId, _, _) = await NewCategoryAsync(w);
        await SetActiveAsync(w, $"{Route}/{inactiveId}", false);
        var names = (await OkAsync(await w.SendAsync(HttpMethod.Get, "/api/v1/reports/current-stock/categories"))).EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains(f.CatName, names);
        Assert.Contains(inactiveName, names);
        await using var db = w.Host.Database.CreateDbContext();
        var inactiveRowName = (await db.ProductCategories.SingleAsync(c => c.Id == inactiveId)).Name;
        Assert.DoesNotContain(inactiveRowName, names);
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    [Fact]
    public async Task Filters_SalesCatalog_ByCategoryIdSubcategoryIdAndLegacyCategory()
    {
        var f = await BuildFilterWorldAsync();
        var w = f.W;
        async Task<Guid[]> Ids(string q) =>
            (await OkAsync(await w.SendAsync(HttpMethod.Get, "/api/v1/sales/catalog?" + q))).EnumerateArray().Select(i => i.GetProperty("productId").GetGuid()).ToArray();

        Assert.Equivalent(new[] { f.InSub, f.InCatOnly }, await Ids($"categoryId={f.Cat}"));
        Assert.Equal([f.InSub], await Ids($"subcategoryId={f.Sub}"));
        Assert.Equivalent(new[] { f.InSub, f.InCatOnly }, await Ids($"category={Uri.EscapeDataString(f.CatName)}"));
        Assert.Equivalent(new[] { f.InSub, f.InCatOnly }, await Ids($"search={Uri.EscapeDataString(f.CatName)}"));
        Assert.Empty(await Ids($"categoryId={Guid.NewGuid()}"));
        var one = (await OkAsync(await w.SendAsync(HttpMethod.Get, $"/api/v1/sales/catalog?subcategoryId={f.Sub}")))[0];
        Assert.Equal(f.Cat, one.GetProperty("categoryId").GetGuid());
    }

    [Fact]
    public async Task Filters_ActiveBranchUser_StockOverviewByCategoryReadsTheBranch()
    {
        var f = await BuildFilterWorldAsync();
        var r = await RestrictedUserWorld.GetWorldAsync(postgres);
        var (branch, _) = await f.W.NewBranchAsync();
        var user = await r.CreateAtAsync(branch);
        var overview = await OkAsync(await f.W.SendAsync(HttpMethod.Get, $"/api/v1/stock/overview?pageSize=100&categoryId={f.Cat}", branch: branch, client: user.Client));
        Assert.Equivalent(new[] { f.InSub, f.InCatOnly }, overview.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("productId").GetGuid()).ToArray());
    }
}
