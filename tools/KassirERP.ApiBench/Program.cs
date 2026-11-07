// =====================================================================
// Kassir ERP — сколько времени добавляет бэкенд поверх базы (шаг 3).
//
// Один и тот же запрос (скан штрихкода, поиск по названию) меряется на трёх
// уровнях, последовательно, одним клиентом:
//   1. Postgres:  EXPLAIN (ANALYZE, TIMING OFF) — планирование + выполнение,
//                 как в db/bench/bench.sql;
//   2. Npgsql:    тот же SQL напрямую с хоста (set_config + запрос одним
//                 пакетом, как в TenantDb) — база + сеть до Docker + драйвер;
//   3. API:       HTTP GET с JWT до последнего байта ответа.
// Плюс две базовые линии без базы: /health/live (только HTTP) и /auth/me
// (HTTP + проверка JWT). «Бэкенд добавил» = API − Npgsql.
//
// Нужна база после generate.sql: фирма «bench» и пользователь «bench» есть
// только там (их создаёт генератор, миграции — нет). Утилита в базу не пишет.
//
// Запуск (API уже работает в Release, см. db/bench/API_REPORT.md):
//   dotnet run -c Release --project tools/KassirERP.ApiBench -- --out db/bench/API_REPORT.md
//   ... -- --app-cs "Host=...;Database=kassir_x3;..." --out x3.md   (другая база)
// =====================================================================
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;

var api = Arg("--api", "http://localhost:5260");
var appCs = Arg("--app-cs", "Host=localhost;Port=15432;Database=kassir;Username=kassir_app;Password=kassir_app_dev");
var outPath = Arg("--out", "API_REPORT.md");
var scans = int.Parse(Arg("--scans", "2000"), CultureInfo.InvariantCulture);
var searches = int.Parse(Arg("--searches", "300"), CultureInfo.InvariantCulture);
const int Warmup = 200;

// Стенд из generate.sql.
const string BenchTenantCode = "bench";
const string BenchLogin = "bench";
const string BenchPassword = "bench-password-1";

using var http = new HttpClient { BaseAddress = new Uri(api) };
var login = await http.PostAsJsonAsync("/auth/login", new { tenantCode = BenchTenantCode, login = BenchLogin, password = BenchPassword });
if (!login.IsSuccessStatusCode)
{
    Console.Error.WriteLine($"Вход bench/bench не удался ({(int)login.StatusCode}). Стенд создаёт только db/bench/generate.sql.");
    return;
}

var session = await login.Content.ReadFromJsonAsync<JsonElement>();
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
var tenant = session.GetProperty("tenantId").GetGuid();
var store = session.GetProperty("storeId").GetGuid();

await using var dataSource = NpgsqlDataSource.Create(appCs);
int variantCount;
int productCount;
await using (var connection = await dataSource.OpenConnectionAsync())
await using (var batch = new NpgsqlBatch(connection))
{
    // Под RLS, как приложение: видно только свою фирму.
    batch.BatchCommands.Add(new NpgsqlBatchCommand("SELECT set_config('app.tenant_id', $1, true)") { Parameters = { new() { Value = tenant.ToString() } } });
    batch.BatchCommands.Add(new NpgsqlBatchCommand("SELECT (SELECT count(*) FROM product_variants WHERE barcode LIKE '46%'), (SELECT count(*) FROM products)"));
    await using var reader = await batch.ExecuteReaderAsync();
    await reader.NextResultAsync();
    await reader.ReadAsync();
    variantCount = (int)reader.GetInt64(0);
    productCount = (int)reader.GetInt64(1);
}

// Свой набор случайных пар у каждого уровня: иначе первый уровень прогревает
// кэш Postgres для второго.
string[] RandomBarcodes(int seed)
{
    var random = new Random(seed);
    return Enumerable.Range(0, Warmup + scans).Select(_ => "46" + random.Next(1, variantCount + 1).ToString("D11", CultureInfo.InvariantCulture)).ToArray();
}

// Новый seed на каждый запуск: предыдущий прогон не должен оставить один набор тёплым.
var seed = Environment.TickCount;
var barcodesDb = RandomBarcodes(seed);
var barcodesApi = RandomBarcodes(seed + 1);
var hotBarcode = barcodesDb[0];
string[] queries = ["кроссовки runner", "кроссовки", "run"];

// SQL — тот же, что в ProductCatalog / bench.sql.
const string ScanSql = """
    SELECT v.id, p.id, p.name, p.brand, p.article, v.size, v.color, v.barcode, v.unit, v.plu, v.sale_price,
           coalesce(s.qty, 0), v.barcode IS DISTINCT FROM $1 AS by_plu
    FROM product_variants v
    JOIN products p ON p.id = v.product_id
    LEFT JOIN stock s ON s.tenant_id = v.tenant_id AND s.store_id = $2 AND s.variant_id = v.id
    WHERE v.barcode = $1 AND v.deleted_at IS NULL AND p.deleted_at IS NULL
    """;
