namespace Forge.Core.Catalogue;

/// <summary>
/// The website the Hub takes its catalogue and its pictures from (12.4, 12.5): https://kovati.dev, or
/// FORGE_SITE_URL for local work (http://localhost:3002, the v2 dev server). The catalogue is at
/// /hub/catalogue.json on it (FORGE_CATALOGUE_URL overrides that one address); a picture is the site's own path
/// on the same host. Nothing is fetched from any other host.
/// </summary>
public static class SiteAddress
{
    public const string DefaultBase = "https://kovati.dev";
    public const string CataloguePath = "/hub/catalogue.json";

    /// <summary>The site's base address without a trailing slash; the default when the variable is not an http(s) address.</summary>
    public static string Base => Normalise(Environment.GetEnvironmentVariable("FORGE_SITE_URL")) ?? DefaultBase;

    public static string CatalogueUrl =>
        Normalise(Environment.GetEnvironmentVariable("FORGE_CATALOGUE_URL"), keepPath: true) ?? Base + CataloguePath;

    /// <summary>An absolute http or https address, without a trailing slash, or null.</summary>
    public static string? Normalise(string? url, bool keepPath = false)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
        if (keepPath) return u.ToString();
        return u.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}
