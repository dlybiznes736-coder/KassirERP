using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

/// <summary>
/// Время отчётов через API (шаг 7). Стендовый менеджер (bench / bench-password-1,
/// филиал 1) запрашивает каждый отчёт за год по своему филиалу; продажи — ещё и
/// по всем филиалам. Каждый отчёт — разогрев 3 раза, затем 20 замеров подряд.
/// Время — от отправки до последнего байта ответа. Данные для отчётов о сроках,
/// расхождениях и поздних возвратах — db/bench/reports_seed.sql.
/// </summary>
internal static class ReportBench
{
    public static async Task RunAsync(string api, string from, string to, string outPath)
    {
        using var http = new HttpClient { BaseAddress = new Uri(api), Timeout = TimeSpan.FromMinutes(2) };
        var login = await Json(await http.PostAsJsonAsync("/auth/login", new { tenantCode = "bench", login = "bench", password = "bench-password-1" }));
        var token = login.GetProperty("accessToken").GetString()!;
        var store = login.GetProperty("storeId").GetGuid();
        var period = $"from={from}&to={to}";

        var reports = new (string Name, string Url)[]
        {
            ("Продажи и прибыль по дням, филиал", $"/reports/sales?{period}&storeId={store}"),
            ("Продажи и прибыль по дням, все филиалы", $"/reports/sales?{period}"),
            ("Расхождения цен, филиал", $"/reports/price-mismatches?{period}&storeId={store}"),
            ("Смены, филиал", $"/reports/shifts?{period}&storeId={store}"),
            ("Смены «изменена после закрытия», филиал", $"/reports/shifts?{period}&storeId={store}&changedOnly=true"),
            ("Поздние возвраты на разбор, филиал", $"/reports/late-returns?{period}&storeId={store}"),
            ("Остатки, страница 500", $"/reports/stock?storeId={store}"),
            ("Истекает за 7 дней, страница 500", $"/reports/expiring?storeId={store}&days=7"),
            ("Истекает за 30 дней, страница 500", $"/reports/expiring?storeId={store}&days=30"),
            ("Истекает за 7 дней + просроченное за 90, страница 500", $"/reports/expiring?storeId={store}&days=7&expired=true"),
            ("Отрицательные остатки, страница 500", $"/reports/negative-stock?storeId={store}"),
        };

        var md = new StringBuilder();
        md.AppendLine("# Kassir ERP — время отчётов через API");
        md.AppendLine();
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Дата: {DateTime.Now:yyyy-MM-dd HH:mm} · API {api} · период {from} … {to} · филиал {store} · разогрев 3, замеров 20, запросы подряд. Цель — 300 мс."));
        md.AppendLine();
        md.AppendLine("| Отчёт | Строк в ответе | p50 | p95 | Макс | ≤ 300 мс |");
        md.AppendLine("|---|--:|--:|--:|--:|:--:|");
        foreach (var (name, url) in reports)
        {
            var rows = 0;
            for (var i = 0; i < 3; i++)
            {
                rows = CountRows(await Json(await Send(http, url, token)));
            }

            var times = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                var started = Stopwatch.GetTimestamp();
                await Json(await Send(http, url, token));
                times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            times.Sort();
            var p95 = times[(int)Math.Ceiling(0.95 * times.Count) - 1];
            md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {name} | {rows} | {times[times.Count / 2]:0.0} мс | {p95:0.0} мс | {times[^1]:0.0} мс | {(p95 <= 300 ? "да" : "**нет**")} |"));
        }

        await File.WriteAllTextAsync(outPath, md.ToString());
        Console.WriteLine(md);
    }

    private static int CountRows(JsonElement body) => body.ValueKind switch
    {
        JsonValueKind.Array => body.GetArrayLength(),
        JsonValueKind.Object when body.TryGetProperty("days", out var days) => days.GetArrayLength(),
        JsonValueKind.Object when body.TryGetProperty("items", out var items) => items.GetArrayLength(),
        _ => 0,
    };

    private static Task<HttpResponseMessage> Send(HttpClient http, string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
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

        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
