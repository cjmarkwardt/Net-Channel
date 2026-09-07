# Entities

This document explains what a net entity is, how its local and remote sides are represented, and how the
supporting types (`INetState`, `INetRemoteEntity`, `NetEntity<TModel, TController>`) fit together. See
[Groups.md](Groups.md) for how an entity becomes visible to a connection in the first place, and
[Access.md](Access.md) for what a visible entity's individual members allow once visible. An entity
relationship only ever exists between its owner and its direct connections — there is no way to relay an
entity onward to a connection the owner isn't directly connected to.

## What an entity is

A local entity is defined by the combination of three things:

- A **model interface** — properties only, each with a public getter and setter. This is the entity's
  network-visible state: whatever other peers can see, and, if allowed, change.
- A **controller interface** — methods only, each returning `void` or a result wrapped in `Task`/`Task<T>`.
  This is the entity's behavior: the actions other peers can trigger remotely.
- An **implementation** — a class inheriting `NetEntity<TModel, TController>` that itself implements
  `TController`, supplying what actually happens when a controller method is called.

These three are deliberately separate: the model interface can't contain methods, and the controller
interface can't contain properties, so "what other peers can see" and "what other peers can do" are always
distinguishable just by which interface a member is declared on. A **remote entity** is what the *other* side
of this looks like — a proxy for an entity owned somewhere else, described in "Remote entities" below.

## The model interface

