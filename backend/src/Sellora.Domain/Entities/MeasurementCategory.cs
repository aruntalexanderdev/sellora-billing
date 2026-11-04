namespace Sellora.Domain.Entities;

/// <summary>
/// What a unit measures. Drives which units may be a product's base unit and which may wrap which.
/// </summary>
public enum MeasurementCategory
{
    /// <summary>Discrete base items (Piece, Dozen). Quantities are whole numbers.</summary>
    Count,

    /// <summary>Measurable mass. <see cref="Unit.StandardFactor"/> is relative to the kilogram.</summary>
    Weight,

    /// <summary>Measurable volume. <see cref="Unit.StandardFactor"/> is relative to the litre.</summary>
    Volume,

    /// <summary>Measurable length. <see cref="Unit.StandardFactor"/> is relative to the metre.</summary>
    Length,

    /// <summary>
    /// Containers (Box, Carton, Bag). Never a product base unit; may wrap a unit of any category.
    /// Quantities are whole numbers.
    /// </summary>
    Packaging,
}
