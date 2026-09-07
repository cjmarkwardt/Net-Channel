# Access Control

This document explains how a local entity's members are exposed to other peers, how they're delivered, and
how `[NetRole]`, `[NetAccess]`, and `[NetSecure]` govern that exposure. See [Entities.md](Entities.md) for the
entity/model/controller concepts these attributes are placed on.

## What's exposed, and through what

An entity exposes its model's properties and its controller's methods to other peers — see
[Entities.md](Entities.md) for what a model/controller interface is and how they're reached. A model
property's getter is always readable by any connection that can see the entity, unconditionally; the only
thing `[NetRole]`/`[NetAccess]` govern on a model interface is whether a property's *setter* can be used
remotely. On a controller interface, they govern whether a method can be called remotely at all. Both
attributes are placed on the property or method itself, never on an individual getter/setter — placing either
directly on one fails interface validation (see [Entities.md](Entities.md#interface-validation)).

## Visibility comes first

None of this applies until an entity is visible to a connection at all. Visibility is:

1. **Group membership** — the entity must have been added to a group that the connection is a viewer of; see
   [Groups.md](Groups.md).
2. **Position-based visibility** — a filter on top of group membership, narrowing it further if both the
   entity and the connection have a position set; see [Api.md](Api.md#position-based-visibility).

Only once both checks pass does a connection know the entity exists at all — at which point every model
property's current value is synced to it unconditionally, and stays synced as it changes; there is no way to
withhold a property's *value* from a connection that can see the entity. `[NetRole]`/`[NetAccess]` only ever
govern *setting* a property or *calling* a method, never *reading* one. Neither ever restores visibility that
group membership or position-based filtering already denied.

## Delivery

How a member is delivered isn't configurable — it follows fixed rules from what kind of member it is:

- Every property set — an owner's broadcast of a local change, or a viewer's request to change one — is sent
  fast and unreliably: no retransmission of a specific lost update, only a guarantee that the *latest* value
  eventually gets through even if older ones in between were dropped, superseded rather than queued behind
  (see [Wire.md](Wire.md#property-delivery) for how). This fits a property well, since it's state to converge
  on rather than a one-off event — losing an intermediate position update is harmless once a newer position
  has already superseded it.
- Every method call is guaranteed to arrive, and to be executed, in the order it was sent, despite UDP's own
  unreliability — a lost packet carrying it is retransmitted rather than silently dropped (see
  [Wire.md](Wire.md#method-delivery) for how). This fits a method well, since a call is a one-off event, not
  state to converge on: a chat message or a purchase must arrive exactly once, in order, not be silently
  replaced by whatever call happens to come after it.

## `[NetSecure]`

Marks a controller interface method's calls and results as encrypted, so that only the two endpoints of the
connection can read them (see [Wire.md](Wire.md#payload-encryption) for how) — everything else about how the
method is delivered is unchanged. Use it for anything sensitive, at the cost of the extra encryption work.
Example: a password login.

- Placement: a controller interface **method** only. A model interface property — including an individual
  getter/setter of one — can never be marked `[NetSecure]`, since a property set is never encrypted; doing so
  fails interface validation (see [Entities.md](Entities.md#interface-validation)) with a
  `NetInvalidInterfaceException`.
- **Default when absent:** the call and its result are sent unencrypted.

## `[NetRole(role)]`

Restricts setting a property or calling a method to connections that hold a specific role, for authorization
that's coarser than "visible or not" but doesn't need a full custom permission system:

- A role is just a caller-defined string with no meaning to Net-Channel itself (e.g. `"Admin"`, `"GameMaster"`,
  `"Moderator"`) — the application decides what roles exist and what they're named.
- A connection holds a role only once it's been explicitly granted one via `INetConnection.Promote(role)`
  (and loses it via `Demote(role)`); holding a role is entirely independent of group/view membership — a
  connection can see an entity without holding any of its roles, and `Promote`/`Demote` don't affect
  visibility at all, only whether a set/call restricted by that role can be made once the entity is already
  visible. Since roles are tracked per connection, promoting one connection to `"Admin"` has no effect on any
  other connection.
- `[NetRole(role)]` on a property or method means: a `set`/call attempted by a connection that has not been
  `Promote`d with that exact role string fails (reported via `INetManager.Failed`, per below), regardless of
  whether `[NetAccess]` would otherwise allow it. Only one role can be required per member — `[NetRole]` isn't
  repeatable, so a member can't require, say, both `"Admin"` and `"Moderator"` at once.
- Placement: a model interface **property** (governing its setter — the getter is never role-restricted) or
  a controller interface **method** — never an individual getter/setter, which fails interface validation
  (see [Entities.md](Entities.md#interface-validation)).
- **Default when absent: no role required** — any connection that can see the entity may set/call the member.

## `[NetAccess(hasAccess)]`

Grants or denies other peers the ability to set a property or call a method, independent of role — a simple
on/off switch checked before role is even considered:

- `HasAccess: true` on a property means a connection that can already see the entity (per "Visibility comes
  first" above) may send a `set` request for it at all, subject to whatever `[NetRole]` says next; on a
  method, that it can be called remotely at all.
- `HasAccess: false` means the setter/method is entirely unusable by every other peer, unconditionally — not
  even a connection holding every possible role can use it, since role is only checked *after* access is
  confirmed. A denied setter still has its *value* synced normally (see "Visibility comes first" — reading is
  never restricted); only remote `set` requests for it are refused. A denied method can never be invoked
  remotely. Either way, the local entity owner can still read/write/call it directly (e.g. via
  `INetState<TModel>.Model`), since `[NetAccess]` only restricts remote peers, never local code.
- Placement: a model interface **property** (governing its setter) or a controller interface **method** —
  never an individual getter/setter, which fails interface validation (see
  [Entities.md](Entities.md#interface-validation)).
- **Defaults when absent:**
  - Methods: `true`.
  - Property setters: `false`.

  So a controller's methods are callable by default, but a property setter needs an explicit
  `[NetAccess(true)]` before another peer can set it. A property's getter has no equivalent default to
  configure, since it is always readable regardless of either attribute.

## How access and role combine

For a given property setter or method, once the entity is visible to a connection:

1. Resolve `HasAccess` from `[NetAccess]` on it, falling back to the default (`true` for methods, `false` for
   property setters). If `false`, the set/call fails.
2. Resolve the required role from `[NetRole]` on it, falling back to "none". If the calling connection hasn't
   been `Promote`d with that role, the set/call fails.
3. Otherwise, it proceeds, delivered per "Delivery" above (and encrypted, if a method carries `[NetSecure]`).

A failed set or call — for either reason, or because the owner's own controller method threw while executing
an otherwise-permitted call — is reported through `INetManager.Failed` (`NetRemoteEntityFailure`: the
`INetRemoteEntity` and the `Exception`), and, for a `Task`/`Task<T>`-returning controller method, also faults
the caller's task with the same `NetFailedException`. Reading a property never fails, since it's always a
local, unconditional cache read with no request to reject.
