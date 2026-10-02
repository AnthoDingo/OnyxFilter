using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services;
using OnyxFilter.Services.ClientLocation;

namespace OnyxFilter.Components.Pages;

public partial class CountryFilter : ComponentBase, IAsyncDisposable
{
    [Inject]
    public ILocalSettingsStore SettingsStore { get; set; } = default!;

    [Inject]
    public IClientLocationService LocationService { get; set; } = default!;

    [Inject]
    public IJSRuntime JS { get; set; } = default!;

    [Inject]
    public NavigationManager Navigation { get; set; } = default!;

    // Carte interactive (CountryFilter.razor.js) : module chargé au premier rendu, puis état (mode et
    // pays sélectionnés) renvoyé à chaque rendu.
    private ElementReference mapContainer;
    private IJSObjectReference? module;
    private DotNetObjectReference<CountryFilter>? dotNetRef;
    private bool mapReady;

    private CountryFilterMode Mode { get; set; }

    private HashSet<string> SelectedCountries { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    // Pays de la base, complétés des pays déjà sélectionnés (base pas encore chargée).
    private List<string> AllCountries { get; set; } = new List<string>();

    // Noms des pays dans la langue de la page, fournis par le navigateur (Intl.DisplayNames) : .NET ne
    // donne les noms que dans la langue du pays lui-même. Code pays affiché en attendant.
    private Dictionary<string, string> CountryNames { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    private string SearchText { get; set; } = string.Empty;

    private string? StatusMessage { get; set; }

    private bool StatusIsError { get; set; }

    private bool IsSaving { get; set; }

    // Pays filtrés par la recherche (nom ou code), regroupés par continent (ordre de Continents.All) et
    // triés par nom ; les continents sans pays visible sont omis.
    private IEnumerable<(string Continent, List<string> Countries)> VisibleGroups
    {
        get
        {
            string search = SearchText.Trim();

            ILookup<string, string> byContinent = AllCountries
                .Where(code => search.Length == 0
                    || NameOf(code).Contains(search, StringComparison.CurrentCultureIgnoreCase)
                    || code.Equals(search, StringComparison.OrdinalIgnoreCase))
                .OrderBy(NameOf, StringComparer.CurrentCultureIgnoreCase)
                .ToLookup(Continents.Of);

            return Continents.All
                .Where(byContinent.Contains)
                .Select(continent => (continent, byContinent[continent].ToList()));
        }
    }

    private int SelectedCountOf(string continent)
    {
        return SelectedCountries.Count(code => Continents.Of(code) == continent);
    }

    private int TotalCountOf(string continent)
    {
        return AllCountries.Count(code => Continents.Of(code) == continent);
    }

    // Coche ou décoche tous les pays affichés d'un continent (ceux qui correspondent à la recherche).
    private void ToggleAll(IEnumerable<string> countries, bool select)
    {
        foreach (string code in countries)
        {
            if (select)
            {
                SelectedCountries.Add(code);
            }
            else
            {
                SelectedCountries.Remove(code);
            }
        }
    }

    protected override async Task OnInitializedAsync()
    {
        AppLocalSettings settings = await SettingsStore.LoadAsync();
        Mode = settings.CountryFilter.Mode;
        SelectedCountries = new HashSet<string>(settings.CountryFilter.Countries, StringComparer.Ordinal);
        AllCountries = LocationService.Countries.Union(SelectedCountries).ToList();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                module = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/CountryFilter.razor.js");

                if (AllCountries.Count != 0)
                {
                    CountryNames = await module.InvokeAsync<Dictionary<string, string>>("getCountryNames", AllCountries);
                }

                dotNetRef = DotNetObjectReference.Create(this);
                string svgUrl = Navigation.ToAbsoluteUri(Assets["maps/world.svg"]).ToString();
                await module.InvokeVoidAsync("initMap", mapContainer, dotNetRef, svgUrl, new { allowed = L["Autorisé"].Value, blocked = L["Bloqué"].Value });
                mapReady = true;
                StateHasChanged();
            }
            catch (JSException)
            {
                // Navigateur sans Intl.DisplayNames ou carte indisponible : la liste reste utilisable.
            }

            return;
        }

        if (mapReady && module is not null)
        {
            await module.InvokeVoidAsync("setMapState", mapContainer, Mode.ToString(), SelectedCountries);
        }
    }

    // Clic sur un pays de la carte : il change d'état (sélectionné ou non). Un pays absent de la base
    // iptoasn est ajouté à la liste pour rester visible et décochable.
    [JSInvokable]
    public async Task ToggleCountryFromMap(string code)
    {
        if (!SelectedCountries.Remove(code))
        {
            SelectedCountries.Add(code);
        }

        if (!AllCountries.Contains(code))
        {
            AllCountries.Add(code);

            if (module is not null)
            {
                foreach ((string key, string name) in await module.InvokeAsync<Dictionary<string, string>>("getCountryNames", new[] { code }))
                {
                    CountryNames[key] = name;
                }
            }
        }

        await InvokeAsync(StateHasChanged);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (module is not null)
            {
                await module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // Circuit déjà fermé : rien à libérer côté navigateur.
        }

        dotNetRef?.Dispose();
    }

    private string NameOf(string code)
    {
        return CountryNames.TryGetValue(code, out string? name) ? name : code;
    }

    private static string FlagOf(string code)
    {
        return new ClientLocation(code, 0, string.Empty, string.Empty).Flag;
    }

    private void ToggleCountry(string code, ChangeEventArgs e)
    {
        if (e.Value is bool isChecked && isChecked)
        {
            SelectedCountries.Add(code);
        }
        else
        {
            SelectedCountries.Remove(code);
        }
    }

    private async Task SaveAsync()
    {
        if (Mode == CountryFilterMode.Allowlist && SelectedCountries.Count == 0)
        {
            StatusIsError = true;
            StatusMessage = L["Sélectionnez au moins un pays à autoriser."];
            return;
        }

        CountryFilterMode mode = Mode;
        List<string> countries = SelectedCountries.OrderBy(code => code, StringComparer.Ordinal).ToList();
        IsSaving = true;

        try
        {
            await SettingsStore.UpdateAsync(settings => settings.CountryFilter = new CountryFilterSettingsData
            {
                Mode = mode,
                Countries = countries,
            });

            StatusIsError = false;
            StatusMessage = L["Filtrage par pays enregistré."];
        }
        catch (Exception ex)
        {
            StatusIsError = true;
            StatusMessage = L["Erreur lors de l'enregistrement : {0}", ex.Message];
        }
        finally
        {
            IsSaving = false;
        }
    }
}
