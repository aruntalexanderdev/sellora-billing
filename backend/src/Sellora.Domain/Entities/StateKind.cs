namespace Sellora.Domain.Entities;

/// <summary>Display-only classification of a first-level subdivision. Persisted as a string.</summary>
public enum StateKind
{
    State,
    UnionTerritory,
    Emirate,
    Region,
    Governorate,
    Municipality,
}
