using Microsoft.EntityFrameworkCore;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Konfiguracja bazy danych SQLite
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=bartender.db"));

builder.Services.AddHttpClient();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Rejestrujemy fabrykę klientów HTTP
builder.Services.AddHttpClient();

// Rejestrujemy system asystenta zakupowego jako usługę działającą w tle
builder.Services.AddHostedService<ShoppingAssistantWorker>();

var app = builder.Build();

// Automatyczne tworzenie bazy danych przy starcie
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// --- ENDPOINTY ---

// 1. Zobacz co masz w domowym barku (Wymaga klucza)
app.MapGet("/api/inventory", async (AppDbContext db) =>
{
    var items = await db.MyIngredients.ToListAsync();
    return Results.Ok(items);
}).AddEndpointFilter<ApiKeyFilter>();

// 2. Dodaj nowy składnik do barku (Wymaga klucza)
app.MapPost("/api/inventory", async (MyIngredient input, AppDbContext db) =>
{
    db.MyIngredients.Add(input);
    await db.SaveChangesAsync();
    return Results.Created($"/api/inventory/{input.Id}", input);
}).AddEndpointFilter<ApiKeyFilter>();

// 3. Wyszukaj przepis w zewnętrznym API (BEZ klucza - otwarte do testów)
app.MapGet("/api/explore/cocktails/{name}", async (string name, HttpClient client) =>
{
    var response = await client.GetAsync($"https://www.thecocktaildb.com/api/json/v1/1/search.php?s={name}");
    if (response.IsSuccessStatusCode)
    {
        var content = await response.Content.ReadAsStringAsync();
        return Results.Content(content, "application/json");
    }
    return Results.BadRequest("Nie udało się pobrać danych z zewnętrznego API.");
});

// 4. Zapisz przepis na drinka do bazy (Wymaga klucza)
app.MapPost("/api/drinks/save", async (SavedDrink input, AppDbContext db) =>
{
    db.SavedDrinks.Add(input);
    await db.SaveChangesAsync();
    return Results.Created($"/api/drinks/save/{input.Id}", input);
}).AddEndpointFilter<ApiKeyFilter>();

// 5. INTELIGENTNY BARMAN: Zderzenie bazy zewnętrznej z wewnętrzną (Wymaga klucza)
app.MapGet("/api/drinks/can-i-make/{cocktailName}", async (string cocktailName, HttpClient client, AppDbContext db) =>
{
    var response = await client.GetFromJsonAsync<JsonDocument>($"https://www.thecocktaildb.com/api/json/v1/1/search.php?s={cocktailName}");
    
    // Sprawdzenie czy zewnętrze API coś znalazło
    if (response == null || !response.RootElement.TryGetProperty("drinks", out var drinks) || drinks.ValueKind == JsonValueKind.Null)
    {
        return Results.NotFound("Nie znaleziono takiego drinka.");
    }

    var firstDrink = drinks[0];
    var requiredIngredients = new List<string>();

    // Zewnętrzne API ma składniki zapisane jako strIngredient1, strIngredient2... aż do 15
    for (int i = 1; i <= 15; i++)
    {
        var propName = $"strIngredient{i}";
        if (firstDrink.TryGetProperty(propName, out var ingredientElement) && ingredientElement.ValueKind == JsonValueKind.String)
        {
            var ingredient = ingredientElement.GetString();
            if (!string.IsNullOrWhiteSpace(ingredient))
            {
                requiredIngredients.Add(ingredient.Trim().ToLower()); // Zamieniamy na małe litery do porównania
            }
        }
    }

    // Pobieramy składniki z naszej własnej bazy
    var myIngredients = await db.MyIngredients.Select(x => x.Name.Trim().ToLower()).ToListAsync();
    
    // Algorytm porównujący
    var missingIngredients = requiredIngredients.Except(myIngredients).ToList();
    var ownedIngredients = requiredIngredients.Intersect(myIngredients).ToList();

    return Results.Ok(new
    {
        DrinkName = firstDrink.GetProperty("strDrink").GetString(),
        CanMake = !missingIngredients.Any(),
        OwnedIngredients = ownedIngredients,
        MissingIngredients = missingIngredients
    });
}).AddEndpointFilter<ApiKeyFilter>();

