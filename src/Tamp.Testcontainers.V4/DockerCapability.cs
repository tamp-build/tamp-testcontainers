namespace Tamp.Testcontainers.V4;

/// <summary>
/// Result of probing the current process for Docker reachability.
/// Returned by <see cref="Testcontainers.Probe"/>.
///
/// <para><c>IsAvailable=true</c> means testcontainers-dotnet (and
/// anything else that spawns containers via Docker) should work in
/// this context. <c>IsAvailable=false</c> means skip the integration
/// tests gracefully — <see cref="UnavailableReason"/> explains why.</para>
/// </summary>
/// <param name="IsAvailable">Docker is reachable from this process.</param>
/// <param name="UnavailableReason">Human-readable reason when <see cref="IsAvailable"/> is false. <c>null</c> when available.</param>
/// <param name="IsRunningInsideDocker">The current process is itself running inside a Docker container (cgroup/dockerenv signal).</param>
/// <param name="DockerSocketAccessible">The Docker socket appears reachable (Unix socket file present OR <c>DOCKER_HOST</c> set to a TCP endpoint).</param>
/// <param name="DockerHostEndpoint">The endpoint Docker would talk to — <c>/var/run/docker.sock</c>, <c>tcp://...</c>, or <c>npipe://...</c>. <c>null</c> if undetermined.</param>
/// <param name="DockerInfoOutput">First line of <c>docker info</c> output (or its stderr on failure), truncated. Useful for telemetry / log messages. <c>null</c> when not run.</param>
public sealed record DockerCapability(
    bool IsAvailable,
    string? UnavailableReason,
    bool IsRunningInsideDocker,
    bool DockerSocketAccessible,
    string? DockerHostEndpoint,
    string? DockerInfoOutput);

/// <summary>
/// Thrown by <see cref="Testcontainers.RequireOrSkip"/> when Docker
/// isn't reachable. The message names the probe finding plus an
/// actionable hint.
/// </summary>
public sealed class TestcontainersUnavailableException : Exception
{
    public DockerCapability Capability { get; }

    public TestcontainersUnavailableException(DockerCapability capability, string message, Exception? inner = null)
        : base(message, inner)
    {
        Capability = capability;
    }
}
