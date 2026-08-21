using Xunit;

/// <summary>
/// Assembly-wide test-case ordering for the live-rig suite (#470).
///
/// Every test method in Tests.LiveRig carries a global [TestOrder(n)]
/// (Ordering/TestOrdering.cs) so ALL tests in the assembly run in a
/// deterministic, model-grouped sequence regardless of class boundaries
/// (xUnit's default per-class alphabetical order interleaves models and
/// causes 8-10 avoidable model swaps per run). The TestOrderer sorts by the
/// global number ascending (stable, ties broken by method name; a method
/// without the attribute sorts last).
///
/// Group order (see each test class header / SmokeTests for the rationale):
///   Group 1 (orders 1-27):  default moe-35b-solo (balanced) resident — ZERO swaps
///   Group 2 (orders 28-30): moe-35b-pd — one intentional swap into the P/D split
///   Group 3 (orders 31-34): dense-27b-combined — one intentional swap into COMBINED
/// The suite therefore makes exactly 2 model swaps total.
/// </summary>
[assembly: TestCaseOrderer("Tests.LiveRig.Ordering.TestOrderer", "Tests.LiveRig")]
