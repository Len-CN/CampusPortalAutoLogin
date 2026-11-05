using System.Reflection;

namespace CampusPortalAutoLogin.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class CaseAttribute : Attribute { }

public static class Check
{
    public static void True(bool value, string message = "断言失败") { if (!value) throw new Exception(message); }
    public static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"期望 {expected}，实际 {actual}"); }
    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"期望抛出 {typeof(T).Name}");
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var methods = Assembly.GetExecutingAssembly().GetTypes().SelectMany(t => t.GetMethods())
            .Where(m => m.GetCustomAttribute<CaseAttribute>() != null)
            .Where(m => args.Length > 0 || m.DeclaringType!.Name != "WindowsIntegrationTests")
            .Where(m => args.Length == 0 || (m.DeclaringType!.Name + "." + m.Name).Contains(args[0], StringComparison.OrdinalIgnoreCase));
        int passed = 0, failed = 0;
        foreach (var method in methods)
        {
            try
            {
                if (method.Invoke(null, null) is Task task) await task;
                Console.WriteLine($"通过 {method.DeclaringType!.Name}.{method.Name}"); passed++;
            }
            catch (Exception e) { Console.WriteLine($"失败 {method.DeclaringType!.Name}.{method.Name}: {(e.InnerException ?? e).Message}"); failed++; }
        }
        Console.WriteLine($"测试结果：{passed} 通过，{failed} 失败");
        return failed == 0 && passed > 0 ? 0 : 1;
    }
}
