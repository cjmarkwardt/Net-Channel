namespace Markwardt.NetChannel.Internal;

/// <summary>
/// A single property of a validated model interface, and the id this side of a connection assigns it
/// (Docs/Wire.md#entities).
/// </summary>
internal sealed record NetModelMember(string Name, PropertyInfo Property, uint Id, bool HasAccess, string? Role);

/// <summary>
/// A single method of a validated controller interface, and the id this side of a connection assigns it
/// (Docs/Wire.md#entities).
/// </summary>
internal sealed record NetControllerMember(string Name, MethodInfo Method, uint Id, bool HasAccess, string? Role, bool IsSecure, Type? ResultType, bool IsTask);

/// <summary>
/// The validated shape of a model interface: its properties, in the fixed order their ids are assigned in.
/// </summary>
internal sealed class NetModelType(Type type, IReadOnlyList<NetModelMember> properties)
{
    /// <summary>
    /// The model interface type.
    /// </summary>
    public Type Type { get; } = type;

    /// <summary>
    /// The model's properties, in the order <see cref="NetModelMember.Id"/> is assigned in.
    /// </summary>
    public IReadOnlyList<NetModelMember> Properties { get; } = properties;

    /// <summary>
    /// The single property marked <see cref="NetPositionAttribute"/>, if any.
    /// </summary>
    public NetModelMember? Position { get; } = properties.SingleOrDefault(property => property.Property.GetCustomAttribute<NetPositionAttribute>() is not null);
}

/// <summary>
/// The validated shape of a controller interface: its methods, in the fixed order their ids are assigned in.
/// </summary>
internal sealed class NetControllerType(Type type, IReadOnlyList<NetControllerMember> methods)
{
    /// <summary>
    /// The controller interface type.
    /// </summary>
    public Type Type { get; } = type;

    /// <summary>
    /// The controller's methods, in the order <see cref="NetControllerMember.Id"/> is assigned in.
    /// </summary>
    public IReadOnlyList<NetControllerMember> Methods { get; } = methods;
}

/// <summary>
/// Validates and caches the shape of model and controller interfaces (Docs/Entities.md#interface-validation),
/// implemented by <see cref="NetEntityTypeCache"/>.
/// </summary>
internal interface INetEntityTypeCache
{
    /// <summary>
    /// Gets the validated shape of a model interface, validating and caching it the first time it's used.
    /// </summary>
    /// <param name="type">The model interface type.</param>
    /// <returns>The validated shape.</returns>
    /// <exception cref="NetInvalidInterfaceException"><paramref name="type"/> does not have the required shape.</exception>
    NetModelType GetModel(Type type);

    /// <summary>
    /// Gets the validated shape of a controller interface, validating and caching it the first time it's used.
    /// </summary>
    /// <param name="type">The controller interface type.</param>
    /// <returns>The validated shape.</returns>
    /// <exception cref="NetInvalidInterfaceException"><paramref name="type"/> does not have the required shape.</exception>
    NetControllerType GetController(Type type);
}

/// <inheritdoc cref="INetEntityTypeCache" />
internal sealed class NetEntityTypeCache : INetEntityTypeCache
{
    private readonly ConcurrentDictionary<Type, NetModelType> models = new();

    private readonly ConcurrentDictionary<Type, NetControllerType> controllers = new();

    /// <inheritdoc />
    public NetModelType GetModel(Type type) => models.GetOrAdd(type, BuildModel);

    /// <inheritdoc />
    public NetControllerType GetController(Type type) => controllers.GetOrAdd(type, BuildController);

