using System.Diagnostics;

namespace Forge.Core.Machine;

/// <summary>What Docker itself is doing, before any container is considered.</summary>
public enum DockerState
{
    /// <summary>No docker command on PATH.</summary>
    NotInstalled,

    /// <summary>Installed, but the daemon is not answering — usually Docker Desktop is not open.</summary>
    NotRunning,

    /// <summary>The daemon answers.</summary>
    Running,
}

/// <summary>A container belonging to a declared runner, if there is one.</summary>
public enum ContainerState
{
    /// <summary>Not asked, or Docker is not there to ask.</summary>
    Unknown,

    /// <summary>No container for this runner on this machine yet.</summary>
    Missing,

    /// <summary>It exists and is not running. Starting it is enough.</summary>
    Stopped,

    /// <summary>It is running. Says nothing about whether the thing inside it is ready.</summary>
    Running,
}

/// <summary>
/// Docker, from outside Unreal.
///
/// **Deliberately does not use `docker compose`.** A compose file interpolates variables the plugin
/// supplies from its own settings — a model cache directory, a token, a signature — and this
/// application has no way to know them. Asking compose would either fail on a missing variable or,
/// worse, appear to succeed while meaning something different from what the editor meant.
///
/// So containers are found by the labels compose itself stamps on them, and started and stopped by
/// id. That is enough for the thing somebody actually wants from the hub: the runner started before
/// the editor is open. **Creating** a container is left to the editor, which knows the paths.
/// </summary>
public static class DockerCli
{
    public static DockerState Probe()
    {
        if (!Run("--version", out _)) return DockerState.NotInstalled;

        // --version answers from the client alone; only `info` proves the daemon is up. And its
        // output has to be read, because a daemon that is still starting exits zero and prints a
        // 500 where the version belongs.
        if (!Run("info --format {{.ServerVersion}}", out var info) || LooksLikeError(info))
        {
            return DockerState.NotRunning;
        }

        return DockerState.Running;
    }

    public static string Version() => Run("--version", out var v) ? v.Trim() : "";

    /// <summary>
    /// The state of the container compose created for this project and service.
    ///
    /// Two facts, not one: `docker ps` without `--all` lists only what runs, so asking twice is how
    /// "there is no container" is told apart from "there is one and it is stopped". They want
    /// different buttons and different sentences.
    /// </summary>
    public static ContainerState Container(string project, string service, out string containerId)
    {
        containerId = "";

        var filter = $"--filter label=com.docker.compose.project={project}";
        if (!string.IsNullOrWhiteSpace(service))
        {
            filter += $" --filter label=com.docker.compose.service={service}";
        }

        if (!Run($"ps --all --quiet {filter}", out var all)) return ContainerState.Unknown;

        var id = FirstLine(all);
        if (string.IsNullOrEmpty(id)) return ContainerState.Missing;

        containerId = id;

        if (!Run($"ps --quiet {filter}", out var running)) return ContainerState.Unknown;

        return string.IsNullOrEmpty(FirstLine(running)) ? ContainerState.Stopped : ContainerState.Running;
    }

    /// <summary>Start an existing container. Nothing is built, nothing is created.</summary>
    public static bool Start(string containerId, out string error) => Act("start", containerId, out error);

    /// <summary>Stop it and leave it there, so starting it again costs seconds.</summary>
    public static bool Stop(string containerId, out string error) => Act("stop", containerId, out error);

    private static bool Act(string verb, string containerId, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(containerId))
        {
            error = "There is no container to act on.";
            return false;
        }

        if (Run($"{verb} {containerId}", out var output)) return true;

        error = string.IsNullOrWhiteSpace(output) ? $"docker {verb} failed." : output.Trim();
        return false;
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => !LooksLikeError(l)) ?? "";

    private static bool LooksLikeError(string text) =>
        text.Contains("Internal Server Error", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase)
        || text.Contains("error during connect", StringComparison.OrdinalIgnoreCase);

    private static bool Run(string arguments, out string output)
    {
        output = "";

        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null) return false;

            output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

            // Docker answers quickly or not at all. A minute is generous for `ps` and short enough
            // that a wedged daemon does not hang the interface.
            if (!process.WaitForExit(60_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            // No docker on PATH is the common case and is not exceptional.
            return false;
        }
    }
}
