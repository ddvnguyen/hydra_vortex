using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Tests.LiveRig.Ordering;

/// <summary>
/// Explicit per-method execution order for the whole live-rig assembly.
/// Combined with <see cref="TestOrderer"/> (wired assembly-wide via
/// Ordering/AssemblyInfo.cs) every test method in Tests.LiveRig runs in a
/// deterministic, model-grouped sequence (see SmokeTests / AssemblyInfo for
/// the rationale). Each method must carry exactly one [TestOrder(n)] with a
/// globally unique n.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class TestOrderAttribute : Attribute
{
    public TestOrderAttribute(int order) => Order = order;

    /// <summary>Ascending sort key: lower runs first. Missing attribute → int.MaxValue (last).</summary>
    public int Order { get; }
}

/// <summary>
/// xUnit ITestCaseOrderer: sorts test cases by <see cref="TestOrderAttribute"/>
/// value ascending (stable), ties broken by method name. Applied assembly-wide
/// via [assembly: TestCaseOrderer(...)] (Ordering/AssemblyInfo.cs) so ALL
/// Tests.LiveRig test cases — across every class in the "LiveRig" collection —
/// sort by the global sequence number. A method without the attribute sorts
/// last (int.MaxValue), so every rig test must be annotated.
/// </summary>
public sealed class TestOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase
    {
        var attributeType = typeof(TestOrderAttribute).AssemblyQualifiedName!;
        return testCases
            .OrderBy(tc => tc.TestMethod.Method
                .GetCustomAttributes(attributeType)
                .SingleOrDefault()
                ?.GetNamedArgument<int>("Order") ?? int.MaxValue)
            .ThenBy(tc => tc.TestMethod.Method.Name, StringComparer.Ordinal);
    }
}
