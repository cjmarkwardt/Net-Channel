namespace Markwardt.NetChannel;

/// <summary>
/// Specifies whether other peers can set a model interface property, or call a controller interface method.
/// Placed on the property itself (not an individual getter/setter — a property's getter is always usable by
/// every connection the entity is visible to, ungoverned by this attribute) or on the method; placing it
/// directly on a getter or setter instead fails interface validation (see
/// <see cref="NetInvalidInterfaceException"/>) the first time the interface is used. Methods default to
/// <see langword="true"/> when absent; property setters default to <see langword="false"/>. See
/// Docs/Access.md.
/// </summary>
/// <param name="hasAccess">Whether other peers can set the property or call the method.</param>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method)]
public sealed class NetAccessAttribute(bool hasAccess) : Attribute
{
    /// <summary>
    /// Whether other peers can set the property or call the method.
    /// </summary>
    public bool HasAccess { get; } = hasAccess;
}
