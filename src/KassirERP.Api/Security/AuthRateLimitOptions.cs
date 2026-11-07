namespace KassirERP.Api.Security;

/// <summary>
/// Лимит запросов на вход по паролю и регистрацию кассы с одного IP (секция
/// RateLimits:Auth), token bucket.
///
/// Вход по PIN сюда больше не входит: он считается по кассе (PinRateLimitOptions),
/// поэтому 50 касс за одним NAT друг другу не мешают. По IP остаются вход в веб
/// по паролю и обмен кода подключения на секрет кассы.
/// Запас 200 — всплеск: вся смена менеджеров и подключение десятков касс разом;
/// пополнение 60 в минуту. От перебора паролей защищает блокировка учётки
/// (10 попыток на 15 минут), этот лимит — от распыления по многим учёткам.
/// </summary>
public sealed class AuthRateLimitOptions
{
    public const string Section = "RateLimits:Auth";

    /// <summary>Сколько запросов проходит подряд (размер всплеска).</summary>
    public int TokenLimit { get; set; } = 200;

    /// <summary>Сколько запросов в минуту добавляется к запасу.</summary>
    public int TokensPerMinute { get; set; } = 60;
}

/// <summary>
/// Лимит входа по PIN с одной кассы (секция RateLimits:Pin), token bucket.
/// Одна касса — один кассир за раз: вход, до 4 ошибок до блокировки учётки,
/// пересменка — запаса 20 хватает с избытком; 10 в минуту — восстановление.
/// Запрос без заголовка кассы до проверки PIN не доходит (401), такие запросы
/// считаются по IP.
/// </summary>
public sealed class PinRateLimitOptions
{
    public const string Section = "RateLimits:Pin";

    public int TokenLimit { get; set; } = 20;

    public int TokensPerMinute { get; set; } = 10;
}
