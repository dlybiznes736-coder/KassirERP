using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

/// <summary>
/// Синхронизация кассы через API (шаг 6), на той же стендовой кассе, что и
/// SalesBench (не регистрирует новых касс). Касса «отработала без связи»:
/// каждая пачка — открытие смены, N чеков (пара обуви + весовая колбаса,
/// наличные + карта), закрытие смены. Меряется POST /sync/push целиком.
/// Затем та же пачка повторно (все чеки — duplicate), затем забор изменений:
/// весь каталог с нуля страницами по 5 000 и опрос «что нового» по курсору.
/// Нужен стенд генератора (db/bench/_bench_stand.sql).
/// </summary>
internal static class SyncBench
{
    private const string StandDevice = "b0000000-0000-0000-0000-00000000be05";
    private const string StandDeviceSecret = "bench-device-secret-1";
    private const string Cashier = "cashier1_1";
    private const string CashierPin = "1234";
    private static readonly Guid Kolbasa = Guid.Parse("b0000000-0000-0000-0000-00000000be03");

    public static async Task RunAsync(string api, int batchSize, int batches, string outPath)
    {
        using var http = new HttpClient { BaseAddress = new Uri(api), Timeout = TimeSpan.FromMinutes(5) };

        var pin = new HttpRequestMessage(HttpMethod.Post, "/auth/pin") { Content = JsonContent.Create(new { login = Cashier, pin = CashierPin }) };
        pin.Headers.Authorization = new AuthenticationHeaderValue("Device", $"{StandDevice}.{StandDeviceSecret}");
        var token = (await Json(await http.SendAsync(pin))).GetProperty("accessToken").GetString()!;

        var state = await Json(await Send(http, HttpMethod.Get, "/devices/me", token, null));
        if (state.GetProperty("openShift").ValueKind == JsonValueKind.Object)
        {
            await Json(await Send(http, HttpMethod.Post, $"/shifts/{state.GetProperty("openShift").GetProperty("id").GetGuid()}/close", token, new { closingCash = 0m }));
        }

        var number = state.GetProperty("lastLocalNumber").GetInt32();
        var firstNumber = number + 1;
        var random = new Random(Environment.TickCount);
        var warmupSize = 20;
        var shoes = new List<(Guid Variant, decimal Price)>();
        while (shoes.Count < warmupSize + batchSize * batches)
        {
            var barcode = "46" + random.Next(1, 500_001).ToString("D11", CultureInfo.InvariantCulture);
            var scan = await Send(http, HttpMethod.Get, $"/products/barcode/{barcode}", token, null);
            if (scan.IsSuccessStatusCode)
            {
                var body = await Json(scan);
                shoes.Add((body.GetProperty("variantId").GetGuid(), body.GetProperty("salePrice").GetDecimal()));
            }
        }

        object Batch(int size, int offset)
        {
            var shiftId = Guid.NewGuid();
            var sales = new List<object>(size);
            for (var i = 0; i < size; i++)
            {
                var kg = random.Next(150, 2500) / 1000m;
                var shoe = shoes[offset + i];
                var total = shoe.Price + decimal.Round(520m * kg, 2, MidpointRounding.AwayFromZero);
                var cash = decimal.Round(total / 3, 2);
                sales.Add(new
                {
                    id = Guid.NewGuid(), shiftId, localNumber = ++number, createdAtLocal = DateTimeOffset.UtcNow,
                    items = new object[]
                    {
                        new { id = Guid.NewGuid(), variantId = shoe.Variant, qty = 1m, price = shoe.Price },
                        new { id = Guid.NewGuid(), variantId = Kolbasa, qty = kg, price = 520m },
                    },
                    payments = new[] { new { id = Guid.NewGuid(), method = "cash", amount = cash }, new { id = Guid.NewGuid(), method = "card", amount = total - cash } },
                });
            }

            return new
            {
                shifts = new[] { new { id = shiftId, openingCash = 0m } },
                sales,
                closes = new[] { new { shiftId, closingCash = 0m } },
            };
        }

        // Разогрев: маленькая пачка, не в замере.
        await Push(http, token, Batch(warmupSize, 0), warmupSize + 2);

        var rows = new List<string>();
        object? last = null;
        for (var b = 0; b < batches; b++)
        {
            last = Batch(batchSize, warmupSize + b * batchSize);
            var (ms, result) = await Push(http, token, last, batchSize + 2);
            rows.Add(string.Create(CultureInfo.InvariantCulture,
                $"| Пачка {b + 1}: {batchSize} чеков + смена | {ms:0} мс | {ms / batchSize:0.00} мс | {result.GetProperty("accepted").GetInt32()} | {result.GetProperty("duplicates").GetInt32()} | {result.GetProperty("rejected").GetInt32()} |"));
        }

        var (replayMs, replay) = await Push(http, token, last!, 0);
        if (replay.GetProperty("duplicates").GetInt32() != batchSize + 2)
        {
            throw new InvalidOperationException("Повтор пачки: ожидались только duplicate. " + replay);
        }

        rows.Add(string.Create(CultureInfo.InvariantCulture,
            $"| Повтор последней пачки | {replayMs:0} мс | {replayMs / batchSize:0.00} мс | {replay.GetProperty("accepted").GetInt32()} | {replay.GetProperty("duplicates").GetInt32()} | {replay.GetProperty("rejected").GetInt32()} |"));

        // Забор изменений: весь каталог с нуля и опрос «что нового».
        var (variantPages, variantRows, variantMs, variantCursor) = await Drain(http, token, "/sync/variants", null);
        var (productPages, productRows, productMs, _) = await Drain(http, token, "/sync/products", null);
        var pollTimes = new List<double>();
        for (var i = 0; i < 50; i++)
        {
            var started = Stopwatch.GetTimestamp();
            await Json(await Send(http, HttpMethod.Get, "/sync/variants?after=" + Uri.EscapeDataString(variantCursor), token, null));
            pollTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        pollTimes.Sort();

        var md = new StringBuilder();
        md.AppendLine("# Kassir ERP — синхронизация кассы через API");
        md.AppendLine();
        md.AppendLine(
            string.Create(CultureInfo.InvariantCulture, $"Дата: {DateTime.Now:yyyy-MM-dd HH:mm} · API {api} · стендовая касса (одна при каждом запуске), чеки {firstNumber}–{number}. ") +
            "Чек: пара обуви (шт) + весовая колбаса (кг), наличные + карта. Время — POST /sync/push от отправки до последнего байта ответа; " +
            "каждый документ — своя транзакция с COMMIT (fsync).");
        md.AppendLine();
        md.AppendLine("| Отправка | Всего | На чек | accepted | duplicate | rejected |");
        md.AppendLine("|---|--:|--:|--:|--:|--:|");
        rows.ForEach(r => md.AppendLine(r));
        md.AppendLine();
        md.AppendLine("| Забор изменений | Строк | Страниц | Время |");
        md.AppendLine("|---|--:|--:|--:|");
        md.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| Пары с нуля (по 5 000) | {variantRows} | {variantPages} | {variantMs:0} мс |"));
        md.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| Товары с нуля (по 5 000) | {productRows} | {productPages} | {productMs:0} мс |"));
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"| Опрос «что нового» по курсору, 50 раз | 0 | 1 | p50 {pollTimes[pollTimes.Count / 2]:0.00} мс, макс {pollTimes[^1]:0.00} мс |"));
        await File.WriteAllTextAsync(outPath, md.ToString());
        Console.WriteLine(md);
    }

    private static async Task<(double Ms, JsonElement Result)> Push(HttpClient http, string token, object batch, int expectAccepted)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await Json(await Send(http, HttpMethod.Post, "/sync/push", token, batch));
        var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (expectAccepted > 0 && result.GetProperty("accepted").GetInt32() != expectAccepted)
        {
            throw new InvalidOperationException("Пачка принята не целиком: " + result);
        }

        return (ms, result);
    }

    private static async Task<(int Pages, int Rows, double Ms, string Cursor)> Drain(HttpClient http, string token, string url, string? after)
    {
        var started = Stopwatch.GetTimestamp();
        var pages = 0;
        var rows = 0;
        var cursor = after;
        while (true)
        {
            var page = await Json(await Send(http, HttpMethod.Get,
                url + "?limit=5000" + (cursor is null ? "" : "&after=" + Uri.EscapeDataString(cursor)), token, null));
            pages++;
            rows += page.GetProperty("items").GetArrayLength();
            cursor = page.GetProperty("next").GetString();
            if (!page.GetProperty("hasMore").GetBoolean())
            {
                break;
            }
        }

        return (pages, rows, Stopwatch.GetElapsedTime(started).TotalMilliseconds, cursor!);
    }

    private static Task<HttpResponseMessage> Send(HttpClient http, HttpMethod method, string url, string token, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{(int)response.StatusCode} {response.RequestMessage?.RequestUri}: {text}");
        }

        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }
}
