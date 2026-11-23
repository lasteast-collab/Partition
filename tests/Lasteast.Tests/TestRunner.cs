using System.Reflection;
using System.Runtime.CompilerServices;

namespace Lasteast.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute { }

public sealed class AssertionException(string message) : Exception(message);

public static class Assert
{
    public static void True(bool condition, string? message = null, [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (!condition) throw new AssertionException(message ?? $"Expected true: {expr}");
    }

    public static void Equal<T>(T expected, T actual, string? message = null, [CallerArgumentExpression(nameof(actual))] string? expr = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{message ?? expr}: expected <{expected}> but was <{actual}>");
    }

    public static void Near(double expected, double actual, double tolerance = 1e-6, [CallerArgumentExpression(nameof(actual))] string? expr = null)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new AssertionException($"{expr}: expected ≈{expected} but was {actual}");
    }

    public static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T ex) { return ex; }
        throw new AssertionException($"Expected {typeof(T).Name}");
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var tests = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static).Select(m => (t, m)))
            .Where(x => x.m.GetCustomAttribute<TestAttribute>() is not null)
            .Where(x => args.Length == 0 || args.Any(a => $"{x.t.Name}.{x.m.Name}".Contains(a, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.t.Name).ThenBy(x => x.m.Name)
            .ToList();

        int failed = 0;
        foreach (var (type, method) in tests)
        {
            var name = $"{type.Name}.{method.Name}";
            try
            {
                var result = method.Invoke(null, null);
                if (result is Task task) await task;
                Console.WriteLine($"  PASS  {name}");
            }
            catch (Exception ex)
            {
                failed++;
                var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
                Console.WriteLine($"  FAIL  {name}\n        {inner.GetType().Name}: {inner.Message}"); if (Environment.GetEnvironmentVariable("LE_TRACE") == "1") Console.WriteLine(inner.StackTrace);
            }
        }
        Console.WriteLine($"\n{tests.Count - failed}/{tests.Count} passed");
        return failed == 0 ? 0 : 1;
    }
}