const string SearchSql = """
    SELECT p.id, p.name, p.brand, p.article, v.id, v.size, v.color, v.barcode, v.sale_price
    FROM catalog.search_products($1, $2) p
    JOIN product_variants v ON v.product_id = p.id AND v.deleted_at IS NULL
    ORDER BY p.name, p.id, v.size, v.color
    """;

var rows = new List<Row>();

Console.WriteLine("== Базовые линии");
var (live, me) = await MeasurePair(Warmup + scans, _ => HttpGet("/health/live"), _ => HttpGet("/auth/me"));

Console.WriteLine("== Скан штрихкода");
// Одна и та же пара — как в bench.sql: страницы в кэше, видна чистая цена слоёв.
var (hotDb, hotApi) = await MeasurePair(Warmup + scans,
    _ => DbRead(ScanSql, hotBarcode, store), _ => HttpGet($"/products/barcode/{hotBarcode}"));
rows.Add(new Row("Скан: одна и та же пара (как в bench.sql)", 0.200,
    await ExplainMedian(ScanSql, [Lit(hotBarcode), Lit(store)], 50, _ => [Lit(hotBarcode), Lit(store)]), hotDb, hotApi));

// Случайные пары из всех: так работает реальная касса.
var (coldDb, coldApi) = await MeasurePair(Warmup + scans,
    i => DbRead(ScanSql, barcodesDb[i], store), i => HttpGet($"/products/barcode/{barcodesApi[i]}"));
var explainBarcodes = RandomBarcodes(seed + 2);
rows.Add(new Row($"Скан: случайные пары из {variantCount:N0}", null,
    await ExplainMedian(ScanSql, [Lit(explainBarcodes[0]), Lit(store)], 100, i => [Lit(explainBarcodes[i]), Lit(store)]), coldDb, coldApi));

for (var q = 0; q < queries.Length; q++)
{
    var query = queries[q];
    Console.WriteLine($"== Поиск «{query}»");
    var (searchDb, searchApi) = await MeasurePair(Warmup / 4 + searches,
        _ => DbRead(SearchSql, query, 50), _ => HttpGet($"/products/search?q={Uri.EscapeDataString(query)}"));
    rows.Add(new Row($"Поиск «{query}» (до 50 товаров со всеми парами)", null,
        await ExplainMedian(SearchSql, [Lit(query), "50"], 9, _ => [Lit(query), "50"]), searchDb, searchApi));
}

// --- Отчёт -----------------------------------------------------------------
var md = new StringBuilder();
md.AppendLine("# Kassir ERP — время ответа API поверх базы");
md.AppendLine();
md.AppendLine(CultureInfo.InvariantCulture, $"Дата: {DateTime.Now:yyyy-MM-dd HH:mm} · API {api} (Release, Production) · один клиент, запросы подряд · " +
              $"товаров у фирмы: {productCount:N0}, пар: {variantCount:N0} · скан: {scans} случайных пар, поиск: {searches} повторов на запрос, перед каждой серией разогрев.");
md.AppendLine();
md.AppendLine("Уровни меряются вперемешку (запрос к Npgsql, запрос к API, и наоборот), у каждого свой набор случайных пар — чтобы один не прогревал кэш другому.");
md.AppendLine();
md.AppendLine("Как читать: **Postgres** — `EXPLAIN ANALYZE` (планирование + выполнение) тем же методом, что в `bench.sql`; " +
              "**Npgsql** — тот же SQL напрямую с хоста одним пакетом с `set_config`, как в `TenantDb` (база + сеть до Docker + драйвер); " +
              "**API** — HTTP-запрос с JWT до последнего байта ответа. **Бэкенд добавил = API − Npgsql** (медианы).");
md.AppendLine();
md.AppendLine("| Запрос | REPORT.md (шаг 4), Postgres | Postgres сейчас | Npgsql p50 | API p50 | API p95 | API p99 | Бэкенд добавил (p50) | Бэкенд добавил (p95) |");
md.AppendLine("|---|--:|--:|--:|--:|--:|--:|--:|--:|");
foreach (var r in rows)
{
    md.AppendLine(CultureInfo.InvariantCulture,
        $"| {r.Name} | {(r.Reference is { } refMs ? refMs.ToString("0.000", CultureInfo.InvariantCulture) + " мс" : "—")} | {r.Explain:0.000} мс | {r.Db.P50:0.00} мс | {r.Api.P50:0.00} мс | {r.Api.P95:0.00} мс | {r.Api.P99:0.00} мс | " +
        $"{r.Api.P50 - r.Db.P50:+0.00;-0.00} мс | {r.Api.P95 - r.Db.P95:+0.00;-0.00} мс |");
}