Every property on a model interface has a public getter and setter — nothing else is permitted on this
interface. A property's getter is always usable by any connection that can see the entity, unconditionally;
`[NetRole]`/`[NetAccess]` on the property only govern whether its *setter* can be used remotely — see
[Access.md](Access.md) for the full rules and defaults. Every property set is delivered the same fixed way
(fast and unreliable, latest value wins — [Access.md](Access.md#delivery)); a property can never be
`[NetSecure]`, since a set is never encrypted. `[NetPosition]` on a `NetPosition`-typed property marks it as
the position/range used for position-based visibility filtering ([Api.md](Api.md#position-based-visibility)).

## The controller interface

Every method on a controller interface returns `void`, `Task`, or `Task<T>` — no other return type is
permitted. The return type isn't just documentation: it determines delivery mechanics on both sides of a
call. A `void` method is sent fire-and-forget and never has a result to produce; a `Task`/`Task<T>` method is
sent as a request whose caller waits for a response. Every call is delivered the same fixed way (guaranteed,
in order — [Access.md](Access.md#delivery)); `[NetSecure]` additionally encrypts a method's calls and results.
`[NetRole]`/`[NetAccess]` control who may call it, the same as they control who may set a property.

## Interface validation

The first time a given `TModel`/`TController` pairing is used anywhere — via `INetGroup.Add` or
`INetManager.Listen` — it's validated once and the result cached for every later use. An interface that fails
any of these checks throws `NetInvalidInterfaceException` at that first point of use:

- `TModel` must contain only properties, each with a public getter and setter; `TController` must contain
  only methods returning `void`, `Task`, or `Task<T>`.
- `[NetRole]`/`[NetAccess]` must be placed on a property or method itself, never on an individual
  getter/setter.
- `[NetSecure]` must be placed on a controller interface method; never on a model interface property or an
  individual getter/setter of one, since a property set is never encrypted.
- `[NetPosition]` must be placed on a model interface property of type `NetPosition`/`NetPosition?`, and on
  at most one property — see [Api.md](Api.md#position-based-visibility).

## `INetEntity` and `INetEntity<TModel, TController>`

`INetEntity` is the non-generic base every local entity implements, so `INetGroup.Add`/`Remove` can accept
entities of any model/controller pairing uniformly without needing to be generic themselves:

- `State` — the entity's model state, in its untyped (`INetState`) form.
- `IsDestroyed` — whether `Destroy()` has been called; the entity should not be used afterward.
- `Destroyed` — an `IObservable<INetEntity>` that fires the first time `Destroy()` is called.
- `Destroy()` — destroys the entity and triggers `Destroyed`. Only the first call has any effect; every
  call after that is a no-op, so it's always safe to call more than once.

`INetEntity<TModel, TController>` extends it, hiding `State` with a strongly-typed `INetState<TModel>` view
of the same underlying state — everything else is inherited unchanged.

## `NetEntity<TModel, TController>`

The abstract base class an entity implementation inherits from. It supplies `State`, `IsDestroyed`,
`Destroyed`, and `Destroy()` concretely, so a deriving class's only remaining responsibility is implementing
`TController` — the real behavior invoked when a remote peer calls a controller method. That implementation,
together with `State`, gives the entity complete control over both what happens when it's called and what
happens when its model properties are read, written, or observed.

`INetGroup.Add<TModel, TController>` takes `TModel`/`TController` as explicit generic arguments at the call
site — it doesn't need to inspect a `NetEntity<TModel, TController>` instance's runtime type to discover
them, since the entity is already statically typed as `INetEntity<TModel, TController>` by the time it
reaches `Add`.

## `INetState` and `INetState<TModel>`

`INetState` is the untyped storage behind a model — the same storage backs both a local entity's `State` and
(indirectly) a remote entity's cached `Model`:

- `Properties` — the names of every property declared on the model interface this state backs.
- `Get(property)`/`Set(property, value)` — get/set a property's value by name, untyped.
- `Observe(property)` — takes a property name and returns an `IObservable<object?>` that pushes the
  property's new value, untyped, whenever it changes.

`INetState<TModel>` adds a strongly-typed view over the same storage:

- `Model` — a dynamically implemented instance of `TModel`, built via reflection over the interface and a
  dynamic object that dispatches every get/set on it straight through to `Get`/`Set`. This is unrestricted,
  local access — `[NetAccess]`/`[NetRole]` only restrict *remote* peers, never this local view.
- `Observe<T>(selector)` — the strongly-typed counterpart of `INetState.Observe`: takes an expression
  selecting one property (e.g. `x => x.Position`) instead of its name, and returns an `IObservable<T>`
  instead of an untyped one.

## Remote entities

`INetRemoteEntity` is the non-generic base every remote entity proxy implements:

- `Destroyed` — an `IObservable<INetRemoteEntity>` that fires when the entity is no longer visible to
  this peer (removed from its group, a viewer removed, filtered out by position-based visibility, or its
  owner's connection lost). The proxy is no longer valid afterward.

`INetRemoteEntity<TModel, TController>`, obtained via `INetManager.Listen<TModel, TController>`, adds:

- `Model` — the remote entity's model. Properties are cached locally, so getting one is always instant, with
  no network round trip, and never fails. *Setting* one updates the local cache immediately (optimistically)
  and sends the new value to the entity's owner, which applies it and then re-broadcasts it to every other
  connection the entity is visible to — the same path a genuine local change from the owner would take. If
  the owner declines the set (no `[NetAccess]` granting access, a missing role, etc.) or the entity becomes
  no longer visible before it answers (e.g. the owner's connection is lost), the failure is reported through
  `INetManager.Failed`, not through the property setter itself, since the setter has already returned by the
  time the owner's answer, or its absence, is known.
- `Controller` — the remote entity's controller. Calling a method packages the arguments and sends them
  to the owner to execute. A method returning `Task`/`Task<T>` is sent as a request whose task completes with
  the owner's result, or faults with a `NetFailedException` if the owner declines the call, its own
  controller method throws while executing it, or the entity becomes no longer visible before it answers —
  this is the one case where a failure *can* reach the original call site, since the `Task` is still being
  awaited when the answer, or that loss of visibility, happens. A `void` method is fire-and-forget, so like a
  declined set, its failure (if any) only surfaces via `INetManager.Failed`. Either way, a visibility-loss
  failure happens at the same moment `Destroyed` fires for that reason (above).

## Listening for remote entities

`INetManager.Listen<TModel, TController>(listener)` registers interest in entities of one specific
model/controller pairing. When an entity creation message arrives, the model and controller interface types
it carries are matched against every registered `Listen` call — both must match the same call for it to
fire; matching only one is not enough. Every listener registered for that exact pair gets a new
`INetRemoteEntity<TModel, TController>` for the entity. If no listener matches, the entity is ignored
entirely: no proxy is created, and nothing else in the system is told it exists.

## Failures

`INetManager.Failed` (`IObservable<NetRemoteEntityFailure>`) is the single, uniform place every set/call
failure across every remote entity surfaces, identifying which entity it happened on
(`NetRemoteEntityFailure.Entity`) and why (`NetRemoteEntityFailure.Exception`). A failed
`Task`/`Task<T>`-returning controller call also faults its own task with the same exception, in addition to
triggering `Failed` — the two aren't alternatives, a call that has a task to fault gets both. Getting a
property never fails and never triggers `Failed`.