app.MapGet("/api/drinks/inspirations/{ingredient}", async (string ingredient, HttpClient client, AppDbContext db) =>
{
    // 1. Sprawdzamy, czy użytkownik w ogóle ma ten składnik w swoim barku
    var hasIngredient = await db.MyIngredients
        .AnyAsync(i => i.Name.ToLower() == ingredient.ToLower());

    if (!hasIngredient)
    {
        return Results.BadRequest($"Nie możesz szukać inspiracji z '{ingredient}', bo nie masz tego w barku! Dodaj najpierw ten składnik (metodą POST).");
    }

    // 2. Skoro masz składnik, pytamy TheCocktailDB o listę drinków (używamy specjalnego linku do filtrowania)
    var response = await client.GetFromJsonAsync<JsonDocument>($"https://www.thecocktaildb.com/api/json/v1/1/filter.php?i={ingredient}");

    if (response == null || !response.RootElement.TryGetProperty("drinks", out var drinks) || drinks.ValueKind == JsonValueKind.Null)
    {
        return Results.NotFound($"Nie znaleziono żadnych drinków ze składnikiem: {ingredient}.");
    }

    // 3. Pakujemy odpowiedź w ładną, zwięzłą listę
    var inspirations = new List<object>();
    foreach (var drink in drinks.EnumerateArray())
    {
        inspirations.Add(new 
        {
            Name = drink.GetProperty("strDrink").GetString(),
            ExternalId = drink.GetProperty("idDrink").GetString()
        });
    }

    return Results.Ok(new 
    {
        MainIngredient = ingredient,
        TotalFound = inspirations.Count,
        Drinks = inspirations
    });
}).AddEndpointFilter<ApiKeyFilter>();

app.Run();


// --- KLASY I MODELE ---

// Baza danych
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<MyIngredient> MyIngredients { get; set; }
    public DbSet<SavedDrink> SavedDrinks { get; set; }
}


// Model w bazie: Składnik
public class MyIngredient
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

// Model w bazie: Przepis
public class SavedDrink
{
    public int Id { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public int PersonalRating { get; set; }
}

// Ochrona endpointów kluczem API
public class ApiKeyFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expectedKey = config["X-Api-Key"]; // Oczekiwany klucz pobrany z appsettings.json

        // Sprawdzamy czy w nagłówku zapytania jest klucz i czy się zgadza
        if (!context.HttpContext.Request.Headers.TryGetValue("X-Api-Key", out var providedKey) || expectedKey != providedKey)
        {
            return Results.Unauthorized(); // Zwraca błąd 401
        }

        return await next(context);
    }
}

