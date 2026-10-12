using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;

namespace DotNetCompatibility;

/// <summary>
/// Reflection and the code it makes at run time: members, activation, attributes, generics, emitted IL and compiled
/// expression trees.
/// </summary>
internal static class ReflectionChecks
{
    /// <summary>
    /// The area's checks.
    /// </summary>
    /// <returns>The checks.</returns>
    public static IEnumerable<Check> All()
    {
        yield return new Check("Reflection", "Invoke", () =>
        {
            var method = typeof(Sample).GetMethod(nameof(Sample.Add))!;
            var value = (int)method.Invoke(new Sample(), [2, 3])!;
            return CheckResult.Now(value == 5, $"{value}");
        });
        yield return new Check("Reflection", "Activator", () =>
        {
            var sample = (Sample)Activator.CreateInstance(typeof(Sample))!;
            return CheckResult.Now(sample.Add(1, 1) == 2, "created");
        });
        yield return new Check("Reflection", "Attributes", () =>
        {
            var attribute = typeof(Sample).GetCustomAttribute<SampleAttribute>();
            return CheckResult.Now(attribute?.Label == "compatibility", attribute?.Label ?? "none");
        });
        yield return new Check("Reflection", "Generics", () =>
        {
            var type = typeof(List<>).MakeGenericType(typeof(Sample));
            var list = (System.Collections.IList)Activator.CreateInstance(type)!;
            list.Add(new Sample());
            return CheckResult.Now(list.Count == 1 && type.GetGenericArguments()[0] == typeof(Sample), type.Name);
        });
        yield return new Check("Reflection", "Emit", () =>
        {
            var method = new DynamicMethod("Multiply", typeof(int), [typeof(int), typeof(int)]);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Mul);
            il.Emit(OpCodes.Ret);
            var multiply = method.CreateDelegate<Func<int, int, int>>();
            return CheckResult.Now(multiply(6, 7) == 42, $"{multiply(6, 7)}");
        });
        yield return new Check("Reflection", "Expressions", () =>
        {
            var x = Expression.Parameter(typeof(int), "x");
            var square = Expression.Lambda<Func<int, int>>(Expression.Multiply(x, x), x).Compile();
            return CheckResult.Now(square(12) == 144, $"{square(12)}");
        });
    }

    [AttributeUsage(AttributeTargets.Class)]
    private sealed class SampleAttribute(string label) : Attribute
    {
        public string Label { get; } = label;
    }

    [Sample("compatibility")]
    private sealed class Sample
    {
        public int Add(int left, int right) => left + right;
    }
}