md.AppendLine();
md.AppendLine("### Подробно");
md.AppendLine();
md.AppendLine("| Запрос | Уровень | Запросов | Среднее | p50 | p95 | p99 | Макс |");
md.AppendLine("|---|---|--:|--:|--:|--:|--:|--:|");
md.AppendLine(Line("Без базы", "HTTP: `/health/live`", live));
md.AppendLine(Line("Без базы", "HTTP + проверка JWT: `/auth/me`", me));
foreach (var r in rows)
{
    md.AppendLine(Line(r.Name, "Npgsql напрямую", r.Db));
    md.AppendLine(Line(r.Name, "API", r.Api));
}

await File.WriteAllTextAsync(outPath, md.ToString());
Console.WriteLine(md);
return;

// --- Помощники ---------------------------------------------------------------
string Arg(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

static string Lit(object value) => "'" + value.ToString()!.Replace("'", "''") + "'";

static string Line(string name, string level, Stats s) => string.Create(CultureInfo.InvariantCulture,
    $"| {name} | {level} | {s.Count} | {s.Mean:0.00} мс | {s.P50:0.00} мс | {s.P95:0.00} мс | {s.P99:0.00} мс | {s.Max:0.00} мс |");

async Task HttpGet(string url)
{
    using var response = await http.GetAsync(url, HttpCompletionOption.ResponseContentRead);
    response.EnsureSuccessStatusCode();
    await response.Content.ReadAsByteArrayAsync();
}

async Task DbRead(string sql, object p1, object p2)
{
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var batch = new NpgsqlBatch(connection);
    batch.BatchCommands.Add(new NpgsqlBatchCommand("SELECT set_config('app.tenant_id', $1, true)") { Parameters = { new() { Value = tenant.ToString() } } });
    batch.BatchCommands.Add(new NpgsqlBatchCommand(sql) { Parameters = { new() { Value = p1 }, new() { Value = p2 } } });
    await using var reader = await batch.ExecuteReaderAsync();
    await reader.NextResultAsync();
    while (await reader.ReadAsync())
    {
        for (var c = 0; c < reader.FieldCount; c++)
        {
            reader.GetValue(c);
        }
    }
}

// Медиана (планирование + выполнение) по EXPLAIN — как bench.run в bench.sql.
async Task<double> ExplainMedian(string sql, string[] first, int runs, Func<int, string[]> literals)
{
    var times = new List<double>();
    await using var connection = await dataSource.OpenConnectionAsync();
    for (var i = 0; i <= runs; i++)
    {
        var lits = i == 0 ? first : literals(i);
        var text = sql.Replace("$2", lits[1]).Replace("$1", lits[0]);
        await using var tx = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', $1, true)", connection, tx))
        {
            set.Parameters.Add(new NpgsqlParameter { Value = tenant.ToString() });
            await set.ExecuteNonQueryAsync();
        }

        await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, TIMING OFF, FORMAT JSON) " + text, connection, tx);
        var json = JsonDocument.Parse((string)(await explain.ExecuteScalarAsync())!).RootElement[0];
        if (i > 0)
        {
            times.Add(json.GetProperty("Planning Time").GetDouble() + json.GetProperty("Execution Time").GetDouble());
        }

        await tx.RollbackAsync();
    }

    times.Sort();
    return times[times.Count / 2];
}

// Два уровня вперемешку: на чётных шагах первым идёт A, на нечётных — B.
// Так фон машины, прогрев JIT и кэша делятся между уровнями поровну.
static async Task<(Stats A, Stats B)> MeasurePair(int total, Func<int, Task> a, Func<int, Task> b)
{
    var warm = Math.Min(Warmup, total / 5);
    var samplesA = new List<double>(total);
    var samplesB = new List<double>(total);
    for (var i = 0; i < total; i++)
    {
        var (first, second, firstSamples, secondSamples) = i % 2 == 0 ? (a, b, samplesA, samplesB) : (b, a, samplesB, samplesA);
        var t1 = await Time(first, i);
        var t2 = await Time(second, i);
        if (i >= warm)
        {
            firstSamples.Add(t1);
            secondSamples.Add(t2);
        }
    }

    return (ToStats(samplesA), ToStats(samplesB));

    static async Task<double> Time(Func<int, Task> action, int i)
    {
        var started = Stopwatch.GetTimestamp();
        await action(i);
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
}

static Stats ToStats(List<double> samples)
{
    samples.Sort();
    double P(double q) => samples[Math.Min(samples.Count - 1, (int)Math.Ceiling(q * samples.Count) - 1)];
    return new Stats(samples.Count, samples.Average(), P(0.50), P(0.95), P(0.99), samples[^1]);
}


internal sealed record Stats(int Count, double Mean, double P50, double P95, double P99, double Max);

internal sealed record Row(string Name, double? Reference, double Explain, Stats Db, Stats Api);
