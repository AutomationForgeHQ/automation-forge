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
/// Rented machines, as the hub needs them: what exists, what it costs, and how to stop paying.
///
/// **The split is by where the knowledge is, not by what is easy.** Choosing a machine — which card,
/// in which region, under what price ceiling — is a decision made against what a model needs, and
/// those facts live in the plugin that knows the model. So the editor rents.
///
/// What the hub is for is the other half, and it is the half you want at four in the afternoon: is
/// anything running, what is it costing, and stop it. That needs no model knowledge at all, and it
/// is worth having without opening an engine to get at it.
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

    /// <summary>Free the GPU and keep the machine. Its disk keeps billing at a few cents an hour.</summary>
    public Task<(bool Ok, string Error)> StopAsync(string apiKey, string podId) => ActionAsync(apiKey, podId, "stop");

    /// <summary>Take the same machine back. Nothing is downloaded; the disk was kept.</summary>
    public Task<(bool Ok, string Error)> StartAsync(string apiKey, string podId) => ActionAsync(apiKey, podId, "start");

    /// <summary>
    /// Destroy the pod. GPU and disk both stop billing; a network volume, if there is one, stays.
    ///
    /// This is what the Kimodo plugin does when it is finished with a machine, and for a good
    /// reason: a merely stopped pod holds a host's GPU slot and cannot be resumed once that host
    /// fills up, which strands the disk attached to it. Anything expensive to rebuild lives on the
    /// network volume instead, so destroying the pod costs a couple of minutes, not a download.
    /// </summary>
    public Task<(bool Ok, string Error)> ReleaseAsync(string apiKey, string podId) =>
        ActionAsync(apiKey, podId, "terminate");

    private async Task<(bool Ok, string Error)> ActionAsync(string apiKey, string podId, string action)
    {
        var body = JsonSerializer.Serialize(new { action });
        var (ok, _, error) = await SendAsync(HttpMethod.Post, $"/pods/{podId}/action", apiKey, body);

        // 409 is Runpod saying it is already in the state you asked for, which is a success to
        // anybody who pressed the button.
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
