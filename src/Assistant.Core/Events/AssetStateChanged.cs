using Assistant.Core.Assets;

namespace Assistant.Core.Events;

/// <summary>A packaged asset's state changed: its check began (<see cref="AssetGroupStatus.Checking"/>) or ended.</summary>
/// <param name="Kind">Which kind of asset it is.</param>
/// <param name="State">Its new state.</param>
public sealed record AssetStateChanged(AssetKind Kind, AssetGroupState State);
