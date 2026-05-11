using Xunit;

namespace Tamp.Testcontainers.V4.Tests;

public sealed class TestcontainersTests
{
    /// <summary>Builder for the injected probe seam — defaults everything to "not present / not in docker / docker info succeeded".</summary>
    private sealed class ProbeStubs
    {
        public HashSet<string> ExistingFiles { get; } = new();
        public Dictionary<string, string> EnvVars { get; } = new();
        public int DockerInfoExit { get; set; } = 0;
        public string DockerInfoStdout { get; set; } = "Server Version: 24.0.5\n";
        public string DockerInfoStderr { get; set; } = string.Empty;
        public Exception? DockerInfoThrows { get; set; }

        public DockerCapability Probe() => Testcontainers.ProbeCore(
            fileExists: p => ExistingFiles.Contains(p),
            envGet: k => EnvVars.TryGetValue(k, out var v) ? v : null,
            runDockerInfo: () =>
            {
                if (DockerInfoThrows is not null) throw DockerInfoThrows;
                return (DockerInfoExit, DockerInfoStdout, DockerInfoStderr);
            });
    }

    // ---- happy path ----

    [Fact]
    public void Probe_With_Reachable_Socket_And_Successful_DockerInfo_Reports_Available()
    {
        var stubs = new ProbeStubs();
        stubs.ExistingFiles.Add("/var/run/docker.sock");

        var cap = stubs.Probe();
        Assert.True(cap.IsAvailable);
        Assert.Null(cap.UnavailableReason);
        Assert.False(cap.IsRunningInsideDocker);
        Assert.True(cap.DockerSocketAccessible);
        Assert.Equal("/var/run/docker.sock", cap.DockerHostEndpoint);
        Assert.Equal("Server Version: 24.0.5", cap.DockerInfoOutput);
    }

    [Fact]
    public void Probe_With_DOCKER_HOST_Env_Wins_Over_Socket()
    {
        var stubs = new ProbeStubs();
        stubs.EnvVars["DOCKER_HOST"] = "tcp://docker-remote.example.com:2375";
        // Note: no /var/run/docker.sock — and DOCKER_HOST is still reported.

        var cap = stubs.Probe();
        Assert.True(cap.IsAvailable);
        Assert.Equal("tcp://docker-remote.example.com:2375", cap.DockerHostEndpoint);
    }

    // ---- sibling-container trap ----

    [Fact]
    public void Probe_Inside_Docker_Without_Mounted_Socket_Reports_Specific_Reason()
    {
        var stubs = new ProbeStubs();
        stubs.ExistingFiles.Add("/.dockerenv");
        // /var/run/docker.sock NOT in ExistingFiles — no socket mounted.

        var cap = stubs.Probe();
        Assert.False(cap.IsAvailable);
        Assert.True(cap.IsRunningInsideDocker);
        Assert.False(cap.DockerSocketAccessible);
        Assert.Null(cap.DockerHostEndpoint);
        Assert.Contains("Running inside Docker", cap.UnavailableReason);
        Assert.Contains("/var/run/docker.sock", cap.UnavailableReason);
    }

    [Fact]
    public void Probe_Inside_Docker_With_Mounted_Socket_Reports_Available()
    {
        // The "sibling containers with socket mounted" pattern — works fine.
        var stubs = new ProbeStubs();
        stubs.ExistingFiles.Add("/.dockerenv");
        stubs.ExistingFiles.Add("/var/run/docker.sock");

        var cap = stubs.Probe();
        Assert.True(cap.IsAvailable);
        Assert.True(cap.IsRunningInsideDocker);
        Assert.True(cap.DockerSocketAccessible);
    }

    // ---- failure modes ----

    [Fact]
    public void Probe_When_DockerInfo_Exits_NonZero_Reports_Daemon_Down()
    {
        var stubs = new ProbeStubs();
        stubs.ExistingFiles.Add("/var/run/docker.sock");
        stubs.DockerInfoExit = 1;
        stubs.DockerInfoStderr = "Cannot connect to the Docker daemon at unix:///var/run/docker.sock.\nIs the docker daemon running?";

        var cap = stubs.Probe();
        Assert.False(cap.IsAvailable);
        Assert.Contains("exited 1", cap.UnavailableReason);
        Assert.Contains("Cannot connect to the Docker daemon", cap.UnavailableReason);
    }

