# Net-Channel

A C# networking library built on an efficient, low-overhead UDP transport. Application state and behavior
are modeled as net entities — a property-based model interface and a method-based controller interface — and
exposed to other peers through groups and views, with fine-grained per-member access control and
position-based visibility. It is published as a NuGet package for use in other .NET projects.

## Projects

| Project | Description |
|---------|-------------|
| **Core** | The Net-Channel client/server library. |
| **Tests** | xUnit tests for Core. |

## Documentation

| Doc | Description |
|-----|-------------|
| [Docs/Api.md](Docs/Api.md) | Public API reference index. |
| [Docs/Entities.md](Docs/Entities.md) | Net entities: model/controller interfaces, state, remote entities. |
| [Docs/Groups.md](Docs/Groups.md) | Groups and views: how entities become visible to connections. |
| [Docs/Access.md](Docs/Access.md) | Access control and delivery: `[NetRole]`/`[NetAccess]`/`[NetSecure]`. |
| [Docs/Wire.md](Docs/Wire.md) | Wire format: datagram layout, checksum, connection handshake, liveness, entity synchronization. |

## Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download)

## Installing

```sh
dotnet add package Markwardt.NetChannel
```

## Building

```sh
dotnet build
```

## Testing

```sh
dotnet test
```
