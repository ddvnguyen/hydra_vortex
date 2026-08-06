using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Tests.LiveRig;

/// <summary>
/// Shared HTTP helpers for live-rig tests. Mirrors the Python helpers
/// from conftest.py and the individual test modules.
/// </summary>
internal static class HttpHelpers
{
    /// <summary>
    /// Single shared HttpClient for all live-rig tests. A fresh HttpClient per
    /// call exhausts the ephemeral port pool under sustained request volume —
    /// each connection sits in TIME_WAIT for ~60s after close (issue #552).
    /// Timeout is unbounded here; callers enforce their own per-call deadline
    /// via CancellationTokenSource, since deadlines vary from 5s (health
    /// checks) to 600s (40k-context multiturn tests).
    /// </summary>
    public static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

    public const double CharsPerToken = 3.0;

    private static readonly string[] SeedParas =
    [
        "Software engineering encompasses requirements gathering, system design, implementation, testing, deployment, and maintenance. Each phase requires careful planning and execution to ensure quality outcomes.",
        "Database indexing strategies significantly impact query performance. B-tree indexes excel at range queries, while hash indexes optimize point lookups. Understanding access patterns guides index selection.",
        "Container orchestration platforms automate deployment, scaling, and management of containerized applications. Kubernetes provides service discovery, load balancing, and automated rollouts.",
        "Distributed systems require careful handling of consistency, availability, and partition tolerance. The CAP theorem states that a distributed system can only guarantee two of these three properties simultaneously.",
        "API design best practices include consistent naming conventions, proper versioning strategies, comprehensive error handling, and thorough documentation. RESTful APIs should leverage HTTP methods correctly.",
        "Test-driven development writes tests before production code, ensuring requirements are clearly understood. The red-green-refactor cycle promotes incremental development and robust test coverage.",
        "Network protocols like TCP provide reliable, ordered delivery of data between applications. UDP offers lower latency but no delivery guarantees, making it suitable for real-time applications.",
        "Microservices architecture decomposes applications into independently deployable services communicating over well-defined APIs. Each service focuses on a specific business capability.",
        "Caching strategies improve application performance by storing frequently accessed data in fast storage layers. Common patterns include cache-aside, write-through, and write-behind caching.",
        "Authentication and authorization are fundamental security concerns. OAuth 2.0 provides delegated access, while JWT tokens enable stateless authentication across distributed systems.",
        "Monitoring and observability are critical for production systems. Metrics, logs, and traces provide visibility into system behavior, enabling rapid incident response and performance optimization.",
        "Machine learning pipelines transform raw data into trained models through stages of collection, preprocessing, feature engineering, training, evaluation, and deployment.",
        "Concurrency control mechanisms prevent race conditions in multi-threaded applications. Mutexes, semaphores, and atomic operations provide different levels of thread safety guarantees.",
        "Cloud infrastructure patterns include lift-and-shift migration, re-platforming with managed services, and cloud-native architecture using serverless computing and managed databases.",
        "Code review practices improve code quality through peer examination. Reviewers check for correctness, maintainability, performance, security, and adherence to team coding standards.",
        "Load balancing distributes incoming traffic across multiple servers to ensure reliability and performance. Algorithms include round-robin, least connections, and consistent hashing.",
        "Data serialization formats like Protocol Buffers and Apache Avro provide efficient binary encoding with schema evolution support, making them suitable for inter-service communication.",
        "Dead letter queues handle messages that cannot be processed successfully. They isolate problematic messages for analysis while allowing the main processing pipeline to continue uninterrupted.",
        "Circuit breaker patterns prevent cascading failures in distributed systems by detecting when a downstream service is unhealthy and failing fast instead of waiting for timeouts.",
        "Infrastructure as code manages cloud resources through declarative configuration files. Tools like Terraform and Pulumi enable version-controlled, reproducible infrastructure deployment.",
        "Reactive programming models handle asynchronous data streams and propagate changes through functional transformations. This approach excels in event-driven and real-time applications.",
        "Feature flags enable safe deployment of new functionality by toggling features on or off without code changes. They support canary releases, A/B testing, and gradual rollouts.",
        "Rate limiting protects APIs from abuse by restricting the number of requests a client can make within a time window. Common algorithms include token bucket and sliding window.",
        "Message queues decouple producers and consumers, enabling asynchronous communication and load leveling. RabbitMQ, Apache Kafka, and Amazon SQS are popular message broker implementations.",
        "Search indexing builds inverted data structures for fast full-text retrieval. Elasticsearch and Apache Solr provide distributed search capabilities with relevance scoring and faceted navigation.",
        "Pipeline automation reduces manual effort in software delivery. Continuous integration builds and tests code changes automatically, while continuous deployment pushes verified changes to production.",
        "Data partitioning strategies distribute large datasets across multiple nodes for scalability. Horizontal partitioning splits rows across shards, while vertical partitioning separates columns.",
        "Service mesh implementations like Istio and Linkerd provide observability, traffic management, and security for microservice communication without requiring application code changes.",
        "WebAssembly enables high-performance code execution in web browsers, supporting multiple languages compiled to a common binary format. It unlocks new possibilities for web applications.",
        "Chaos engineering proactively tests system resilience by introducing controlled failures. Experiments verify that systems handle unexpected conditions without degrading user experience.",
        "Event sourcing stores state changes as an append-only event log, enabling audit trails, temporal queries, and event-driven architectures. The current state is derived by replaying events.",
        "Configuration management tools like Ansible and Puppet automate server provisioning and application deployment, ensuring consistent environments across development, staging, and production.",
        "Connection pooling reuses database connections to reduce the overhead of establishing new connections. Pool size must balance resource usage against concurrent workload demands.",
        "GraphQL provides a flexible query language for APIs, allowing clients to request exactly the data they need. This reduces over-fetching and under-fetching common in REST APIs.",
        "Time-series databases optimize storage and querying for timestamped data points. They excel at handling monitoring metrics, sensor data, and financial market data at scale.",
    ];

