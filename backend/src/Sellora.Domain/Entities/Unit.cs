namespace Sellora.Domain.Entities;

public class Unit
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string ShortCode { get; set; }
    public MeasurementCategory MeasurementCategory { get; set; }

    /// <summary>Allowed decimal places of quantities entered in this unit (0–3). Always 0 for Count and Packaging.</summary>
    public short DecimalPlaces { get; set; }

    /// <summary>
    /// Factor relative to the category reference unit (kg / L / m). Only for measurable categories
    /// (Weight, Volume, Length); null otherwise.
    /// </summary>
    public decimal? StandardFactor { get; set; }

    public bool IsActive { get; set; } = true;
}
