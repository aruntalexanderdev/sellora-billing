using Sellora.Domain.Entities;

namespace Sellora.Domain.Inventory;

/// <summary>A unit as submitted from the Unit Master form. Name and ShortCode are trimmed by the service.</summary>
public sealed record UpsertUnitInput(
    string Name,
    string ShortCode,
    MeasurementCategory MeasurementCategory,
    short DecimalPlaces,
    decimal? StandardFactor
);
