# Tamp.Testcontainers

Diagnostic library for **testcontainers-dotnet** pipelines. Probes
whether the current process can reach Docker — and surfaces the
"running inside Docker without a mounted socket" trap that silently
hangs integration tests.

```csharp
using Tamp.Testcontainers.V4;
```

| Package | testcontainers-dotnet | Status |
|---|---|---|
| `Tamp.Testcontainers.V4` | 4.x | preview |

Requires `Tamp.Core ≥ 1.0.5`. NOT a CLI wrapper — it doesn't shell out
to `docker` for routine ops. It's a 1-call probe + a typed result.

## The pain

testcontainers-dotnet spawns sibling containers via the Docker daemon.
Two failure modes that cause silent CI hangs:

1. **No Docker on the runner.** Hosted runners may not have Docker
   (e.g. the `windows-latest` image has it; some self-hosted images
   don't). testcontainers waits a long time before erroring.
2. **Running inside Docker without the host socket mounted.**
   Hermetic CI runs the build INSIDE a container. testcontainers
   tries to spawn a *sibling* container by talking to the host
   Docker daemon — but if `/var/run/docker.sock` wasn't bind-mounted
   into the build container, there's no daemon to talk to. Same
   silent hang.

Strata's pain (from the seed message): *"Testcontainers.PostgreSql for
integration tests (currently filtered out at CI time because the
agent can't spawn from inside Docker; STRATA-427 to fix)."*

## What this does

One synchronous call returns a typed `DockerCapability` with:

```csharp
public sealed record DockerCapability(
    bool IsAvailable,                  // overall verdict
    string? UnavailableReason,         // human-readable, actionable
    bool IsRunningInsideDocker,        // /.dockerenv or /proc/1/cgroup
    bool DockerSocketAccessible,       // socket file present or DOCKER_HOST set
    string? DockerHostEndpoint,        // /var/run/docker.sock | tcp://... | npipe://...
    string? DockerInfoOutput);         // first line of `docker info`, for logs
```

It runs `docker info` with a short timeout (default 3s) and inspects
`/.dockerenv`, `/proc/1/cgroup`, `/var/run/docker.sock`, and
`DOCKER_HOST` env. The order matters — the inside-docker-without-socket
case short-circuits before we burn 3s on the subprocess.

## Quick example — gate a target

```csharp
using Tamp;
using Tamp.NetCli.V10;
using Tamp.Testcontainers.V4;

Target IntegrationTests => _ => _
    .OnlyWhen(() => Testcontainers.Probe().IsAvailable,
              "Docker unreachable — testcontainers tests skipped.")
    .Executes(() => DotNet.Test(s => s
        .SetProject("tests/Strata.Api.IntegrationTests/Strata.Api.IntegrationTests.csproj")));
```

Or do a hard require for a target that MUST have Docker:

```csharp
Target MustHaveDocker => _ => _.Executes(() =>
{
    Testcontainers.RequireOrSkip(hint: "this target needs Postgres testcontainers");
    // ... will throw TestcontainersUnavailableException if not
});
```

Or read the result and log diagnostics:

```csharp
Target ReportDockerState => _ => _.Executes(() =>
{
    var cap = Testcontainers.Probe();
    Console.WriteLine($"Docker available:        {cap.IsAvailable}");
    Console.WriteLine($"Running inside Docker:   {cap.IsRunningInsideDocker}");
    Console.WriteLine($"Socket accessible:       {cap.DockerSocketAccessible}");
    Console.WriteLine($"Endpoint:                {cap.DockerHostEndpoint ?? "<none>"}");
    if (!cap.IsAvailable)
        Console.WriteLine($"Why not:                 {cap.UnavailableReason}");
});
```

## Detection details

| Signal | Meaning |
|---|---|
| `/.dockerenv` exists | Process is inside a Docker container (older Docker). |
| `/proc/1/cgroup` mentions `docker` or `containerd` | Process is inside a Docker/containerd container. |
| `/var/run/docker.sock` exists | Docker socket reachable (Linux + macOS Docker Desktop). |
| `DOCKER_HOST` env set | Docker daemon at a custom endpoint (e.g. `tcp://...`). Wins over socket detection. |
| `docker info` exits 0 | Daemon authoritative answer — reachable & permitted. |
| `docker info` exits non-zero | Daemon down OR insufficient permissions OR connection failure. |

## What's NOT in v0.1.0

- **xUnit `[FactRequiringDocker]` trait** — would require taking an
  xUnit dependency. Consumers can roll one in 5 lines:
  ```csharp
  public sealed class FactRequiringDockerAttribute : FactAttribute
  {
      public FactRequiringDockerAttribute()
      {
          var cap = Testcontainers.Probe();
          if (!cap.IsAvailable) Skip = cap.UnavailableReason;
      }
  }
  ```
- **Container lifecycle helpers** (start/stop/wait-healthy). That's
  testcontainers-dotnet's own surface — this package complements it,
  not replaces it.
- **Windows named-pipe socket existence check.** Currently relies on
  `docker info` exit code on Windows (no P/Invoke).

## Releasing

See [MAINTAINERS.md](MAINTAINERS.md).
