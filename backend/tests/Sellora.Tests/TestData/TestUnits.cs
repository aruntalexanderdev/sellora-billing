using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Tests.TestData;

/// <summary>Fresh, isolated EF InMemory contexts for service tests.</summary>
public static class TestDb
{
    public static SelloraDbContext NewInMemory()
    {
        var options = new DbContextOptionsBuilder<SelloraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SelloraDbContext(options);
    }
}

/// <summary>
/// Canonical Unit Master rows (mirroring the seeder: category, decimal places, standard factor). Each call returns
/// a new instance with a new id so tests never share tracked entities.
/// </summary>
public static class TestUnits
{
    public static Unit Kilogram() => New("Kilogram", "kg", MeasurementCategory.Weight, 3, 1m);
    public static Unit Gram() => New("Gram", "g", MeasurementCategory.Weight, 0, 0.001m);
    public static Unit Litre() => New("Litre", "litre", MeasurementCategory.Volume, 3, 1m);
    public static Unit Millilitre() => New("Millilitre", "ml", MeasurementCategory.Volume, 0, 0.001m);
    public static Unit Metre() => New("Metre", "m", MeasurementCategory.Length, 3, 1m);
    public static Unit Piece() => New("Piece", "pc", MeasurementCategory.Count, 0, null);
    public static Unit Box() => New("Box", "box", MeasurementCategory.Packaging, 0, null);
    public static Unit HalfCarton() => New("Half Carton", "hctn", MeasurementCategory.Packaging, 0, null);
    public static Unit FullCarton() => New("Full Carton", "fctn", MeasurementCategory.Packaging, 0, null);

    public static Unit New(string name, string shortCode, MeasurementCategory category, short decimalPlaces, decimal? standardFactor, bool isActive = true) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ShortCode = shortCode,
        MeasurementCategory = category,
        DecimalPlaces = decimalPlaces,
        StandardFactor = standardFactor,
        IsActive = isActive,
    };

    public static Unit Inactive(this Unit unit)
    {
        unit.IsActive = false;
        return unit;
    }
}

/// <summary>Minimal valid <see cref="Product"/> for tests that only need a product to reference a base unit.</summary>
public sealed class ProductBuilder
{
    private string _sku = "SKU-" + Guid.NewGuid().ToString("N")[..8];
    private string _name = "Test product";
    private Unit? _baseUnit;
    private Tax? _tax;
    private bool _isActive = true;

    public ProductBuilder WithSku(string sku) { _sku = sku; return this; }
    public ProductBuilder WithName(string name) { _name = name; return this; }
    public ProductBuilder WithBaseUnit(Unit unit) { _baseUnit = unit; return this; }
    public ProductBuilder WithTax(Tax tax) { _tax = tax; return this; }
    public ProductBuilder Inactive() { _isActive = false; return this; }

    public Product Build()
    {
        var unit = _baseUnit ?? throw new InvalidOperationException("ProductBuilder needs a base unit (WithBaseUnit).");
        var tax = _tax ?? TestTaxes.Gst5();
        return new Product
        {
            Id = Guid.NewGuid(), Sku = _sku, Name = _name,
            BaseUnitId = unit.Id, BaseUnit = unit, TaxId = tax.Id, Tax = tax, IsActive = _isActive,
        };
    }
}

public static class TestTaxes
{
    public static Tax Gst5() => new() { Id = Guid.NewGuid(), Name = "GST 5%", RatePercent = 5m };
}