    /// <summary>Generate approximately <paramref name="approxTokens"/> tokens of filler text.</summary>
    public static string GenerateText(int approxTokens)
    {
        var targetChars = (int)(approxTokens * CharsPerToken);
        var parts = new List<string>();
        var length = 0;
        while (length < targetChars)
        {
            foreach (var p in SeedParas)
            {
                if (length >= targetChars) break;
                parts.Add(p);
                length += p.Length + 1;
            }
        }
        return string.Join(" ", parts).Substring(0, Math.Min(targetChars, string.Join(" ", parts).Length));
    }

    /// <summary>Extract content from a chat completion message (content or reasoning_content).</summary>
    public static string GetOutputText(JsonElement message)
    {
        if (message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            return c.GetString() ?? "";
        if (message.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
            return rc.GetString() ?? "";
        return "";
    }

    // ── Prometheus text-exposition parser ────────────────────────────────

    private static readonly Regex LinePattern = new(
        @"^(?<name>[a-zA-Z_:][a-zA-Z0-9_:]*)(?:\{(?<labels>[^}]*)\})?\s+(?<value>[0-9.eE+\-]+)\s*$",
        RegexOptions.Compiled);

    public sealed record PromSample(string Name, Dictionary<string, string> Labels, double Value);

    /// <summary>Parse Prometheus text-exposition body into samples.</summary>
    public static List<PromSample> ParsePromLines(string body)
    {
        var outList = new List<PromSample>();
        foreach (var line in body.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;
            var m = LinePattern.Match(line);
            if (!m.Success) continue;

            var name = m.Groups["name"].Value;
            var labels = new Dictionary<string, string>();
            if (m.Groups["labels"].Success)
            {
                foreach (var kv in m.Groups["labels"].Value.Split(','))
                {
                    var eq = kv.IndexOf('=');
                    if (eq < 0) continue;
                    var k = kv.Substring(0, eq).Trim();
                    var v = kv.Substring(eq + 1).Trim().Trim('"');
                    labels[k] = v;
                }
            }
            if (double.TryParse(m.Groups["value"].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var val))
                outList.Add(new PromSample(name, labels, val));
        }
        return outList;
    }

    /// <summary>Sum a Prometheus counter across all series matching name (and optional label subset).</summary>
    public static double SumCounter(List<PromSample> samples, string name,
        Dictionary<string, string>? labels = null)
    {
        double total = 0;
        foreach (var s in samples)
        {
            if (s.Name != name) continue;
            if (labels != null && !labels.All(kv => s.Labels.TryGetValue(kv.Key, out var v) && v == kv.Value))
                continue;
            total += s.Value;
        }
        return total;
    }

    /// <summary>Parse llamacpp Prometheus metrics text into a name→value dictionary.</summary>
    public static Dictionary<string, double> ParseLlamaMetrics(string metricsText)
    {
        var metrics = new Dictionary<string, double>();
        foreach (var line in metricsText.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                continue;
            var spaceIdx = trimmed.IndexOf(' ');
            if (spaceIdx < 0) continue;
            var name = trimmed.Substring(0, spaceIdx);
            var valStr = trimmed.Substring(spaceIdx + 1);
            if (double.TryParse(valStr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var val))
                metrics[name] = val;
        }
        return metrics;
    }
}
