using System.Reflection;

namespace BjoRx.Tests;

/// <summary>Marks a public static method with no parameters as a test.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute;

/// <summary>Thrown by <see cref="Check"/> when a test fails.</summary>
public sealed class CheckFailed(string message) : Exception(message);

public static class Check
{
    public static void True(bool condition, string what)
    {
        if (!condition) throw new CheckFailed(what);
    }

    public static void Equal<T>(T expected, T actual, string what = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new CheckFailed($"{what}: expected {expected}, got {actual}");
        }
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string what = "")
    {
        var e = expected.ToList();
        var a = actual.ToList();
        if (!e.SequenceEqual(a))
        {
            throw new CheckFailed($"{what}: expected [{string.Join(", ", e)}], got [{string.Join(", ", a)}]");
        }
    }

    public static void Throws<TException>(Action action, string what) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new CheckFailed($"{what}: expected {typeof(TException).Name}");
    }
}

/// <summary>
/// Runs every <see cref="TestAttribute"/> method in this assembly. Arguments
/// filter by substring of "Class.Method". Exit code is the number of failures.
/// </summary>
public static class Runner
{
    public static int Main(string[] args)
    {
        var tests = typeof(Runner).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
            .Select(m => (Name: $"{m.DeclaringType!.Name}.{m.Name}", Method: m))
            .Where(t => args.Length == 0 || args.Any(a => t.Name.Contains(a, StringComparison.Ordinal)))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        int failed = 0;
        foreach (var (name, method) in tests)
        {
            try
            {
                method.Invoke(null, null);
            }
            catch (TargetInvocationException e)
            {
                failed++;
                var inner = e.InnerException!;
                Console.WriteLine($"FAIL {name}: {(inner is CheckFailed ? inner.Message : inner.ToString())}");
            }
        }

        Console.WriteLine($"{tests.Count - failed}/{tests.Count} tests passed");
        return failed;
    }
}
