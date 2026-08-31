namespace SocietyHub.IntegrationTests;

/// <summary>
/// A fact that is skipped, not failed, when Docker is not running.
///
/// A developer reading this code on a laptop with Docker Desktop closed is in a normal state,
/// not a broken one. Failing there would make the suite red for an environmental reason, and a
/// suite that is routinely red for reasons nobody needs to act on is a suite people stop
/// reading — which costs more than the coverage these tests add.
///
/// CI is the opposite case and is handled in the workflow: there, Docker is guaranteed, so a
/// skip means something is wrong with the runner rather than with the developer's machine.
/// Set <c>SOCIETYHUB_REQUIRE_DOCKER=1</c> to turn a missing daemon back into a failure.
/// </summary>
public sealed class RequiresDockerFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(Probe, isThreadSafe: true);

    public RequiresDockerFactAttribute()
    {
        if (Available.Value)
        {
            return;
        }

        if (Environment.GetEnvironmentVariable("SOCIETYHUB_REQUIRE_DOCKER") == "1")
        {
            // Deliberately not skipped. In CI a missing daemon is a broken runner, and
            // silently skipping would let the integration suite quietly stop running while
            // the pipeline stayed green.
            return;
        }

        Skip = "Docker is not available. Start Docker Desktop to run the integration suite.";
    }

    /// <summary>
    /// Checks for the daemon's endpoint rather than shelling out to <c>docker</c>.
    ///
    /// The CLI may be absent while the daemon is running, and vice versa; the socket or named
    /// pipe is what Testcontainers actually connects to, so it is what is worth asking about.
    /// </summary>
    private static bool Probe()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Enumerating the pipe directory is the only reliable way to test for a named
                // pipe's existence — File.Exists returns false for pipes.
                return Directory
                    .GetFiles(@"\\.\pipe\")
                    .Any(p => p.Contains("docker_engine", StringComparison.OrdinalIgnoreCase));
            }

            return File.Exists("/var/run/docker.sock");
        }
        catch
        {
            return false;
        }
    }
}
