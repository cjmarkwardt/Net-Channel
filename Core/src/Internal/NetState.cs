namespace Markwardt.NetChannel.Internal;

/// <inheritdoc cref="INetState{TModel}" />
internal sealed class NetState<TModel> : INetState<TModel>
    where TModel : class
{
    private readonly Dictionary<string, object?> values = [];

    private readonly Dictionary<string, Subject<object?>> subjects = [];

    private readonly Lock gate = new();

    /// <summary>
    /// Initializes a new instance backed by the given validated model shape.
    /// </summary>
    /// <param name="modelType">The validated shape of <typeparamref name="TModel"/>.</param>
    public NetState(NetModelType modelType)
    {
        ModelType = modelType;
        Properties = modelType.Properties.Select(property => property.Name).ToHashSet();
        Model = NetModelProxy<TModel>.Create(this);
    }

    /// <summary>
    /// The validated shape of <typeparamref name="TModel"/> this state stores values for.
    /// </summary>
    public NetModelType ModelType { get; }

    /// <inheritdoc />
    public IReadOnlySet<string> Properties { get; }

    /// <inheritdoc />
    public TModel Model { get; }

    /// <inheritdoc />
    public object? Get(string property)
    {
        NetModelMember member = RequireMember(property);

        lock (gate)
        {
            if (values.TryGetValue(property, out object? value))
            {
                return value;
            }
        }

        Type propertyType = member.Property.PropertyType;
        return propertyType.IsValueType ? Activator.CreateInstance(propertyType) : null;
    }

    /// <inheritdoc />
    public void Set(string property, object? value)
    {
        RequireMember(property);
        Subject<object?>? subject;

        lock (gate)
        {
            values[property] = value;
            subjects.TryGetValue(property, out subject);
        }

        subject?.OnNext(value);
    }

    /// <inheritdoc />
    public IObservable<object?> Observe(string property)
    {
        RequireMember(property);

        lock (gate)
        {
            if (!subjects.TryGetValue(property, out Subject<object?>? subject))
            {
                subject = new Subject<object?>();
                subjects.Add(property, subject);
            }

            return subject;
        }
    }

    private NetModelMember RequireMember(string property) =>
        ModelType.Properties.FirstOrDefault(candidate => candidate.Name == property)
            ?? throw new ArgumentException($"'{property}' is not a property of model interface '{ModelType.Type}'.", nameof(property));

    /// <inheritdoc />
    public IObservable<T> Observe<T>(Expression<Func<TModel, T>> selector) =>
        Observe(GetPropertyName(selector.Body)).Select(value => (T)value!);

    private static string GetPropertyName(Expression body)
    {
        if (body is UnaryExpression unary)
        {
            body = unary.Operand;
        }

        return body is MemberExpression { Member: PropertyInfo property }
            ? property.Name
            : throw new ArgumentException("Selector must be a simple property access.", nameof(body));
    }
}
