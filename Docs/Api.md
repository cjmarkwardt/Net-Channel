# Public API

This is a reference index of the public surface. For the mechanics and rules behind each area, see:
[Entities.md](Entities.md) (entities, model/controller, state, remote entities), [Access.md](Access.md)
(delivery, `[NetRole]`/`[NetAccess]`/`[NetSecure]`), and [Groups.md](Groups.md) (groups/views/audiences).
[Wire.md](Wire.md) covers the byte-level protocol underneath all of it.

## Entry point

`INetManager` is the main entry point for communicating with other peers.

- `Host(port = 0, identity = null, host = "0.0.0.0")` starts (or restarts, on a new socket) accepting incoming connections, so the manager can act as a server as well as make its own outgoing ones. `port` of `0` lets the OS assign one; `identity` is a long-term Ed25519 private key used to prove the server's identity to a client connecting in Pinned mode, and without one only Unauthenticated clients can connect — see [Wire.md](Wire.md#server-authentication).
- `HostPort` is the UDP port currently being listened on, or `null` before `Host` is called — the way to read back an OS-assigned port.
- `Connect(host, port, tag = null, expectedIdentity = null)` begins connecting and immediately returns an `INetConnection` in a connecting state. An optional `tag` can later be used to look the connection back up via `GetConnection(tag)` (tag is required to be non-null for lookup). `expectedIdentity` is the peer's expected Ed25519 identity public key, connecting in Pinned mode; leaving it null connects in Unauthenticated mode.
- `PendingConnections` / `ActiveConnections` list connections currently connecting / connected.
- `StatusChanged` observable fires on any connection status change (`NetStatusChange`: `Connection` + `Status`).
- `Rejected` observable fires when a connecting connection fails to connect (`NetConnectionFailure`: `Connection` + `Exception`).
- `Dropped` observable fires when a connected connection loses its remote connection (same payload as `Rejected`). Does not fire for a local `Drop`/`Disconnect` call.
- `Failed` observable (`NetRemoteEntityFailure`: `Entity` + `Exception`) fires on any remote entity set/call failure (getting a property never fails) — see [Entities.md](Entities.md#failures).
- `PingInterval` (default 1.5 seconds) / `DisconnectTimeout` (default 5 seconds) configure the liveness heartbeat — see [Wire.md](Wire.md#liveness).
- `CreateView(tag = null)` / `GetView(tag)` create/look up an `INetView`; `Views` lists all of them.
- `All` is the `INetGroup` for every active connection — see [Groups.md](Groups.md).
- `Listen<TModel, TController>(listener)` registers a listener for newly visible entities matching both `TModel` and `TController`, returning an `IDisposable` that unsubscribes it — see [Entities.md](Entities.md#listening-for-remote-entities).
- `SetSerializer<T>(serializer)` registers the `INetSerializer` used for every model property value, controller method argument, and method result of type `T` — see "Value serialization" below.
- `INetManager` implements `IDisposable`/`IAsyncDisposable`: `Dispose()` immediately ends every connection (like calling `Drop()` on each); `DisposeAsync()` ends them all gracefully (like calling `Disconnect()` on each) and completes once every connection has ended.

## Connections

`INetConnection` represents a single connection to a remote peer.

- `Status` (`NetStatus`: `Disconnected` / `Connecting` / `Connected`), `Direction` (`NetDirection`: `Incoming` / `Outgoing`), `Host`, `Port`, `Tag`, `Roles`.
- `Latency` is a `TimeSpan`, the connection's round-trip latency as most recently measured by the liveness heartbeat (see [Wire.md](Wire.md#liveness)).
- `Position` is this connection's global `NetPosition?` for position-based visibility filtering — see "Position-based visibility" below. Purely local to this manager; never transmitted to or visible from the remote peer.
- `Drop()` ends the connection immediately, without a graceful shutdown. `Disconnect()` ends it gracefully, notifying the peer first so it need not wait for a timeout; the notification is never acknowledged, so this doesn't wait on the peer either.
- `Wait()` resolves once a connecting connection reaches `Connected`, or faults if the attempt ends for any reason — a rejection, a local `Drop()`/`Disconnect()`, or the manager being disposed. It returns immediately if the connection has already resolved either way.
- `Promote(role)` / `Demote(role)` grant/revoke a role on the connection — see [Access.md](Access.md).

`INetConnection` has no generic message-sending API of its own — all network interaction flows through entities.

Addressing is IPv4 only, for both `Host` and `Connect` — anything else is rejected with an `ArgumentException`
rather than reaching the socket layer. `Connect` also accepts a host name, resolved to its first IPv4 address.

## Groups and views

`INetGroup` exposes a set of local entities to an audience of connections — `Entities` lists them;
`Add<TModel, TController>`/`Remove` add/remove them. `INetView` implements it with an explicit,
caller-managed audience (`Tag`, `Viewers`, `AddViewers`/`RemoveViewers`, `GetViewerPosition`/`SetViewerPosition`,
`Destroy`); `INetManager.All` implements it with an implicit audience of every active connection and no
viewers to manage. See [Groups.md](Groups.md) for the full mechanics. An entity relationship only ever exists
between its owner and its direct connections — there is no way to relay one onward through an intermediate
peer.

## Entities

A local entity is a **model interface** + a **controller interface** + an implementation
(`NetEntity<TModel, TController>`); a **remote entity** (`INetRemoteEntity<TModel, TController>`) is a proxy
for one owned elsewhere. Every property set is delivered fast and unreliably (latest value wins); every
method call is delivered reliably and in order, optionally encrypted via `[NetSecure]`; `[NetRole]`/
`[NetAccess]` govern setter/call permission (see [Access.md](Access.md) — a property's getter is always usable
by any connection that can see the entity); `[NetPosition]` marks a model property for position-based
visibility filtering (see below). The first time a
`TModel`/`TController` pairing is used (`Add` or `Listen`), it's validated once and cached;
`NetInvalidInterfaceException` is thrown then if either doesn't have the required shape, or misuses one of
these attributes — see [Entities.md](Entities.md#interface-validation) for the full list of checks. Either
interface may extend others; everything it inherits counts as its own. See
[Entities.md](Entities.md) for `INetEntity`, `NetEntity<TModel, TController>`, `INetState`/`INetState<TModel>`,
`INetRemoteEntity`, and how failures surface.

## Value serialization

Every model property value, controller method argument, and method result travels the wire as opaque bytes.
How an individual value becomes those bytes is decided per CLR type:

- By default, automatic Protocol Buffers serialization, requiring no attributes or generated code on the type.
- `INetManager.SetSerializer<T>(serializer)` overrides that for `T` with an `INetSerializer`, whose
  `Serialize(value)` returns an `IMemoryOwner<byte>` rented from a shared pool (the caller disposes it) and
  whose `Deserialize(data)` reads one back. Both sides of a connection must agree on the serializer used for a
  type; registering one on only one peer leaves the other unable to read what it sends.

A null value and a value that happens to encode to nothing (an empty collection, say) stay distinguishable, so
either round-trips back as itself rather than collapsing into the other.

Whatever a value encodes to has to fit in a single UDP datagram alongside the rest of its packet — there is no
fragmentation ([Wire.md](Wire.md#datagram-layout)). Setting a property to something too large doesn't throw;
the value simply never reaches the other side, so keep bulk data out of entity state.

A type with no serializer registered for it has to be one Protocol Buffers can handle. One that isn't (an
`Exception`, say, or another type with no meaningful contract) surfaces as an exception from whatever set or
call first tries to encode it, so register an `INetSerializer` for any such type before using it.

## Position-based visibility

`NetPosition(Position, Range = null)` is a `record` consolidating a position (`Vector3`) and an optional
`NetRange` into the single field each is represented by everywhere: an entity's `[NetPosition]` model
property, a connection's global `INetConnection.Position`, and a view's per-connection
`INetView.SetViewerPosition` override. A `Vector3` implicitly converts to a `NetPosition` with no range, so a
plain position can be assigned directly wherever a `NetPosition` is expected.

`NetRange(EnterDistance, ExitDistance)` is a hysteresis pair, not a single cutoff: an invisible relationship
becomes visible once within `EnterDistance`, but a visible one only becomes invisible once beyond
`ExitDistance`. Unlike `NetPosition`, there's no implicit conversion from a single `float` — giving
`EnterDistance` and `ExitDistance` the same value collapses the buffer back to a plain single-threshold
cutoff, which defeats the point of a hysteresis pair, so it isn't given a shortcut; construct `NetRange`
explicitly with both distances.

Position-based visibility is a filter on top of a group's existing visibility: an entity must already be
visible to a connection by being in a group (`INetGroup`) that connection is a viewer of before this filtering
applies at all — it can only take visibility away, never grant it beyond that.

- For a connection viewing an entity within a particular view, the connection's effective position is that view's `GetViewerPosition(connection)` override if one is set, otherwise the connection's global `Position`.
- An invisible entity-connection pairing becomes visible only once the distance between the entity's `[NetPosition]` property and the connection's effective position (per the above) is within both sides' `Range.EnterDistance` — checked independently, so a side with no position, or no `Range`, imposes no constraint of its own.
- A visible pairing becomes invisible only once that distance exceeds either side's `Range.ExitDistance` — checked independently, so exceeding just one of them, when set, is enough.
- Otherwise, a pairing's visibility is left exactly as it already was — this hysteresis is what prevents an entity hovering near a single boundary from flickering in and out of visibility.
- If either side has no position (or the entity's model declares no `[NetPosition]` property), this filter doesn't apply at all.
- Whenever a connection's global or view-overriding position changes, or an entity's `[NetPosition]` property value changes, every connection-entity pairing this could affect is re-evaluated: a pairing newly becoming visible gets an entity creation message sent for it on that connection; one newly becoming invisible gets an entity destruction message, exactly as if it had been added to or removed from the group.
