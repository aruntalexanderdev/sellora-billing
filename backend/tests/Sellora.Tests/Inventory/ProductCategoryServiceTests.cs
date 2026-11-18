using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Inventory;

/// <summary>Phase 08 (FI-D16): category and subcategory masters and their assignment to products. InMemory: the unique indexes and the
/// composite FK are not exercised here (integration tests), only the service rules.</summary>
public class ProductCategoryServiceTests
{
    private sealed record Fixture(SelloraDbContext Db, ProductCategoryService Categories, ProductService Products, Unit Unit, Tax Tax);

    private static async Task<Fixture> SeedAsync()
    {
        var db = TestCompany.NewDb(new DbContextOptionsBuilder<SelloraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Piece", ShortCode = "pc", MeasurementCategory = MeasurementCategory.Count, DecimalPlaces = 0 };
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST 5%", RatePercent = 5m };
        db.AddRange(unit, tax);
        await db.SaveChangesAsync();
        return new Fixture(db, new ProductCategoryService(db), new ProductService(db), unit, tax);
    }

    private static UpsertProductClassificationInput Input(string code, string name, int sort = 0) => new(code, name, sort);

    private static async Task<string?> FieldOf(Func<Task> act) => (await Assert.ThrowsAsync<DomainValidationException>(act)).Field;

    private static UpsertProductInput ProductInput(Fixture f, string sku, Guid? categoryId, Guid? subcategoryId) => new(
        sku, "Item " + sku, categoryId, subcategoryId, f.Unit.Id, f.Tax.Id, 0m,
        [new ProductUnitInputItem(null, f.Unit.Id, null, 1m, "Piece", null, true, true, false, false, true)]);

    // ------------------------------------------------------------------ masters

    [Fact]
    public async Task CreateCategoryAsync_TrimsAndUpperCasesCode()
    {
        var f = await SeedAsync();

        var category = await f.Categories.CreateCategoryAsync(Input("  fresh-1 ", "  Fresh  ", 3));

        Assert.Equal("FRESH-1", category.Code);
        Assert.Equal("Fresh", category.Name);
        Assert.Equal(3, category.SortOrder);
        Assert.True(category.IsActive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-ABC")]
    [InlineData("AB C")]
    [InlineData("AB_C")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU")]
    public async Task CreateCategoryAsync_InvalidCode_Rejected(string code)
    {
        var f = await SeedAsync();

        Assert.Equal("code", await FieldOf(() => f.Categories.CreateCategoryAsync(Input(code, "Fresh"))));
        Assert.Empty(await f.Db.ProductCategories.ToListAsync());
    }

    [Fact]
    public async Task CreateCategoryAsync_TwentyCharacterCode_Accepted()
    {
        var f = await SeedAsync();

        var category = await f.Categories.CreateCategoryAsync(Input("ABCDEFGHIJKLMNOPQRST", "Long"));

        Assert.Equal(20, category.Code.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateCategoryAsync_BlankName_Rejected(string name)
    {
        var f = await SeedAsync();

        Assert.Equal("name", await FieldOf(() => f.Categories.CreateCategoryAsync(Input("FRESH", name))));
    }

    [Fact]
    public async Task CreateCategoryAsync_NameLongerThan60_Rejected()
    {
        var f = await SeedAsync();

        Assert.Equal("name", await FieldOf(() => f.Categories.CreateCategoryAsync(Input("FRESH", new string('n', 61)))));
        Assert.NotNull(await f.Categories.CreateCategoryAsync(Input("FRESH", new string('n', 60))));
    }

    [Fact]
    public async Task CreateCategoryAsync_NegativeSortOrder_Rejected()
    {
        var f = await SeedAsync();

        Assert.Equal("sortOrder", await FieldOf(() => f.Categories.CreateCategoryAsync(Input("FRESH", "Fresh", -1))));
    }

    [Theory]
    [InlineData("fresh")]
    [InlineData(" FRESH ")]
    public async Task CreateCategoryAsync_DuplicateCode_CaseAndSpaceVariants_Rejected(string code)
    {
        var f = await SeedAsync();
        await f.Categories.CreateCategoryAsync(Input("FRESH", "Fresh"));

        Assert.Equal("code", await FieldOf(() => f.Categories.CreateCategoryAsync(Input(code, "Another"))));
    }

    [Theory]
    [InlineData("fresh")]
    [InlineData("  FRESH  ")]
    [InlineData("FrEsH")]
    public async Task CreateCategoryAsync_DuplicateName_CaseAndSpaceVariants_Rejected(string name)
    {
        var f = await SeedAsync();
        await f.Categories.CreateCategoryAsync(Input("FRESH", "Fresh"));

        Assert.Equal("name", await FieldOf(() => f.Categories.CreateCategoryAsync(Input("OTHER", name))));
    }

    [Fact]
    public async Task UpdateCategoryAsync_KeepingOwnCodeAndName_IsAllowed()
    {
        var f = await SeedAsync();
        var category = await f.Categories.CreateCategoryAsync(Input("FRESH", "Fresh"));

        var updated = await f.Categories.UpdateCategoryAsync(category.Id, Input("fresh", "FRESH", 7));

        Assert.Equal(7, updated!.SortOrder);
        Assert.Equal("FRESH", updated.Name);
    }

    [Fact]
    public async Task UpdateCategoryAsync_ToAnotherCategorysCodeOrName_Rejected()
    {
        var f = await SeedAsync();
        await f.Categories.CreateCategoryAsync(Input("FRESH", "Fresh"));
        var dry = await f.Categories.CreateCategoryAsync(Input("DRY", "Dry Goods"));

        Assert.Equal("code", await FieldOf(() => f.Categories.UpdateCategoryAsync(dry.Id, Input("fresh", "Dry Goods"))));
        Assert.Equal("name", await FieldOf(() => f.Categories.UpdateCategoryAsync(dry.Id, Input("DRY", " fresh "))));
    }

    [Fact]
    public async Task UpdateCategoryAsync_Unknown_ReturnsNull()
    {
        var f = await SeedAsync();

        Assert.Null(await f.Categories.UpdateCategoryAsync(Guid.NewGuid(), Input("X", "X")));
    }

    [Fact]
    public async Task CreateSubcategoryAsync_SameCodeAndNameAllowedUnderAnotherCategory_NotWithinOne()
    {
        var f = await SeedAsync();
        var a = await f.Categories.CreateCategoryAsync(Input("A", "Alpha"));
        var b = await f.Categories.CreateCategoryAsync(Input("B", "Beta"));
        await f.Categories.CreateSubcategoryAsync(a.Id, Input("milk", "Milk"));

        Assert.NotNull(await f.Categories.CreateSubcategoryAsync(b.Id, Input("MILK", "milk")));
        Assert.Equal("code", await FieldOf(() => f.Categories.CreateSubcategoryAsync(a.Id, Input("MILK", "Other"))));
        Assert.Equal("name", await FieldOf(() => f.Categories.CreateSubcategoryAsync(a.Id, Input("OTHER", " MILK "))));
    }

    [Fact]
    public async Task CreateSubcategoryAsync_UnderAnInactiveCategory_Is400OnCategoryId_AndWorksOnceReactivated()
    {
        var f = await SeedAsync();
        var category = await f.Categories.CreateCategoryAsync(Input("DAIRY", "Dairy"));
        await f.Categories.SetCategoryActiveAsync(category.Id, false);

        Assert.Equal("categoryId", await FieldOf(() => f.Categories.CreateSubcategoryAsync(category.Id, Input("MILK", "Milk"))));
        Assert.Empty(f.Db.ProductSubcategories.Where(s => s.CategoryId == category.Id));

        await f.Categories.SetCategoryActiveAsync(category.Id, true);
        Assert.NotNull(await f.Categories.CreateSubcategoryAsync(category.Id, Input("MILK", "Milk")));
    }

    [Fact]
    public async Task CreateSubcategoryAsync_UnknownCategory_ReturnsNull()
    {
        var f = await SeedAsync();

        Assert.Null(await f.Categories.CreateSubcategoryAsync(Guid.NewGuid(), Input("X", "X")));
    }

    [Fact]
    public async Task UpdateAndSetStatusSubcategory_UnderAnotherCategory_ReturnsNull()
    {
        var f = await SeedAsync();
        var a = await f.Categories.CreateCategoryAsync(Input("A", "Alpha"));
        var b = await f.Categories.CreateCategoryAsync(Input("B", "Beta"));
        var sub = (await f.Categories.CreateSubcategoryAsync(a.Id, Input("S", "Sub")))!;

        Assert.Null(await f.Categories.UpdateSubcategoryAsync(b.Id, sub.Id, Input("S", "Renamed")));
        Assert.Null(await f.Categories.SetSubcategoryActiveAsync(b.Id, sub.Id, false));
        Assert.Equal("Sub", (await f.Db.ProductSubcategories.SingleAsync()).Name);
    }

    // ------------------------------------------------------------------ status rules

    [Fact]
    public async Task SetSubcategoryActiveAsync_ActivatingUnderInactiveCategory_RejectedOnIsActive()
    {
        var f = await SeedAsync();
        var category = await f.Categories.CreateCategoryAsync(Input("A", "Alpha"));
        var sub = (await f.Categories.CreateSubcategoryAsync(category.Id, Input("S", "Sub")))!;
        await f.Categories.SetSubcategoryActiveAsync(category.Id, sub.Id, false);
        await f.Categories.SetCategoryActiveAsync(category.Id, false);

        Assert.Equal("isActive", await FieldOf(() => f.Categories.SetSubcategoryActiveAsync(category.Id, sub.Id, true)));
        Assert.False((await f.Db.ProductSubcategories.SingleAsync()).IsActive);

        await f.Categories.SetCategoryActiveAsync(category.Id, true);
        Assert.True((await f.Categories.SetSubcategoryActiveAsync(category.Id, sub.Id, true))!.IsActive);
    }

    [Fact]
    public async Task SetCategoryActiveAsync_Deactivate_KeepsProductsAndSubcategories()
    {
        var f = await SeedAsync();
        var category = await f.Categories.CreateCategoryAsync(Input("A", "Alpha"));
        var sub = (await f.Categories.CreateSubcategoryAsync(category.Id, Input("S", "Sub")))!;
        var product = await f.Products.CreateProductAsync(ProductInput(f, "P1", category.Id, sub.Id));

        var result = await f.Categories.SetCategoryActiveAsync(category.Id, false);

        Assert.False(result!.IsActive);
        var stored = await f.Db.Products.SingleAsync(p => p.Id == product.Id);
        Assert.Equal(category.Id, stored.CategoryId);
        Assert.Equal(sub.Id, stored.SubcategoryId);
        Assert.Equal("Alpha", stored.Category);
        Assert.True((await f.Db.ProductSubcategories.SingleAsync()).IsActive);
    }

    // ------------------------------------------------------------------ product assignment

    [Fact]
    public async Task CreateProductAsync_WithCategoryAndSubcategory_MirrorsCategoryName()
    {
        var f = await SeedAsync();
        var category = await f.Categories.CreateCategoryAsync(Input("A", "Alpha"));
        var sub = (await f.Categories.CreateSubcategoryAsync(category.Id, Input("S", "Sub")))!;

        var product = await f.Products.CreateProductAsync(ProductInput(f, "P1", category.Id, sub.Id));

        Assert.Equal(category.Id, product.CategoryId);
        Assert.Equal(sub.Id, product.SubcategoryId);
        Assert.Equal("Alpha", product.Category);
    }

    [Fact]
    public async Task CreateProductAsync_WithoutCategory_HasNoMirror()
    {
        var f = await SeedAsync();

        var product = await f.Products.CreateProductAsync(ProductInput(f, "P1", null, null));

        Assert.Null(product.CategoryId);
        Assert.Null(product.Category);
    }

    [Fact]
    public async Task CreateProductAsync_UnknownCategory_RejectedOnCategoryId()
    {
        var f = await SeedAsync();

        Assert.Equal("categoryId", await FieldOf(() => f.Products.CreateProductAsync(ProductInput(f, "P1", Guid.NewGuid(), null))));
        Assert.Empty(await f.Db.Products.ToListAsync());
    }

    [Fact]
    public async Task CreateProductAsync_SubcategoryOfAnotherCategory_RejectedOnSubcategoryId()
    {
        var f = await SeedAsync();
        var a = await f.Categories.CreateCategoryAsync(Input("A", "Alpha"));
        var b = await f.Categories.CreateCategoryAsync(Input("B", "Beta"));
        var subOfB = (await f.Categories.CreateSubcategoryAsync(b.Id, Input("S", "Sub")))!;

        Assert.Equal("subcategoryId", await FieldOf(() => f.Products.CreateProductAsync(ProductInput(f, "P1", a.Id, subOfB.Id))));
        Assert.Equal("subcategoryId", await FieldOf(() => f.Products.CreateProductAsync(ProductInput(f, "P2", a.Id, Guid.NewGuid()))));
    }

    [Fact]
    public async Task CreateProductAsync_SubcategoryWithoutCategory_RejectedOnSubcategoryId()
    {
        var f = await SeedAsync();
        var category = await f.Categories.CreateCategoryAsync(Input("A", "Alpha"));
        var sub = (await f.Categories.CreateSubcategoryAsync(category.Id, Input("S", "Sub")))!;

        Assert.Equal("subcategoryId", await FieldOf(() => f.Products.CreateProductAsync(ProductInput(f, "P1", null, sub.Id))));
    }

    [Fact]
    public async Task CreateProductAsync_InactiveCategoryOrSubcategory_NotNewlyAssignable()
    {
        var f = await SeedAsync();
        var inactive = await f.Categories.CreateCategoryAsync(Input("OLD", "Old"));
        var active = await f.Categories.CreateCategoryAsync(Input("NEW", "New"));
        var inactiveSub = (await f.Categories.CreateSubcategoryAsync(active.Id, Input("S", "Sub")))!;
        await f.Categories.SetCategoryActiveAsync(inactive.Id, false);
        await f.Categories.SetSubcategoryActiveAsync(active.Id, inactiveSub.Id, false);

        Assert.Equal("categoryId", await FieldOf(() => f.Products.CreateProductAsync(ProductInput(f, "P1", inactive.Id, null))));
        Assert.Equal("subcategoryId", await FieldOf(() => f.Products.CreateProductAsync(ProductInput(f, "P2", active.Id, inactiveSub.Id))));
    }

    [Fact]
    public async Task UpdateProductAsync_UnchangedInactiveMasters_AreKept()
    {
        var f = await SeedAsync();
        var category = await f.Categories.CreateCategoryAsync(Input("A", "Alpha"));
        var sub = (await f.Categories.CreateSubcategoryAsync(category.Id, Input("S", "Sub")))!;
        var product = await f.Products.CreateProductAsync(ProductInput(f, "P1", category.Id, sub.Id));
        await f.Categories.SetSubcategoryActiveAsync(category.Id, sub.Id, false);
        await f.Categories.SetCategoryActiveAsync(category.Id, false);

        var edit = ProductInput(f, "P1", category.Id, sub.Id) with { Name = "Renamed item", Units = UnitRows(product) };
        var updated = await f.Products.UpdateProductAsync(product.Id, edit);

        Assert.Equal(category.Id, updated!.CategoryId);
        Assert.Equal(sub.Id, updated.SubcategoryId);
        Assert.Equal("Renamed item", updated.Name);
    }

    [Fact]
    public async Task UpdateProductAsync_SwitchingToInactiveCategory_Rejected_ClearingAllowed()
    {
        var f = await SeedAsync();
        var current = await f.Categories.CreateCategoryAsync(Input("A", "Alpha"));
        var inactive = await f.Categories.CreateCategoryAsync(Input("B", "Beta"));
        await f.Categories.SetCategoryActiveAsync(inactive.Id, false);
        var product = await f.Products.CreateProductAsync(ProductInput(f, "P1", current.Id, null));

        var toInactive = ProductInput(f, "P1", inactive.Id, null) with { Units = UnitRows(product) };
        Assert.Equal("categoryId", await FieldOf(() => f.Products.UpdateProductAsync(product.Id, toInactive)));

        var cleared = ProductInput(f, "P1", null, null) with { Units = UnitRows(product) };
        var updated = await f.Products.UpdateProductAsync(product.Id, cleared);
        Assert.Null(updated!.CategoryId);
        Assert.Null(updated.Category);
    }

    [Fact]
    public async Task UpdateCategoryAsync_Rename_UpdatesMirrorOnItsProductsOnly()
    {
        var f = await SeedAsync();
        var a = await f.Categories.CreateCategoryAsync(Input("A", "Alpha"));
        var b = await f.Categories.CreateCategoryAsync(Input("B", "Beta"));
        var inA = await f.Products.CreateProductAsync(ProductInput(f, "P1", a.Id, null));
        var inB = await f.Products.CreateProductAsync(ProductInput(f, "P2", b.Id, null));

        await f.Categories.UpdateCategoryAsync(a.Id, Input("A", "Alpha Renamed"));

        Assert.Equal("Alpha Renamed", (await f.Db.Products.SingleAsync(p => p.Id == inA.Id)).Category);
        Assert.Equal("Beta", (await f.Db.Products.SingleAsync(p => p.Id == inB.Id)).Category);
    }

    private static List<ProductUnitInputItem> UnitRows(Product product) => product.Units
        .Select(u => new ProductUnitInputItem(u.Id, u.UnitId, null, 1m, u.Label, null, true, true, false, false, true)).ToList();
}
