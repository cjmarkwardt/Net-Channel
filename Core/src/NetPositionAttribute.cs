namespace Markwardt.NetChannel;

/// <summary>
/// Marks a model interface property of type <see cref="NetPosition"/> (nullable or not) as the entity's
/// position and visibility range for position-based visibility filtering. <see cref="INetConnection.Position"/>
/// and <see cref="INetView.SetViewerPosition"/> have the same meaning for a connection as a viewer, globally and
/// scoped to a single view respectively. A model interface may declare at most one property with this
/// attribute, and only on a property of the required type; either violation fails interface validation (see
/// <see cref="NetInvalidInterfaceException"/>) the first time the interface is used.
/// </summary>
/// <remarks>
/// Position-based visibility is a filter on top of a group's existing visibility (<see cref="INetGroup"/>):
/// an entity must already be visible to a connection by being in a group that connection is a viewer of
/// before this filtering is applied at all — it can only take visibility away, never grant it.
/// <para/>
/// For a given connection viewing a given entity within a view, the effective position used for that
/// connection is the view's own override (<see cref="INetView.GetViewerPosition"/>) if one is set there, otherwise
/// the connection's global <see cref="INetConnection.Position"/>.
/// <para/>
/// The filter is a hysteresis, driven by whichever visibility state (visible or not) the connection-entity
/// pairing is already in, rather than a single stateless threshold — this is what <see cref="NetRange"/>'s
/// two distances are for, and it's what prevents an entity hovering near a single boundary from rapidly
/// flickering in and out of visibility:
/// <list type="bullet">
/// <item>An invisible pairing becomes visible only once the distance between the entity's position and the
/// connection's effective position is within both sides' <see cref="NetRange.EnterDistance"/> (checked
/// independently — a side with no position, or no <see cref="NetPosition.Range"/>, imposes no constraint of
/// its own).</item>
/// <item>A visible pairing becomes invisible only once that distance exceeds either side's
/// <see cref="NetRange.ExitDistance"/> (checked independently, so exceeding just one of them, when set, is
/// enough).</item>
/// <item>Otherwise, the pairing's visibility is left exactly as it already was.</item>
/// </list>
/// If either side has no position (or the entity's model declares no <see cref="NetPositionAttribute"/>
/// property), this filter does not apply at all — visibility is governed purely by group membership. Unlike
/// the entity's position, which is ordinary model state synced to every connection it's visible to, a
/// connection's own position/range is purely local to whichever manager set it (see
/// <see cref="INetConnection.Position"/>) — it is never transmitted to that connection's remote peer.
/// <para/>
/// A <see langword="null"/> <see cref="NetPosition.Range"/> on either side means that side never contributes
/// an enter or exit constraint. If both are <see langword="null"/> (or absent), position-based visibility has
/// no effect regardless of position or distance.
/// <para/>
/// Whenever a connection's or a view's overriding position changes, or an entity's position property value
/// changes, every connection-entity pairing this could affect is re-evaluated: a pairing newly becoming
/// visible triggers an entity creation for that connection, and one newly becoming invisible triggers its
/// destruction, exactly as if the entity had been added to or removed from the group.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NetPositionAttribute : Attribute;
