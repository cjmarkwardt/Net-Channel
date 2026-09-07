namespace Markwardt.NetChannel;

/// <summary>
/// Restricts setting a model interface property, or calling a controller interface method, so that only a
/// connection with the given role can do so. Placed on the property itself (not an individual getter/setter
/// — a property's getter is never role-restricted, and is always usable by every connection the entity is
/// visible to) or on the method; placing it directly on a getter or setter instead fails interface validation
/// (see <see cref="NetInvalidInterfaceException"/>) the first time the interface is used. No role is required
/// when absent. See Docs/Access.md.
/// </summary>
/// <param name="role">The role required to set the property or call the method.</param>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property)]
public sealed class NetRoleAttribute(string role) : Attribute
{
    /// <summary>
    /// The role required to set the property or call the method.
    /// </summary>
    public string Role { get; } = role;
}