    [Fact]
    public void Probe_When_Docker_Cli_Not_Found_Reports_Path_Issue()
    {
        var stubs = new ProbeStubs
        {
            DockerInfoThrows = new FileNotFoundException("`docker` not found"),
        };

        var cap = stubs.Probe();
        Assert.False(cap.IsAvailable);
        Assert.Contains("not found on PATH", cap.UnavailableReason);
    }

    [Fact]
    public void Probe_When_Win32Exception_Reports_Path_Issue()
    {
        // Windows surfaces "binary not found" as Win32Exception.
        var stubs = new ProbeStubs
        {
            DockerInfoThrows = new System.ComponentModel.Win32Exception("The system cannot find the file specified"),
        };

        var cap = stubs.Probe();
        Assert.False(cap.IsAvailable);
        Assert.Contains("not found on PATH", cap.UnavailableReason);
    }

    [Fact]
    public void Probe_When_DockerInfo_Throws_Unexpected_Captures_Type_And_Message()
    {
        var stubs = new ProbeStubs
        {
            DockerInfoThrows = new TimeoutException("docker info timed out after 3s"),
        };

        var cap = stubs.Probe();
        Assert.False(cap.IsAvailable);
        Assert.Contains("TimeoutException", cap.UnavailableReason);
        Assert.Contains("docker info timed out", cap.UnavailableReason);
    }

    [Fact]
    public void Probe_DockerInfo_FirstLine_Truncated_To_256_Chars()
    {
        var stubs = new ProbeStubs();
        stubs.ExistingFiles.Add("/var/run/docker.sock");
        stubs.DockerInfoStdout = new string('x', 1000);

        var cap = stubs.Probe();
        Assert.NotNull(cap.DockerInfoOutput);
        Assert.True(cap.DockerInfoOutput!.Length <= 256 + 1, // +1 for the truncation ellipsis
            $"Expected first line ≤ 257 chars (256 + ellipsis); got {cap.DockerInfoOutput.Length}.");
    }

    // ---- inside-docker detection variants ----

    [Fact]
    public void Probe_Detects_Inside_Docker_Via_Dockerenv()
    {
        var stubs = new ProbeStubs();
        stubs.ExistingFiles.Add("/.dockerenv");
        stubs.ExistingFiles.Add("/var/run/docker.sock");
        var cap = stubs.Probe();
        Assert.True(cap.IsRunningInsideDocker);
    }

    [Fact]
    public void Probe_With_Neither_Socket_Nor_DOCKER_HOST_Falls_Through_To_DockerInfo()
    {
        // No filesystem evidence, no env var. The probe still calls
        // `docker info` — which would normally fail, but our stub
        // returns success. The wrapper records "no endpoint detected"
        // but the daemon answered, so IsAvailable=true.
        var stubs = new ProbeStubs();
        // No files. No DOCKER_HOST.
        var cap = stubs.Probe();
        Assert.True(cap.IsAvailable);
        Assert.False(cap.DockerSocketAccessible);
        // Endpoint unknown (or "npipe..." on Windows hosts).
    }

    // ---- RequireOrSkip ----

    [Fact]
    public void RequireOrSkip_When_Available_Returns_Silently()
    {
        // Can't easily mock the public Probe(), so this test relies on
        // the integration shape — when the local machine HAS Docker, this
        // returns silently; when it doesn't, it throws. Either is a
        // valid outcome — we just verify the call path doesn't throw
        // unexpected exception types.
        try
        {
            Testcontainers.RequireOrSkip();
            // No throw → docker available. Fine.
        }
        catch (TestcontainersUnavailableException)
        {
            // Expected when no Docker — fine.
        }
        catch (Exception other)
        {
            Assert.Fail($"RequireOrSkip threw unexpected exception type: {other.GetType().Name}: {other.Message}");
        }
    }

    [Fact]
    public void TestcontainersUnavailableException_Carries_Capability()
    {
        var cap = new DockerCapability(
            IsAvailable: false,
            UnavailableReason: "unit test",
            IsRunningInsideDocker: false,
            DockerSocketAccessible: false,
            DockerHostEndpoint: null,
            DockerInfoOutput: null);
        var ex = new TestcontainersUnavailableException(cap, "test message");
        Assert.Same(cap, ex.Capability);
        Assert.Equal("test message", ex.Message);
    }

    // ---- record equality / shape ----

    [Fact]
    public void DockerCapability_Record_Has_Value_Equality()
    {
        var a = new DockerCapability(true, null, false, true, "/var/run/docker.sock", "Server Version: 24");
        var b = new DockerCapability(true, null, false, true, "/var/run/docker.sock", "Server Version: 24");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