    private static NetModelType BuildModel(Type type)
    {
        if (GetAllMethods(type).Any(method => !method.IsSpecialName))
        {
            throw new NetInvalidInterfaceException($"Model interface '{type}' must contain only properties.");
        }

        List<NetModelMember> properties = [];
        uint id = 0;

        foreach (PropertyInfo property in GetAllProperties(type))
        {
            if (property.GetMethod is not { IsPublic: true } || property.SetMethod is not { IsPublic: true })
            {
                throw new NetInvalidInterfaceException($"Model interface property '{type}.{property.Name}' must have a public getter and setter.");
            }

            if (property.GetMethod.GetCustomAttribute<NetSecureAttribute>() is not null || property.SetMethod.GetCustomAttribute<NetSecureAttribute>() is not null)
            {
                throw new NetInvalidInterfaceException($"[NetSecure] cannot be placed on model interface property accessor '{type}.{property.Name}'.");
            }

            if (property.GetMethod.GetCustomAttribute<NetAccessAttribute>() is not null || property.SetMethod.GetCustomAttribute<NetAccessAttribute>() is not null)
            {
                throw new NetInvalidInterfaceException($"[NetAccess] must be placed on the property itself, not an accessor, for '{type}.{property.Name}'.");
            }

            if (property.GetMethod.GetCustomAttribute<NetRoleAttribute>() is not null || property.SetMethod.GetCustomAttribute<NetRoleAttribute>() is not null)
            {
                throw new NetInvalidInterfaceException($"[NetRole] must be placed on the property itself, not an accessor, for '{type}.{property.Name}'.");
            }

            if (property.GetCustomAttribute<NetPositionAttribute>() is not null && property.PropertyType != typeof(NetPosition))
            {
                throw new NetInvalidInterfaceException($"[NetPosition] property '{type}.{property.Name}' must be of type NetPosition or NetPosition?.");
            }

            NetAccessAttribute? access = property.GetCustomAttribute<NetAccessAttribute>();
            NetRoleAttribute? role = property.GetCustomAttribute<NetRoleAttribute>();
            properties.Add(new NetModelMember(property.Name, property, id++, access?.HasAccess ?? false, role?.Role));
        }

        if (properties.Count(property => property.Property.GetCustomAttribute<NetPositionAttribute>() is not null) > 1)
        {
            throw new NetInvalidInterfaceException($"Model interface '{type}' may declare [NetPosition] on at most one property.");
        }

        return new NetModelType(type, properties);
    }

    /// <summary>
    /// Every property an interface exposes, its base interfaces' included, in a fixed order independent of the
    /// order reflection happens to return them in.
    /// </summary>
    /// <param name="type">The interface.</param>
    /// <returns>The properties, ordered by name.</returns>
    /// <exception cref="NetInvalidInterfaceException">Two of them share a name.</exception>
    private static IEnumerable<PropertyInfo> GetAllProperties(Type type)
    {
        // An interface's own reflection results never include what it inherits, and their order is unspecified
        // — left as-is, a base interface's members would silently never sync, and two peers could number the
        // same interface differently.
        List<PropertyInfo> properties = type.GetInterfaces()
            .Append(type)
            .SelectMany(candidate => candidate.GetProperties())
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToList();

        if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Count)
        {
            throw new NetInvalidInterfaceException($"Model interface '{type}' declares more than one property of the same name, which the wire format cannot tell apart.");
        }

        return properties;
    }

    /// <summary>
    /// Every method an interface exposes, its base interfaces' included, in a fixed order independent of the
    /// order reflection happens to return them in.
    /// </summary>
    /// <param name="type">The interface.</param>
    /// <returns>The methods, ordered by name.</returns>
    private static IEnumerable<MethodInfo> GetAllMethods(Type type) =>
        type.GetInterfaces()
            .Append(type)
            .SelectMany(candidate => candidate.GetMethods())
            .OrderBy(method => method.Name, StringComparer.Ordinal);

    private static NetControllerType BuildController(Type type)
    {
        if (GetAllProperties(type).Any())
        {
            throw new NetInvalidInterfaceException($"Controller interface '{type}' must contain only methods.");
        }

        List<MethodInfo> declared = GetAllMethods(type).Where(method => !method.IsSpecialName).ToList();

        if (declared.Select(method => method.Name).Distinct(StringComparer.Ordinal).Count() != declared.Count)
        {
            throw new NetInvalidInterfaceException($"Controller interface '{type}' declares more than one method of the same name, which the wire format cannot tell apart.");
        }

        List<NetControllerMember> methods = [];
        uint id = 0;

        foreach (MethodInfo method in declared)
        {
            bool isTask = method.ReturnType == typeof(Task) || (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>));

            if (method.ReturnType != typeof(void) && !isTask)
            {
                throw new NetInvalidInterfaceException($"Controller interface method '{type}.{method.Name}' must return void, Task, or Task<T>.");
            }

            Type? resultType = method.ReturnType.IsGenericType ? method.ReturnType.GetGenericArguments()[0] : null;
            NetAccessAttribute? access = method.GetCustomAttribute<NetAccessAttribute>();
            NetRoleAttribute? role = method.GetCustomAttribute<NetRoleAttribute>();
            bool isSecure = method.GetCustomAttribute<NetSecureAttribute>() is not null;
            methods.Add(new NetControllerMember(method.Name, method, id++, access?.HasAccess ?? true, role?.Role, isSecure, resultType, isTask));
        }

        return new NetControllerType(type, methods);
    }
}
