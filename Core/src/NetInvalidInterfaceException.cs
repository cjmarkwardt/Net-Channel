namespace Markwardt.NetChannel;

/// <summary>
/// Thrown the first time a model or controller interface is used (e.g. via
/// <see cref="INetGroup.Add{TModel, TController}"/> or <see cref="INetManager.Listen{TModel, TController}"/>)
/// if it does not have the shape required of that kind of interface, or misuses one of the attributes that
/// can be placed on its members. An interface's members include everything it inherits from the interfaces it
/// extends, which are validated and used exactly as if declared on it directly. A given interface is
/// validated only once; the result is cached for every later use. The checks are:
/// <list type="bullet">
/// <item>A model interface must contain only properties, each with a public getter and setter.</item>
/// <item>A controller interface must contain only methods returning <see langword="void"/>,
/// <see cref="Task"/>, or <see cref="Task{TResult}"/>.</item>
/// <item>No two of an interface's members, inherited ones included, may share a name, since the wire format
/// identifies a member by name — so a controller interface may not declare overloads.</item>
/// <item><see cref="NetRoleAttribute"/> and <see cref="NetAccessAttribute"/> must be placed on a property or
/// method itself, never on an individual getter/setter.</item>
/// <item><see cref="NetSecureAttribute"/> must be placed on a controller interface method; never on a model
/// interface property or an individual getter/setter of one, since a property set is never encrypted.</item>
/// <item><see cref="NetPositionAttribute"/> must be placed on a model interface property of type
/// <see cref="NetPosition"/> or <c>NetPosition?</c>, and on at most one property per model interface.</item>
/// </list>
/// </summary>
/// <param name="message">A human-readable message describing why the interface is invalid.</param>
public sealed class NetInvalidInterfaceException(string message) : Exception(message);
