using System.Collections.Concurrent;
using System.Reflection;
using TripleG3.P2P.Attributes;

namespace TripleG3.P2P.Serialization;

internal sealed class SerializationContract
{
    private static readonly ConcurrentDictionary<Type, SerializationContract> Cache = new();

    private readonly ConstructorInfo _constructor;
    private readonly int[] _constructorPropertyIndexes;
    private readonly bool _usesPropertySetters;

    private SerializationContract(
        Type type,
        IReadOnlyList<PropertyInfo> properties,
        ConstructorInfo constructor,
        int[] constructorPropertyIndexes,
        bool usesPropertySetters)
    {
        Type = type;
        Properties = properties;
        _constructor = constructor;
        _constructorPropertyIndexes = constructorPropertyIndexes;
        _usesPropertySetters = usesPropertySetters;
    }

    public Type Type { get; }

    public IReadOnlyList<PropertyInfo> Properties { get; }

    public static SerializationContract For(Type type) => Cache.GetOrAdd(type, Create);

    public object Create(IReadOnlyList<object?> values)
    {
        if (values.Count != Properties.Count)
        {
            throw new InvalidDataException($"Expected {Properties.Count} values for {Type.FullName}, but received {values.Count}.");
        }

        if (_usesPropertySetters)
        {
            var instance = _constructor.Invoke(null);
            for (int index = 0; index < Properties.Count; index++)
            {
                Properties[index].SetValue(instance, values[index]);
            }

            return instance;
        }

        var arguments = new object?[_constructorPropertyIndexes.Length];
        for (int index = 0; index < arguments.Length; index++)
        {
            arguments[index] = values[_constructorPropertyIndexes[index]];
        }

        return _constructor.Invoke(arguments);
    }

    private static SerializationContract Create(Type type)
    {
        var orderedProperties = type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => (Property: property, Attribute: property.GetCustomAttribute<UdpAttribute>()))
            .Where(item => item.Attribute is not null)
            .OrderBy(item => item.Attribute!.Order ?? int.MaxValue)
            .ThenBy(item => item.Property.Name, StringComparer.Ordinal)
            .ToArray();

        if (orderedProperties.Length == 0)
        {
            throw new InvalidOperationException($"Type {type.FullName} has no properties marked with {nameof(UdpAttribute)}.");
        }

        var duplicateOrder = orderedProperties
            .Where(item => item.Attribute!.Order.HasValue)
            .GroupBy(item => item.Attribute!.Order!.Value)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateOrder is not null)
        {
            throw new InvalidOperationException($"Type {type.FullName} has duplicate UDP order {duplicateOrder.Key}.");
        }

        var properties = orderedProperties.Select(item => item.Property).ToArray();
        var candidates = type
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
            .Select(constructor => (Constructor: constructor, Mapping: TryMapConstructor(constructor, properties)))
            .Where(candidate => candidate.Mapping is not null)
            .ToArray();

        if (candidates.Length == 1)
        {
            return new SerializationContract(type, properties, candidates[0].Constructor, candidates[0].Mapping!, false);
        }

        if (candidates.Length > 1)
        {
            throw new InvalidOperationException($"Type {type.FullName} has multiple constructors matching its UDP contract.");
        }

        var parameterless = type.GetConstructor(Type.EmptyTypes);
        if (parameterless is not null && properties.All(property => property.SetMethod is { IsPublic: true }))
        {
            return new SerializationContract(type, properties, parameterless, [], true);
        }

        throw new InvalidOperationException($"Type {type.FullName} needs one public constructor whose parameters match its UDP properties by name and type.");
    }

    private static int[]? TryMapConstructor(ConstructorInfo constructor, IReadOnlyList<PropertyInfo> properties)
    {
        var parameters = constructor.GetParameters();
        if (parameters.Length != properties.Count)
        {
            return null;
        }

        var mapping = new int[parameters.Length];
        for (int parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
        {
            var parameter = parameters[parameterIndex];
            var propertyIndex = -1;
            for (int candidateIndex = 0; candidateIndex < properties.Count; candidateIndex++)
            {
                var property = properties[candidateIndex];
                if (string.Equals(parameter.Name, property.Name, StringComparison.OrdinalIgnoreCase)
                    && parameter.ParameterType == property.PropertyType)
                {
                    propertyIndex = candidateIndex;
                    break;
                }
            }

            if (propertyIndex < 0)
            {
                return null;
            }

            mapping[parameterIndex] = propertyIndex;
        }

        return mapping.Distinct().Count() == mapping.Length ? mapping : null;
    }
}