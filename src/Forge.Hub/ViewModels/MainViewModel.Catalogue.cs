using Avalonia.Threading;
using Forge.Core;
using Forge.Core.Catalogue;
using Forge.Hub.ViewModels.Pages;

namespace Forge.Hub.ViewModels;

/// <summary>
/// The catalogue from the website (12.5, slice 5): fetched when the window opens and with every update check
/// (the watcher's rhythm), written to the cache by <see cref="CatalogueClient"/>, and taken when it is newer than
/// the copy in use. The open pages are rebuilt from the new copy, so a product added on the site shows up in
/// Browse, search, its track and its own page without a Hub release. Offline or failing, nothing changes.
/// </summary>
public partial class MainViewModel
{
    private static readonly HttpClient CatalogueHttp = CreateCatalogueHttp();
    private bool _catalogueSwapPending;

    private static HttpClient CreateCatalogueHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent("hub-catalogue"));
        return http;
    }

    /// <summary>
    /// False when a developer pinned the catalogue to a file (FORGE_CATALOGUE) or turned the fetch off
    /// (FORGE_CATALOGUE_URL=off): the screenshot harness and fixed-data checks see only what they were given.
    /// </summary>
    public static bool CatalogueFetchEnabled =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FORGE_CATALOGUE"))
        && !string.Equals(Environment.GetEnvironmentVariable("FORGE_CATALOGUE_URL"), "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>One fetch; a newer copy is taken on the UI thread. Never throws.</summary>
    public async Task FetchCatalogueAsync()
    {
        if (!CatalogueFetchEnabled) return;
        var client = new CatalogueClient(CatalogueHttp);
        var result = await client.FetchAsync().ConfigureAwait(false);
        if (result == CatalogueFetch.Updated) Dispatcher.UIThread.Post(TakeNewerCatalogue);
        else if (result == CatalogueFetch.Ignored && client.LastMessage is { } why) Say(why);
    }

    /// <summary>The cache changed (this window's fetch, or the watcher's): read again, and use the copy if it is newer.</summary>
    public void TakeNewerCatalogue()
    {
        var fresh = CatalogueLoader.Load();
        if (fresh is null) return;
        if (Catalogue is not null && !CatalogueLoader.IsNewer(fresh.GeneratedAt, Catalogue.GeneratedAt)) return;
        Catalogue = fresh;
        Details.Add($"{DateTime.Now:HH:mm:ss}  The list of tools was updated from {fresh.Source} ({fresh.GeneratedAt}).");
        if (Busy) { _catalogueSwapPending = true; return; }
        RebuildPagesForCatalogue();
    }

    partial void OnBusyChanged(bool value)
    {
        if (value || !_catalogueSwapPending) return;
        _catalogueSwapPending = false;
        Dispatcher.UIThread.Post(RebuildPagesForCatalogue);
    }

    /// <summary>
    /// Every page holds the catalogue's objects it was opened with, so each is opened again from the new copy:
    /// the one in front and the ones behind it. A track or product the new copy no longer has falls back to Home.
    /// </summary>
    private void RebuildPagesForCatalogue()
    {
        var behind = _back.Reverse().Select(Reopen).ToList();
        _back.Clear();
        foreach (var page in behind) _back.Push(page);
        var current = CurrentPage is null ? null : Reopen(CurrentPage);
        if (ReferenceEquals(current, CurrentPage)) current?.Refresh();
        else CurrentPage = current;
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(BackLabel));
        RecountPill();
        OpenFromEnvironment();
    }

    private bool _openedFromEnvironment;

    /// <summary>
    /// FORGE_HUB_OPEN=browse | library | components | track:&lt;slug&gt; | product:&lt;slug&gt;: open that page as soon as the catalogue has it, once.
    /// For checking a development build (with FORGE_HUB_SNAPSHOT); the door from the web (kovatihub:, slice 6)
    /// will open pages the same way. A slug only a fetched catalogue has opens when that copy arrives.
    /// </summary>
    internal void OpenFromEnvironment()
    {
        if (_openedFromEnvironment || Catalogue is not { } cat) return;
        if (Environment.GetEnvironmentVariable("FORGE_HUB_OPEN") is not { Length: > 0 } open) { _openedFromEnvironment = true; return; }
        var (kind, slug) = open.IndexOf(':') is var i and > 0 ? (open[..i], open[(i + 1)..]) : (open, "");
        switch (kind)
        {
            case "browse": ShowBrowse(); break;
            case "library": ShowLibrary(); break;
            case "components": ShowComponents(); break;
            case "track" when cat.Track(slug) is not null: OpenTrack(slug); break;
            case "product" when cat.Product(slug) is not null: OpenProduct(slug); break;
            default: return; // not in this copy (yet)
        }
        _openedFromEnvironment = true;
    }

    private PageViewModel Reopen(PageViewModel page)
    {
        var cat = Catalogue!;
        return page switch
        {
            TrackPageViewModel t => cat.Track(t.Track.Slug) is { } track ? new TrackPageViewModel(this, track) : new HomePageViewModel(this),
            ProductPageViewModel p => cat.Product(p.Product.Slug) is { } product ? new ProductPageViewModel(this, product) : new HomePageViewModel(this),
            HomePageViewModel => new HomePageViewModel(this),
            BrowsePageViewModel => new BrowsePageViewModel(this),
            // Library, Components and Runners read the catalogue each time they refresh.
            _ => page,
        };
    }

    private DispatcherTimer? _mediaArrivedTimer;

    /// <summary>A picture that was not on this machine arrived: the page in front looks again, once for a burst of them.</summary>
    internal void OnMediaArrived()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _mediaArrivedTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
            {
                _mediaArrivedTimer!.Stop();
                CurrentPage?.Refresh();
            });
            _mediaArrivedTimer.Stop();
            _mediaArrivedTimer.Start();
        });
    }
}
