# Intelligent Bartender & Smart Shopping Assistant API

## Opis Projektu
Nowoczesna aplikacja backendowa napisana w języku C#, działająca jako wirtualny asystent barmański. Architektura łączy klasyczne REST API (Minimal API) z asynchronicznymi procesami analitycznymi w tle (Background Services). System zarządza wirtualnym barkiem użytkownika, weryfikuje dostępność składników i zderza je na żywo z potężną zewnętrzną bazą przepisów (TheCocktailDB).

Kluczowym elementem systemu jest analityczny algorytm rekomendacyjny, który cyklicznie skanuje ulubione przepisy użytkownika i generuje zoptymalizowane listy zakupów, wskazując "Złoty Składnik" maksymalizujący możliwości barmańskie.

## Technologie i Architektura
* **Środowisko:** .NET Core / Minimal API
* **Baza Danych:** SQLite + Entity Framework Core (Code-First)
* **Zarządzanie połączeniami:** `IHttpClientFactory` (optymalizacja komunikacji z zewnętrznym REST API)
* **Zadania w tle:** Wzorzec `IHostedService` / `BackgroundService`
* **Bezpieczeństwo:** Niestandardowy Middleware (`IEndpointFilter`) realizujący autoryzację poprzez `X-Api-Key`
* **Analiza danych:** LINQ, zaawansowane operacje na zbiorach (Except, Intersect), agregacja za pomocą słowników (Dictionary)

## Główne Funkcjonalności

### 1. Zarządzanie Lokalnym Magazynem (Entity Framework)
Utrzymywanie spójnego stanu posiadanego inwentarza w relacyjnej bazie danych SQLite. System zapewnia trwałość danych i automatycznie normalizuje wejście (obsługa wielkości liter).

### 2. Live Sync z zewnętrznym API
Aplikacja nie powiela pełnych baz przepisów u siebie. Działa jako Proxy, pobierając definicje drinków w czasie rzeczywistym z TheCocktailDB i dynamicznie porównując je z bazą SQLite użytkownika. Zapewnia to natychmiastową informację o brakujących i posiadanych elementach dla konkretnego przepisu.

### 3. Moduł Inspiracji (Zabezpieczenie logiki biznesowej)
Endpoint eksploracyjny, który rygorystycznie weryfikuje zasoby użytkownika. Zapytanie o rekomendacje na bazie konkretnego alkoholu powiedzie się tylko wtedy, gdy użytkownik faktycznie posiada go w swoim wirtualnym barku.

### 4. Analityczny Worker (Złoty Składnik)
Proces działający w tle, asynchronicznie skanujący tabelę `SavedDrinks`.
* **Smart Polling:** Cykliczne (co 25 sekund) odpytywanie zewnętrznego dostawcy o aktualne wymagania dla zapisanych przepisów.
* **Agregacja braków:** Obliczanie deficytów względem wirtualnego barku i budowa macierzy częstotliwości brakujących elementów.
* **Algorytm rekomendacyjny:** Wyróżnianie jednego "Złotego Składnika", którego zakup odblokuje największą liczbę nowych przepisów, oszczędzając budżet użytkownika.

## Dokumentacja API (Endpoints)

### Wirtualny Barek
* **GET /api/inventory** - Pobiera pełną listę posiadanych składników [Wymaga klucza]
* **POST /api/inventory** - Dodaje nową pozycję do magazynu [Wymaga klucza]
  * Model JSON: `{ "name": "nazwa_skladnika" }`

### Integracja i Eksploracja TheCocktailDB
* **GET /api/explore/cocktails/{name}** - Bezpośrednie zapytanie proxy do zew. API. Zwraca surową strukturę JSON. **[Otwarte dla testów / Nie wymaga klucza]**
* **GET /api/drinks/inspirations/{ingredient}** - Pobiera listę przepisów bazujących na wskazanym składniku (wymaga weryfikacji stanu magazynowego) [Wymaga klucza]
* **GET /api/drinks/can-i-make/{cocktailName}** - Zderza przepis z barkiem. Zwraca flagę możliwości wykonania, zbiór części wspólnych oraz listę deficytów [Wymaga klucza]

### Ulubione Przepisy (Dla Workera)
* **POST /api/drinks/save** - Zapisuje referencję do przepisu, aktywując dla niego analitykę w tle [Wymaga klucza]
  * Model JSON: `{ "externalId": "11000", "name": "Mojito", "instructions": "Opcjonalny opis", "personalRating": 5 }`

## Autoryzacja i Testowanie (Klucz API)

Większość endpointów w aplikacji jest zabezpieczona autorskim filtrem (`ApiKeyFilter`). Zabezpiecza to bazę danych przed nieautoryzowanym dostępem.

Aby móc testować API za pomocą narzędzi takich jak wbudowany plik `.http`, Thunder Client, Postman czy curl, należy do każdego wysyłanego zapytania HTTP dołączyć odpowiedni nagłówek.

**Wymagany nagłówek:**
* **Klucz (Key):** `X-Api-Key`
* **Wartość (Value):** *Wartość klucza znajduje się w pliku `appsettings.json` pod właściwością "X-Api-Key".*

*(W środowisku produkcyjnym klucz ten jest izolowany od kodu źródłowego i przechowywany jako zmienna środowiskowa).*

## Instrukcja Uruchomienia

1. Sklonuj repozytorium i otwórz projekt w terminalu.
2. Upewnij się, że posiadasz zainstalowany pakiet .NET SDK.
3. Wykonaj polecenie: `dotnet run`
4. Aplikacja automatycznie utworzy i przygotuje lokalną bazę `bartender.db`.
5. Serwer zostanie uruchomiony, a proces Asystenta Zakupów zacznie generować logi analityczne bezpośrednio w konsoli. Posiada on również wbudowany interfejs Swagger UI (w trybie Development) do wizualnego przeglądania i testowania endpointów (wymaga podania klucza w sekcji autoryzacji).
