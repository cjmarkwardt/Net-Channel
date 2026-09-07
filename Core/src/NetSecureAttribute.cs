namespace Markwardt.NetChannel;

/// <summary>
/// Marks a controller interface method's calls and results as encrypted, so that only the two endpoints of
/// the connection can read them. Placed on a controller interface method only — never on a model interface
/// property or an individual getter/setter, which is invalid and rejected by interface validation (see
/// <see cref="NetInvalidInterfaceException"/>) the first time the interface is used, since a property set is
/// never encrypted. Absent by default, meaning the call and its result are sent unencrypted. See
/// Docs/Access.md.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class NetSecureAttribute : Attribute;
