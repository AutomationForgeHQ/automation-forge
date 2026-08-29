using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Forge.Core.Machine;

/// <summary>A rented machine, as Runpod reports it.</summary>
public sealed record Pod(string Id, string Name, string State, string Gpu, double HourlyPrice, string? PublicUrl)
{
    public bool IsRunning => State.Equals("RUNNING", StringComparison.OrdinalIgnoreCase);

    /// <summary>Billing at the card's full rate happens whenever it exists and runs.</summary>
    public bool IsBilling => IsRunning;
}

/// <summary>A network volume: where the weights live, and the cost that continues with no pod.</summary>
public sealed record Volume(string Id, string Name, int SizeGb, string DataCenterId)
{
    /// <summary>Runpod charges about $0.07 per GB per month for network storage.</summary>
    public double MonthlyCost => SizeGb * 0.07;
}

/// <summary>
/// Runpod, from the hub.
///
/// **Reads and releases; it does not rent.** Renting well means walking a list of cards that have
/// stock in the volume's region, under a price ceiling, retrying past host-level refusals — logic
/// that exists in the Kimodo plugin and was repaired there today. A second copy in this application
/// would be a second copy of the code that spends money, and the two would drift the way any two
/// implementations of one thing do. Provisioning stays in the editor until that walk can be shared.
///
/// What the hub does offer is the half somebody actually wants from a tray application: seeing that
/// a pod is running and billing, and releasing it without opening Unreal.
/// </summary>
public sealed class RunpodClient(HttpClient http)
{
    private const string ApiRoot = "https://api.runpod.io/v2";

    /// <summary>Every pod on the account. An empty list and a failure are different answers.</summary>
    public async Task<(bool Ok, IReadOnlyList<Pod> Pods, string Error)> ListPodsAsync(string apiKey)
    {
        var (ok, json, error) = await SendAsync(HttpMethod.Get, "/pods", apiKey, null);
        if (!ok) return (false, [], error);

        try
        {
            using var doc = JsonDocument.Parse(json);

            // Runpod wraps collections in a key named after the resource, and has used more than
            // one over time. Trying the three costs nothing and beats trusting a summary.
            if (!TryArray(doc.RootElement, out var items, "pods", "items", "data"))
            {
                return (true, [], "");
            }

            var pods = new List<Pod>();
            foreach (var item in items.EnumerateArray())
            {
                pods.Add(new Pod(
                    Id: Str(item, "id"),
                    Name: Str(item, "name"),
                    State: Str(item, "desiredStatus", "status", "state"),
                    Gpu: Str(item, "machineType", "gpuTypeId", "gpuType"),
                    HourlyPrice: Num(item, "costPerHr", "costPerHour"),
                    PublicUrl: null));
            }

            return (true, pods, "");
        }
        catch (Exception ex)
        {
            return (false, [], ex.Message);
        }
    }

    /// <summary>Every network volume. A volume with no pod is the quiet cost nobody notices.</summary>
    public async Task<(bool Ok, IReadOnlyList<Volume> Volumes, string Error)> ListVolumesAsync(string apiKey)
    {
        var (ok, json, error) = await SendAsync(HttpMethod.Get, "/network-volumes", apiKey, null);
        if (!ok) return (false, [], error);

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!TryArray(doc.RootElement, out var items, "networkVolumes", "network_volumes", "items", "data"))
            {
                return (true, [], "");
            }

            var volumes = items.EnumerateArray()
                .Select(v => new Volume(Str(v, "id"), Str(v, "name"), (int)Num(v, "size"), Str(v, "dataCenterId")))
                .ToList();

            return (true, volumes, "");
        }
        catch (Exception ex)
        {
            return (false, [], ex.Message);
        }
    }

    /// <summary>
    /// Release the pod. The GPU stops billing entirely; the volume keeps its weights and its cost.
    ///
    /// Terminate rather than pause, deliberately, and this matches what the plugin does: a paused
    /// pod holds a host's GPU slot and cannot be resumed once that host fills up, which strands the
    /// disk attached to it. Everything expensive to rebuild is on the network volume instead.
    /// </summary>
    public async Task<(bool Ok, string Error)> ReleaseAsync(string apiKey, string podId)
    {
        var body = JsonSerializer.Serialize(new { action = "terminate" });
        var (ok, _, error) = await SendAsync(HttpMethod.Post, $"/pods/{podId}/action", apiKey, body);

        // 409 means it is already in the state being asked for, which is a success as far as
        // anybody who pressed "release it" is concerned.
        if (!ok && error.Contains("409")) return (true, "");

        return (ok, error);
    }

    private async Task<(bool Ok, string Json, string Error)> SendAsync(
        HttpMethod method, string path, string apiKey, string? body)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return (false, "", "No Runpod API key is stored. Set one in Settings.");
        }

        try
        {
            using var request = new HttpRequestMessage(method, ApiRoot + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            using var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return (false, "", $"Runpod said {(int)response.StatusCode}: {Trim(text)}");
            }

            return (true, text, "");
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }

    private static string Trim(string text) =>
        text.Length <= 200 ? text.Trim() : text[..200].Trim() + "…";

    private static bool TryArray(JsonElement root, out JsonElement array, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(name, out var found)
                && found.ValueKind == JsonValueKind.Array)
            {
                array = found;
                return true;
            }
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
            return true;
        }

        array = default;
        return false;
    }

    private static string Str(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("id", out var id))
                {
                    return id.GetString() ?? "";
                }
            }
        }
        return "";
    }

    private static double Num(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
            {
                return value.GetDouble();
            }
        }
        return 0;
    }
}
