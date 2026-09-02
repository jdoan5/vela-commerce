using System.Reflection;
using Mono.Cecil;

namespace VelaCommerce.Architecture.Tests;

/// <summary>
/// Reads facts out of compiled IL with Mono.Cecil.
/// <para>
/// Reflection cannot answer the two questions this project needs most. It cannot say which types a
/// method <em>body</em> touches, so a composition root that resolves a <c>DbContext</c> inside a
/// lambda looks clean to <see cref="Type.GetMembers()"/>; and it cannot see a property read, so
/// <c>DateTimeOffset.UtcNow</c> buried in a constructor is invisible. Both are exactly the kind of
/// leak these rules exist to catch, so the IL is read directly.
/// </para>
/// <para>
/// Everything here attributes findings to the <em>outermost</em> declaring type. The compiler
/// scatters closures, async state machines and lambda caches into nested types with unspeakable
/// names (<c>Program/&lt;&gt;c/&lt;&lt;&lt;Main&gt;$&gt;b__0_2&gt;d</c>); a failure message naming
/// one of those would tell a reviewer nothing, so it is reported against the type they wrote.
/// </para>
/// </summary>
internal static class IlFacts
{
    /// <summary>
    /// Opens an assembly for reading. No <see cref="IAssemblyResolver"/> is configured on purpose:
    /// nothing here calls <c>Resolve()</c>, so the rules never need the referenced assemblies on
    /// disk and cannot fail for an unrelated missing dependency.
    /// </summary>
    internal static ModuleDefinition ReadModule(Assembly assembly) =>
        ModuleDefinition.ReadModule(assembly.Location);

    /// <summary>
    /// The type a human actually wrote, walking out through any compiler-generated nesting.
    /// </summary>
    internal static TypeDefinition AuthoredType(TypeDefinition type) =>
        type.DeclaringType is null ? type : AuthoredType(type.DeclaringType);

    /// <summary>
    /// Every type full name that <paramref name="type"/> mentions anywhere: its base type and
    /// interfaces, the signatures of its fields, properties and methods, the locals and every
    /// instruction operand of its method bodies, and the types named in its attributes.
    /// <para>
    /// Attributes are included because EF Core stamps <c>[DbContext(typeof(...))]</c> onto
    /// migrations — a real reference to the context that carries no field, parameter or call, and
    /// would otherwise slip past a rule about where the context may appear.
    /// </para>
    /// </summary>
    internal static IReadOnlySet<string> TypesMentionedBy(TypeDefinition type) =>
        ReferencedTypes(type).SelectMany(Expand).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Every method call made from the bodies of <paramref name="type"/>, paired with the method
    /// that makes it. Property reads are calls too, which is what makes a rule about clock access
    /// expressible at all.
    /// </summary>
    internal static IEnumerable<(MethodDefinition From, MethodReference Called)> CallsMadeBy(TypeDefinition type) =>
        from method in type.Methods
        where method.HasBody
        from instruction in method.Body.Instructions
        let called = instruction.Operand as MethodReference
        where called is not null
        select (method, called);

    private static IEnumerable<TypeReference> ReferencedTypes(TypeDefinition type)
    {
        if (type.BaseType is not null)
        {
            yield return type.BaseType;
        }

        foreach (var implemented in type.Interfaces)
        {
            yield return implemented.InterfaceType;
        }

        foreach (var named in NamedInAttributes(type.CustomAttributes))
        {
            yield return named;
        }

        foreach (var field in type.Fields)
        {
            yield return field.FieldType;
        }

        foreach (var property in type.Properties)
        {
            yield return property.PropertyType;
        }

        foreach (var method in type.Methods)
        {
            foreach (var referenced in ReferencedTypes(method))
            {
                yield return referenced;
            }
        }
    }

    private static IEnumerable<TypeReference> ReferencedTypes(MethodDefinition method)
    {
        yield return method.ReturnType;

        foreach (var parameter in method.Parameters)
        {
            yield return parameter.ParameterType;
        }

        foreach (var named in NamedInAttributes(method.CustomAttributes))
        {
            yield return named;
        }

        if (!method.HasBody)
        {
            yield break;
        }

        foreach (var local in method.Body.Variables)
        {
            yield return local.VariableType;
        }

        foreach (var instruction in method.Body.Instructions)
        {
            switch (instruction.Operand)
            {
                case TypeReference referenced:
                    yield return referenced;
                    break;

                case FieldReference field:
                    yield return field.DeclaringType;
                    yield return field.FieldType;
                    break;

                case MethodReference called:
                    yield return called.DeclaringType;
                    yield return called.ReturnType;

                    foreach (var parameter in called.Parameters)
                    {
                        yield return parameter.ParameterType;
                    }

                    // A generic call such as AddDbContext<VelaCommerceDbContext>() names the
                    // interesting type only in its type arguments, nowhere in its signature.
                    if (called is GenericInstanceMethod generic)
                    {
                        foreach (var argument in generic.GenericArguments)
                        {
                            yield return argument;
                        }
                    }

                    break;
            }
        }
    }

    private static IEnumerable<TypeReference> NamedInAttributes(IEnumerable<CustomAttribute> attributes)
    {
        foreach (var attribute in attributes)
        {
            yield return attribute.AttributeType;

            if (!attribute.HasConstructorArguments)
            {
                continue;
            }

            foreach (var argument in attribute.ConstructorArguments)
            {
                if (argument.Value is TypeReference named)
                {
                    yield return named;
                }
            }
        }
    }

    /// <summary>
    /// Flattens a type reference to every full name it stands for. <c>DbSet&lt;Product&gt;</c>
    /// must yield <c>DbSet`1</c> and <c>Product</c>, or a rule asking "does anything here mention
    /// the context" would miss <c>DbContextOptions&lt;VelaCommerceDbContext&gt;</c> entirely.
    /// </summary>
    private static IEnumerable<string> Expand(TypeReference reference)
    {
        yield return reference.FullName;

        switch (reference)
        {
            case GenericInstanceType generic:
                foreach (var name in Expand(generic.ElementType))
                {
                    yield return name;
                }

                foreach (var argument in generic.GenericArguments)
                {
                    foreach (var name in Expand(argument))
                    {
                        yield return name;
                    }
                }

                break;

            // Arrays, by-ref and pointer types wrap the type that actually matters.
            case TypeSpecification specification:
                foreach (var name in Expand(specification.ElementType))
                {
                    yield return name;
                }

                break;
        }
    }
}
