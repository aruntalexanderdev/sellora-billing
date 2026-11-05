using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
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
    public static Unit Bag() => New("Bag", "bag", MeasurementCategory.Packaging, 0, null);
    public static Unit Bottle() => New("Bottle", "btl", MeasurementCategory.Packaging, 0, null);
    public static Unit Case() => New("Case", "case", MeasurementCategory.Packaging, 0, null);

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

/// <summary>
/// Minimal valid <see cref="Product"/>. Without <see cref="WithUnits"/> or <see cref="WithBaseUnitRow"/> the product
/// has no <see cref="ProductUnit"/> rows (enough for tests that only need a product to reference a base unit).
/// </summary>
public sealed class ProductBuilder
{
    private string _sku = "SKU-" + Guid.NewGuid().ToString("N")[..8];
    private string _name = "Test product";
    private Unit? _baseUnit;
    private Tax? _tax;
    private decimal _reorderLevel;
    private bool _isActive = true;
    private readonly List<Action<Product>> _units = [];

    public ProductBuilder WithSku(string sku) { _sku = sku; return this; }
    public ProductBuilder WithName(string name) { _name = name; return this; }
    public ProductBuilder WithBaseUnit(Unit unit) { _baseUnit = unit; return this; }
    public ProductBuilder WithTax(Tax tax) { _tax = tax; return this; }
    public ProductBuilder WithReorderLevel(decimal reorderLevel) { _reorderLevel = reorderLevel; return this; }
    public ProductBuilder Inactive() { _isActive = false; return this; }

    /// <summary>Adds the base <see cref="ProductUnit"/> row (factor 1) when the product is built.</summary>
    public ProductBuilder WithBaseUnitRow(string? label = null) =>
        WithUnits(p => TestProductUnits.Base(p, label));

    /// <summary>
    /// Configures the unit hierarchy once the product exists, e.g.
    /// <c>.WithUnits(p => { var pc = TestProductUnits.Base(p); TestProductUnits.Pack(p, box, pc, 10); })</c>.
    /// Rows created through <see cref="TestProductUnits"/> are attached to <c>product.Units</c>.
    /// </summary>
    public ProductBuilder WithUnits(Action<Product> configure) { _units.Add(configure); return this; }

    public Product Build()
    {
        var unit = _baseUnit ?? throw new InvalidOperationException("ProductBuilder needs a base unit (WithBaseUnit).");
        var tax = _tax ?? TestTaxes.Gst5();
        var product = new Product
        {
            Id = Guid.NewGuid(), Sku = _sku, Name = _name,
            BaseUnitId = unit.Id, BaseUnit = unit, TaxId = tax.Id, Tax = tax, ReorderLevel = _reorderLevel, IsActive = _isActive,
        };
        foreach (var configure in _units) configure(product);
        return product;
    }
}

/// <summary>
/// <see cref="ProductUnit"/> rows for test fixtures, attached to <c>product.Units</c> and carrying the real
/// <see cref="Unit"/> navigation (EF InMemory drops rows whose required <c>Unit</c> is missing when a query
/// includes it, so fixtures must always add real Unit rows; attaching the navigation does that on <c>Add</c>).
/// <para>Factors are computed with the domain <see cref="ProductUnitGraph"/> over the product's rows, i.e. the same
/// materialisation the product service performs on save.</para>
/// </summary>
public static class TestProductUnits
{
    /// <summary>The base row: unit = product base unit, no inner, InnerQuantity 1, factor 1.</summary>
    public static ProductUnit Base(Product product, string? label = null)
    {
        var unit = product.BaseUnit ?? throw new InvalidOperationException("TestProductUnits.Base needs product.BaseUnit set.");
        var row = new ProductUnit
        {
            Id = Guid.NewGuid(), ProductId = product.Id, Product = product, UnitId = unit.Id, Unit = unit,
            InnerProductUnitId = null, InnerQuantity = 1m, ConversionFactorToBase = 1m, IsBaseUnit = true, Label = label,
            SortOrder = (short)product.Units.Count,
        };
        product.Units.Add(row);
        return row;
    }

    /// <summary>"1 <paramref name="unit"/> contains <paramref name="innerQuantity"/> <paramref name="inner"/>".</summary>
    public static ProductUnit Pack(Product product, Unit unit, ProductUnit inner, decimal innerQuantity, string? label = null)
    {
        if (inner.ProductId != product.Id) throw new InvalidOperationException("Inner product unit belongs to another product.");
        var row = new ProductUnit
        {
            Id = Guid.NewGuid(), ProductId = product.Id, Product = product, UnitId = unit.Id, Unit = unit,
            InnerProductUnitId = inner.Id, InnerQuantity = innerQuantity, IsBaseUnit = false, Label = label,
            SortOrder = (short)product.Units.Count,
        };
        var nodes = product.Units.Append(row).Select(u => new ProductUnitGraph.Node(u.Id, u.InnerProductUnitId, u.InnerQuantity));
        var result = ProductUnitGraph.Compute(nodes)[row.Id];
        row.ConversionFactorToBase = result is { Problem: ProductUnitGraph.Problem.None, Factor: { } factor }
            ? factor
            : throw new InvalidOperationException($"Invalid test hierarchy for {unit.ShortCode}: {result.Problem}.");
        product.Units.Add(row);
        return row;
    }
}

public static class TestTaxes
{
    public static Tax Gst5() => new() { Id = Guid.NewGuid(), Name = "GST 5%", RatePercent = 5m };
}
