using System.Diagnostics;

namespace Tamp.Testcontainers.V4;

/// <summary>
/// Probe for whether the current process can use testcontainers-dotnet.
///
/// <para>The pain (Strata, pain point: "Postgres testcontainers
/// filtered out at CI time because the agent can't spawn from inside
/// Docker"): build runners often execute INSIDE a Docker container
/// for hermeticity. testcontainers-dotnet then tries to spawn SIBLING
/// containers via the Docker daemon — which only works when the host's
/// Docker socket has been bind-mounted into the build container. If it
/// hasn't, the symptom is a silent hang or a confusing connection
/// error.</para>
///
/// <para>This probe answers the question "should I even try?" before
/// the test starts:</para>
/// <code>
/// Target IntegrationTests => _ => _
///     .OnlyWhen(() =&gt; Testcontainers.Probe().IsAvailable,
///               "Docker unreachable — skipping integration tests.")
///     .Executes(...);
/// </code>
/// </summary>
public static class Testcontainers
{
    /// <summary>
    /// Probe the current process for Docker reachability.
    ///
    /// <para>Runs <c>docker info</c> with a short timeout (default 3s)
    /// and inspects environment signals (<c>DOCKER_HOST</c>,
    /// <c>/.dockerenv</c>, the Unix socket path). Pure: no
    /// side-effects beyond reading env and spawning the
    /// <c>docker info</c> subprocess.</para>
    /// </summary>
    public static DockerCapability Probe(TimeSpan? timeout = null)
    {
        var t = timeout ?? TimeSpan.FromSeconds(3);
        return ProbeCore(
            fileExists: File.Exists,
            envGet: Environment.GetEnvironmentVariable,
            runDockerInfo: () => RunDockerInfo(t));
    }

    /// <summary>
    /// Throws <see cref="TestcontainersUnavailableException"/> when
    /// Docker isn't reachable. Use this in a target's
    /// <c>Executes</c> block when you want a hard failure rather than
    /// a skip.
    /// </summary>
    /// <param name="hint">Optional caller-supplied hint appended to the error message.</param>
    public static void RequireOrSkip(string? hint = null)
    {
        var cap = Probe();
        if (cap.IsAvailable) return;
        var msg = $"Docker not available: {cap.UnavailableReason ?? "unknown reason"}.";
        if (!string.IsNullOrEmpty(hint)) msg += $" Hint: {hint}";
        throw new TestcontainersUnavailableException(cap, msg);
    }

    /// <summary>
    /// Pure version of the probe — every side-effectful operation is
    /// injected so unit tests can stub fileystem / env / subprocess.
    /// </summary>
    internal static DockerCapability ProbeCore(
        Func<string, bool> fileExists,
        Func<string, string?> envGet,
        Func<(int ExitCode, string Stdout, string Stderr)> runDockerInfo)
    {
        var isInsideDocker = DetectRunningInsideDocker(fileExists);
        var (socketAccessible, endpoint) = DetectDockerEndpoint(fileExists, envGet);

        // If we're inside Docker AND no socket reachable, we know the
        // answer without spawning a subprocess: the sibling-containers
        // trap. Surface a specific reason.
        if (isInsideDocker && !socketAccessible)
        {
            return new DockerCapability(
                IsAvailable: false,
                UnavailableReason: "Running inside Docker, but no host socket appears mounted. " +
                                   "testcontainers will try to spawn sibling containers and fail. " +
                                   "Mount /var/run/docker.sock into this container, or run the tests on a non-containerized host.",
                IsRunningInsideDocker: true,
                DockerSocketAccessible: false,
                DockerHostEndpoint: null,
                DockerInfoOutput: null);
        }

        // Try `docker info`. Non-zero exit = daemon unreachable or
        // permission issue.
        (int exit, string stdout, string stderr) info;
        try
        {
            info = runDockerInfo();
        }
        catch (Exception ex) when (ex is FileNotFoundException || ex is System.ComponentModel.Win32Exception)
        {
            return new DockerCapability(
                IsAvailable: false,
                UnavailableReason: "`docker` CLI not found on PATH.",
                IsRunningInsideDocker: isInsideDocker,
                DockerSocketAccessible: socketAccessible,
                DockerHostEndpoint: endpoint,
                DockerInfoOutput: null);
        }
        catch (Exception ex)
        {
            return new DockerCapability(
                IsAvailable: false,
                UnavailableReason: $"Failed to run `docker info`: {ex.GetType().Name}: {ex.Message}",
                IsRunningInsideDocker: isInsideDocker,
                DockerSocketAccessible: socketAccessible,
                DockerHostEndpoint: endpoint,
                DockerInfoOutput: null);
        }

        if (info.exit != 0)
        {
            var output = !string.IsNullOrWhiteSpace(info.stderr) ? info.stderr : info.stdout;
            return new DockerCapability(
                IsAvailable: false,
                UnavailableReason: $"`docker info` exited {info.exit}. " +
                                   $"Daemon may be down or this user lacks permission. " +
                                   $"First line: {FirstLine(output)}",
                IsRunningInsideDocker: isInsideDocker,
                DockerSocketAccessible: socketAccessible,
                DockerHostEndpoint: endpoint,
                DockerInfoOutput: FirstLine(output));
        }

        return new DockerCapability(
            IsAvailable: true,
            UnavailableReason: null,
            IsRunningInsideDocker: isInsideDocker,
            DockerSocketAccessible: socketAccessible,
            DockerHostEndpoint: endpoint,
            DockerInfoOutput: FirstLine(info.stdout));
    }

    private static bool DetectRunningInsideDocker(Func<string, bool> fileExists)
    {
        // The canonical signals: /.dockerenv exists (older docker)
        // or /proc/1/cgroup mentions docker / containerd.
        if (fileExists("/.dockerenv")) return true;
        try
        {
            if (fileExists("/proc/1/cgroup"))
            {
                var cgroup = File.ReadAllText("/proc/1/cgroup");
                if (cgroup.Contains("docker", StringComparison.OrdinalIgnoreCase) ||
                    cgroup.Contains("containerd", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // /proc/1/cgroup not readable — fall through and assume not containerized.
        }
        return false;
    }

    private static (bool Accessible, string? Endpoint) DetectDockerEndpoint(Func<string, bool> fileExists, Func<string, string?> envGet)
    {
        // DOCKER_HOST wins if set (Docker Desktop on macOS sometimes sets this,
        // also CI configurations that talk to a remote daemon).
        var dockerHost = envGet("DOCKER_HOST");
        if (!string.IsNullOrEmpty(dockerHost)) return (true, dockerHost);

        // Unix socket (Linux + macOS Docker Desktop).
        const string UnixSocket = "/var/run/docker.sock";
        if (fileExists(UnixSocket)) return (true, UnixSocket);

        // Windows named pipe — can't easily test for existence without a P/Invoke.
        // We mark inaccessible-until-proven-otherwise; the `docker info` call below
        // is the authoritative check.
        if (OperatingSystem.IsWindows()) return (false, "npipe://./pipe/docker_engine");

        return (false, null);
    }

    private static (int ExitCode, string Stdout, string Stderr) RunDockerInfo(TimeSpan timeout)
    {
        var psi = new ProcessStartInfo("docker", "info --format \"{{.ServerVersion}}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null for `docker info`.");
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return (1, stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "", "timeout");
        }
        return (p.ExitCode,
            stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "",
            stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "");
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var i = s.IndexOfAny(['\r', '\n']);
        var line = i < 0 ? s : s[..i];
        return line.Length > 256 ? line[..256] + "…" : line;
    }
}
