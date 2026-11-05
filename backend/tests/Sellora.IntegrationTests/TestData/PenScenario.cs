using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.IntegrationTests.TestData;

/// <summary>
/// Unit Master rows (Piece, Box, Half Carton, Full Carton, Kilogram, Gram, Bag), a 12% tax, an admin user and the
/// canonical "Ball Pen" (Piece 1, Box 10, Half Carton 50, Full Carton 100) created through the real
/// <see cref="ProductService"/> on a migrated database.
/// </summary>
public sealed record PenScenario(Guid PenId, Guid TaxId, Guid UserId, Guid Pc, Guid Box, Guid HCtn, Guid FCtn, Guid Kg, Guid Gram, Guid Bag)
{
    public const string PenSku = "STN-PEN";

    public static async Task<PenScenario> SeedAsync(SelloraDbContext db)
    {
        static Unit U(string name, string code, MeasurementCategory category, short dp, decimal? sf) =>
            new() { Id = Guid.NewGuid(), Name = name, ShortCode = code, MeasurementCategory = category, DecimalPlaces = dp, StandardFactor = sf };
        var pc = U("Piece", "pc", MeasurementCategory.Count, 0, null);
        var box = U("Box", "box", MeasurementCategory.Packaging, 0, null);
        var hctn = U("Half Carton", "hctn", MeasurementCategory.Packaging, 0, null);
        var fctn = U("Full Carton", "fctn", MeasurementCategory.Packaging, 0, null);
        var kg = U("Kilogram", "kg", MeasurementCategory.Weight, 3, 1m);
        var g = U("Gram", "g", MeasurementCategory.Weight, 0, 0.001m);
        var bag = U("Bag", "bag", MeasurementCategory.Packaging, 0, null);
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST 12%", RatePercent = 12m };
        var role = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = "Ravi", LastName = "Krishnan", Username = "admin",
            Email = "admin@selloramart.in", PasswordHash = "x", RoleId = role.Id, Role = role,
        };
        db.AddRange(pc, box, hctn, fctn, kg, g, bag, tax, role, user);
        await db.SaveChangesAsync();

        var scenario = new PenScenario(Guid.Empty, tax.Id, user.Id, pc.Id, box.Id, hctn.Id, fctn.Id, kg.Id, g.Id, bag.Id);
        var pen = await new ProductService(db).CreateProductAsync(scenario.Input(PenSku, pc.Id,
            Row(pc.Id, null, 1m), Row(box.Id, pc.Id, 10m), Row(hctn.Id, box.Id, 5m), Row(fctn.Id, hctn.Id, 2m)));
        return scenario with { PenId = pen.Id };
    }

    public static ProductUnitInputItem Row(Guid unitId, Guid? innerUnitId, decimal? qty, Guid? id = null, string? barcode = null,
        bool active = true, bool defaultSales = false) =>
        new(id, unitId, innerUnitId, qty, null, barcode, true, true, false, defaultSales, active);

    public UpsertProductInput Input(string sku, Guid baseUnitId, params ProductUnitInputItem[] units) =>
        new(sku, "Ball Pen " + sku, "Stationery", baseUnitId, TaxId, 0m, units);

    /// <summary>The product's active stored rows as the editor would resubmit them, keyed by UnitId.</summary>
    public static async Task<Dictionary<Guid, ProductUnitInputItem>> CurrentAsync(SelloraDbContext db, Guid productId)
    {
        var rows = await db.ProductUnits.AsNoTracking().Where(u => u.ProductId == productId).ToListAsync();
        var byId = rows.ToDictionary(r => r.Id);
        return rows.Where(r => r.IsActive).ToDictionary(r => r.UnitId, r => new ProductUnitInputItem(
            r.Id, r.UnitId, r.InnerProductUnitId is { } i ? byId[i].UnitId : null, r.InnerQuantity, r.Label, r.Barcode,
            r.IsPurchasable, r.IsSellable, r.IsDefaultPurchaseUnit, r.IsDefaultSalesUnit, r.IsActive));
    }

    public Task<Product?> UpdatePenAsync(SelloraDbContext db, params ProductUnitInputItem[] units) =>
        new ProductService(db).UpdateProductAsync(PenId, Input(PenSku, Pc, units));

    public static async Task<ProductUnit> ActiveRowAsync(SelloraDbContext db, Guid productId, Guid unitId) =>
        await db.ProductUnits.AsNoTracking().SingleAsync(u => u.ProductId == productId && u.UnitId == unitId && u.IsActive);
}
