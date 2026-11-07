using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

/// <summary>
/// Время проведения чека через API (шаг 5). Всё — через API, как касса:
/// менеджер стенда выдаёт код, касса регистрируется, кассир входит по PIN,
/// открывает смену; затем чеки (пара обуви + весовая колбаса, наличные + карта)
/// и возвраты (часть колбасы). Меряется только POST /sales: пары подобраны
/// сканом заранее. Один клиент, запросы подряд. В конце смена закрывается.
/// Нужен стенд генератора (db/bench/_bench_stand.sql).
/// </summary>
internal static class SalesBench
{
    private const string Manager = "bench";
    private const string ManagerPassword = "bench-password-1";
    private const string Cashier = "cashier1_1";
    private const string CashierPin = "1234";
    private static readonly Guid Kolbasa = Guid.Parse("b0000000-0000-0000-0000-00000000be03");

    public static async Task RunAsync(string api, int sales, int returns, string outPath)
    {
        using var http = new HttpClient { BaseAddress = new Uri(api) };

        var manager = await Json(await http.PostAsJsonAsync("/auth/login", new { tenantCode = "bench", login = Manager, password = ManagerPassword }));
        var code = await Json(await Send(http, HttpMethod.Post, "/devices/enrollment-codes", manager.GetProperty("accessToken").GetString()!, new { }));
        var device = await Json(await http.PostAsJsonAsync("/devices/enroll", new { code = code.GetProperty("code").GetString(), name = "Замер чеков" }));
        var pin = new HttpRequestMessage(HttpMethod.Post, "/auth/pin") { Content = JsonContent.Create(new { login = Cashier, pin = CashierPin }) };
        pin.Headers.Authorization = new AuthenticationHeaderValue("Device", $"{device.GetProperty("deviceId")}.{device.GetProperty("deviceSecret").GetString()}");
        var token = (await Json(await http.SendAsync(pin))).GetProperty("accessToken").GetString()!;
        var shiftId = Guid.NewGuid();
        await Json(await Send(http, HttpMethod.Post, "/shifts", token, new { id = shiftId, openingCash = 0m }));

        // Пары для чеков: случайные штрихкоды генератора, цена — из скана.
        var random = new Random(Environment.TickCount);
        var warmup = Math.Min(50, sales / 5);
        var shoes = new List<(Guid Variant, decimal Price)>();
        while (shoes.Count < sales + warmup)
        {
            var barcode = "46" + random.Next(1, 500_001).ToString("D11", CultureInfo.InvariantCulture);
            var scan = await Send(http, HttpMethod.Get, $"/products/barcode/{barcode}", token, null);
            if (scan.IsSuccessStatusCode)
            {
                var body = await Json(scan);
                shoes.Add((body.GetProperty("variantId").GetGuid(), body.GetProperty("salePrice").GetDecimal()));
            }
        }

        var number = 0;
        var saleTimes = new List<double>();
        var posted = new List<(Guid Sale, Guid KolbasaLine, decimal Kg)>();
        for (var i = 0; i < sales + warmup; i++)
        {
            var kg = random.Next(150, 2500) / 1000m;
            var kolbasaLine = Guid.NewGuid();
            var shoe = shoes[i];
            var total = shoe.Price + decimal.Round(520m * kg, 2, MidpointRounding.AwayFromZero);
            var cash = decimal.Round(total / 3, 2);
            var sale = new
            {
                id = Guid.NewGuid(), shiftId, localNumber = ++number, createdAtLocal = DateTimeOffset.UtcNow,
                items = new object[]
                {
                    new { id = Guid.NewGuid(), variantId = shoe.Variant, qty = 1m, price = shoe.Price },
                    new { id = kolbasaLine, variantId = Kolbasa, qty = kg, price = 520m },
                },
                payments = new[] { new { id = Guid.NewGuid(), method = "cash", amount = cash }, new { id = Guid.NewGuid(), method = "card", amount = total - cash } },
            };

            var started = Stopwatch.GetTimestamp();
            var response = await Send(http, HttpMethod.Post, "/sales", token, sale);
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            await Json(response);
            if (i >= warmup)
            {
                saleTimes.Add(ms);
                posted.Add((sale.id, kolbasaLine, kg));
            }
        }

        var returnTimes = new List<double>();
        foreach (var (saleId, line, kg) in posted.Take(returns))
        {
            var back = decimal.Round(kg / 2, 3);
            var refund = decimal.Round(520m * back, 2, MidpointRounding.AwayFromZero);
            var ret = new
            {
                id = Guid.NewGuid(), shiftId, localNumber = ++number, createdAtLocal = DateTimeOffset.UtcNow,
                type = "return", returnOfSaleId = saleId,
                items = new[] { new { id = Guid.NewGuid(), qty = back, returnOfItemId = line } },
                payments = new[] { new { id = Guid.NewGuid(), method = "cash", amount = refund } },
            };

            var started = Stopwatch.GetTimestamp();
            var response = await Send(http, HttpMethod.Post, "/sales", token, ret);
            returnTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            await Json(response);
        }

        await Json(await Send(http, HttpMethod.Post, $"/shifts/{shiftId}/close", token, new { closingCash = 0m }));

        var md = new StringBuilder();
        md.AppendLine("# Kassir ERP — время проведения чека через API");
        md.AppendLine();
        md.AppendLine(
            string.Create(CultureInfo.InvariantCulture, $"Дата: {DateTime.Now:yyyy-MM-dd HH:mm} · API {api} · один клиент, запросы подряд, разогрев {warmup} чеков. ") +
            "Чек: пара обуви (шт) + весовая колбаса (кг), оплата наличными и картой. Возврат: половина колбасы из чека, наличными. " +
            "Время — от отправки POST /sales до последнего байта ответа (проверки, запись чека, оплат и строк, триггер склада, COMMIT с fsync).");
        md.AppendLine();
        md.AppendLine("| Операция | Запросов | Среднее | p50 | p95 | p99 | Макс |");
        md.AppendLine("|---|--:|--:|--:|--:|--:|--:|");
        md.AppendLine(Row("Чек: 2 строки, 2 оплаты", saleTimes));
        md.AppendLine(Row("Возврат: 1 строка", returnTimes));
        await File.WriteAllTextAsync(outPath, md.ToString());
        Console.WriteLine(md);
    }

    private static string Row(string name, List<double> samples)
    {
        samples.Sort();
        double P(double q) => samples[Math.Min(samples.Count - 1, (int)Math.Ceiling(q * samples.Count) - 1)];
        return string.Create(CultureInfo.InvariantCulture,
            $"| {name} | {samples.Count} | {samples.Average():0.00} мс | {P(0.50):0.00} мс | {P(0.95):0.00} мс | {P(0.99):0.00} мс | {samples[^1]:0.00} мс |");
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
