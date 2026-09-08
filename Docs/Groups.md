# Groups and Views

This document explains `INetGroup`, `INetView`, and `INetManager.All` — how local entities become visible to
connections. An entity relationship only ever exists between a local entity's owner and its direct
connections; there is no way to relay an entity onward through an intermediate peer. See
[Entities.md](Entities.md) for the entity/model/controller concepts these expose, and [Access.md](Access.md)
for what happens once a connection can see an entity.

## The concept: a group is an audience

Every group tracks two things: a set of local entities, and an audience of connections those entities are
exposed to. Adding an entity to a group doesn't target specific connections directly — it targets whichever
connections are currently in the group's audience, and that audience can keep changing independently
afterward (viewers added/removed, connections dropping) without touching the entity list at all.

`INetGroup` is the shared contract for "a set of entities exposed to an audience." It's implemented two
different ways:

- `INetView` — an explicit, caller-managed audience. You create one with `INetManager.CreateView`, and
  you decide exactly which connections are in it via `AddViewers`/`RemoveViewers`. Nothing is in a view's
  audience until you put it there.
- `INetManager.All` — an implicit audience: every currently active connection, automatically, with no
  viewer list to manage at all. A connection that becomes active is immediately part of `All`'s audience; one
  that stops being active immediately isn't. There's exactly one `All` per manager — it isn't created, just
  referenced.

Both give you the same `INetGroup` surface (`Entities`, `Add`, `Remove`) — the only difference is how the
audience itself is managed.

## Adding local entities

`Add<TModel, TController>(entities)` exposes local entities (`INetEntity<TModel, TController>`, see
[Entities.md](Entities.md)) to the group's current audience. `TModel`/`TController` are supplied explicitly as
generic arguments by the caller — not discovered by inspecting each entity's runtime type — so one `Add` call
handles entities of exactly one model/controller pairing; entities of a different pairing need their own
call. The first time a given pairing is used anywhere (`Add` or `Listen`), it's validated once and
the result cached; an interface that doesn't fit its required shape (see [Entities.md](Entities.md#interface-validation))
throws `NetInvalidInterfaceException` at that point.

The first time a specific entity becomes visible to a specific connection in the audience:

1. That connection is assigned a sequential id for the entity itself.
2. If the connection hasn't seen the entity's model interface type before, it's assigned a sequential id for
   that type; likewise for the controller interface type if it hasn't been seen before. These two type-id
   sequences, and the entity-id sequence, are all independent of each other and scoped per connection — two
   different connections viewing the same entity assign their own ids for it, unrelated to each other.
3. An entity creation message is sent to that connection carrying the entity id, the model/controller type
   ids, each type's full name (only the first time that type's id is used on that connection — a connection
   that already knows a type from an earlier entity just gets the id, not the name again), and the entity's
   model property values that differ from their default.

`Remove(entities)` takes entities back out of the group, which removes their visibility from every
connection in the audience (an entity destruction message per connection, per [Wire.md](Wire.md#entities)).
A connection's entity id is never reused for a different entity afterward; an entity that becomes visible to
that connection again later is created under a new id, as a fresh entity from the connection's point of view. Calling `INetEntity.Destroy()`
does the same thing across every group at once, so a destroyed entity never stays exposed anywhere — see
[Entities.md](Entities.md#inetentity-and-inetentitytmodel-tcontroller).

Because visibility is audience-driven, an entity's visibility can also change purely from the audience side —
adding a viewer to a view exposes every entity already in that view to the new viewer, and removing one
retracts every entity in that view from them, without any `Add`/`Remove` call at all.

## View-specific members

Beyond the shared `INetGroup` surface, `INetView` adds the pieces needed to manage an explicit audience:

- `Tag` — the arbitrary value the view was created with (or `null`), usable later to look the view back
  up via `INetManager.GetView`.
- `Viewers` — the connections currently in the view's audience.
- `AddViewers`/`RemoveViewers` — add/remove connections from that audience. A connection is removed
  automatically, from every view it's in, the instant it drops to `NetStatus.Disconnected` — whether from a
  local `Drop`/`Disconnect` or a remote failure — so a lost connection can never linger in a view's audience
  waiting to be cleaned up manually.
- `GetViewerPosition`/`SetViewerPosition` — a per-view position/range override for a connection, used for
  position-based visibility filtering ([Api.md](Api.md#position-based-visibility)) instead of that
  connection's global position, scoped to just this view. Like the global position it overrides, this is
  purely local to this manager and never transmitted to the connection's remote peer.
- `Destroy` — destroys the view entirely, retracting every entity in it from every viewer.

A connection is not one-directional: both peers can own entities and expose them over the same connection, in
which case each also views the other's. Each side numbers the entities it owns independently, so the same
entity id means a different entity depending on which side sent it — see [Wire.md](Wire.md#entities).

`All` has none of these, since it has no audience to manage — its audience is always exactly "every active
connection," which is precisely what makes it convenient for anything that should be globally visible
without bothering to track viewers at all.