// ==========================================
// PROCES W TLE (Analityczny Asystent Zakupów)
// ==========================================
class ShoppingAssistantWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ShoppingAssistantWorker> _logger;

    public ShoppingAssistantWorker(IServiceProvider serviceProvider, IHttpClientFactory httpClientFactory, ILogger<ShoppingAssistantWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🛒 Analityczny Asystent Zakupów wystartował! Szukam optymalnych zakupów...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Add("User-Agent", "IntelligentBartender/GoldenIngredient");

                var myFavorites = await db.SavedDrinks.ToListAsync(stoppingToken);
                var myInventory = await db.MyIngredients.Select(i => i.Name.ToLower().Trim()).ToListAsync(stoppingToken);

                if (myFavorites.Count > 0)
                {
                    // Słownik zliczający częstotliwość braków! (Zamiast HashSet)
                    var missingIngredientsFrequency = new Dictionary<string, int>();
                    int successfullyProcessedDrinks = 0;
                    var processedDrinkNames = new List<string>();

                    foreach (var favorite in myFavorites)
                    {
                        try 
                        {
                            if (string.IsNullOrWhiteSpace(favorite.ExternalId))
                            {
                                _logger.LogWarning($"⚠️ Drink '{favorite.Name}' nie ma zapisanego ID z bazy TheCocktailDB. Pomijam go.");
                                continue;
                            }

                            var response = await client.GetFromJsonAsync<JsonDocument>($"https://www.thecocktaildb.com/api/json/v1/1/lookup.php?i={favorite.ExternalId}", stoppingToken);

                            if (response != null && response.RootElement.TryGetProperty("drinks", out var drinks) && drinks.ValueKind == JsonValueKind.Array)
                            {
                                var drinkData = drinks[0];
                                var requiredForThisDrink = new List<string>();

                                for (int i = 1; i <= 15; i++)
                                {
                                    var key = $"strIngredient{i}";
                                    if (drinkData.TryGetProperty(key, out var ingEl) && ingEl.ValueKind != JsonValueKind.Null)
                                    {
                                        var name = ingEl.GetString();
                                        if (!string.IsNullOrWhiteSpace(name)) requiredForThisDrink.Add(name.ToLower().Trim());
                                    }
                                }

                                var missing = requiredForThisDrink.Except(myInventory).ToList();
                                
                                // Zliczamy braki
                                foreach (var item in missing)
                                {
                                    if (!missingIngredientsFrequency.ContainsKey(item))
                                    {
                                        missingIngredientsFrequency[item] = 0;
                                    }
                                    missingIngredientsFrequency[item]++; 
                                }
                                
                                successfullyProcessedDrinks++;
                                processedDrinkNames.Add(favorite.Name);
                            }
                        }
                        catch (System.Text.Json.JsonException)
                        {
                            _logger.LogWarning($"⚠️ Zewnętrzne API TheCocktailDB nie zwróciło poprawnych danych dla drinka '{favorite.Name}'.");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"⚠️ Wewnętrzny problem przy sprawdzaniu '{favorite.Name}': {ex.Message}");
                        }
                    }

                    // 4. podsumowanie
                    if (successfullyProcessedDrinks == 0)
                    {
                        _logger.LogInformation("ℹ️ Nie udało się przeanalizować żadnego przepisu.");
                    }
                    else if (missingIngredientsFrequency.Count == 0)
                    {
                        _logger.LogWarning($"✅ JESTEŚ GOTOWY NA WEEKEND! Posiadasz wszystko na {successfullyProcessedDrinks} przeanalizowanych drinków!");
                    }
                    else
                    {
                        // Sortujemy wszystkie braki od najbardziej do najmniej potrzebnych
                        var sortedMissing = missingIngredientsFrequency.OrderByDescending(x => x.Value).ToList();
                        
                        // Złoty składnik to ten, który pojawia się najczęściej w brakach
                        var goldenIngredient = sortedMissing.First();

                        // Pełna lista zakupów
                        var allMissingNames = sortedMissing.Select(x => x.Key);
                        var fullShoppingListStr = string.Join(", ", allMissingNames);
                        var drinkNamesStr = string.Join(", ", processedDrinkNames);

                        if (goldenIngredient.Value > 1)
                        {
                            var nextTop = sortedMissing.Skip(1).Take(2).Select(x => $"{x.Key} (do {x.Value})").ToList();
                            var nextTopStr = nextTop.Count > 0 ? $" Oraz: {string.Join("i ", nextTop)}." : "";

                            _logger.LogWarning($"ZŁOTY SKŁADNIK: Najbardziej opłaca Ci się kupić '{goldenIngredient.Key.ToUpper()}'! Przyda się do {goldenIngredient.Value} z {successfullyProcessedDrinks} drinków.{nextTopStr}");
                            _logger.LogInformation($"🛒 PEŁNA LISTA ZAKUPÓW (aby zrobić: {drinkNamesStr}): {fullShoppingListStr}");
                        }
                        else
                        {
                            // Jeśli wszystkie braki mają wynik równo 1 (żaden składnik się nie powtarza)
                            _logger.LogWarning($"🛒 LISTA ZAKUPÓW: Aby zrobić {drinkNamesStr}, musisz dokupić: {fullShoppingListStr}.");
                            _logger.LogInformation($"💡 (Wszystkie {missingIngredientsFrequency.Count} składników są tak samo potrzebne - żaden się nie powtarza w przepisach).");
                        }
                    }
                }
                else
                {
                    _logger.LogInformation("Nie masz jeszcze żadnych ulubionych drinków. Dodaj je przez API (/api/drinks/save)!");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Błąd Asystenta Zakupów: {ex.Message}");
            }

            // Asystent idzie spać na 25 sekund
            await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken);
        }
    }
}