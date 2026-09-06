namespace Hydra.Core.Configuration;

/// <summary>
/// T2 test-instance config: two cores (A/B) share appsettings.Test.json.
/// Differentiated via HYDRA_INSTANCE_ID env var. Only active when
/// HYDRA_INSTANCE=test; otherwise prod is untouched.
/// </summary>
public sealed class HydraTestConfig
{
    public const string InstanceEnvVar = "HYDRA_INSTANCE";
    public const string InstanceIdEnvVar = "HYDRA_INSTANCE_ID";
    public const string TestInstanceValue = "test";

    public static bool IsTestInstance =>
        string.Equals(Environment.GetEnvironmentVariable(InstanceEnvVar), TestInstanceValue,
            StringComparison.OrdinalIgnoreCase);

    public static string InstanceId =>
        Environment.GetEnvironmentVariable(InstanceIdEnvVar) ?? "";

    /// <summary>
    /// Validate HYDRA_INSTANCE_ID when in test mode. Throws if invalid.
    /// No-op when HYDRA_INSTANCE is unset (prod).
    /// </summary>
    public static void ValidateIfTestInstance()
    {
        if (!IsTestInstance) return;

        var id = InstanceId;
        if (id != "A" && id != "B")
            throw new InvalidOperationException(
                $"HYDRA_INSTANCE=test requires HYDRA_INSTANCE_ID=A|B, got '{id}'");
    }
}
