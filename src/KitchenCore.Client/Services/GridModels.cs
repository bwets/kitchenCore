using KitchenCore.Shared;

namespace KitchenCore.Client.Services;

/// <summary>Points at one entry in the grid: its cell, and which of the cell's entries.</summary>
public sealed record MenuEntryRef(DateOnly Date, string Slot, int Index, MenuEntry Entry);

/// <summary>
/// A moment drawn across a day column rather than occupying a slot -- currently
/// the shopping order deadline and delivery. <see cref="Kind"/> becomes a CSS
/// modifier, so the colours stay in the theme where every other colour lives.
/// </summary>
public sealed record DayMarker(DateTime At, string Kind, string Label);
