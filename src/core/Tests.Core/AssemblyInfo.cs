using System.Runtime.CompilerServices;

// MiniFleet smoke tier consumes the harness catalog (ScenarioSpec/ScenarioCatalog)
// across assembly boundaries — keep this in sync if the test assembly is renamed.
[assembly: InternalsVisibleTo("Tests.MiniFleet")]
