using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Tests.LiveRig.Ordering;

/// <summary>
/// Explicit per-method execution order for a test class. Combined with
/// <see cref="TestOrderer"/> this makes the live-rig smoke tests run in a
/// deterministic, model-grouped sequence (see SmokeTests for the rationale).
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
/// value ascending (stable), ties broken by method name. Applied via
/// [TestCaseOrderer] on the SmokeTests class only — LiveRig assembly-wide
/// ordering is deliberately NOT enabled so other test classes keep xUnit's
/// default execution.
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
