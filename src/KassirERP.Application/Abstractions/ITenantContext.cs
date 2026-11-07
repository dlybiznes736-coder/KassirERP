namespace KassirERP.Application.Abstractions;

/// <summary>
/// Кто делает запрос. Единственный источник — проверенный JWT
/// (реализация в Api читает только claims, которые прошли проверку подписи).
/// Ни тело, ни заголовки, ни URL сюда не попадают.
/// Обращение без аутентификации — исключение, а не пустой тенант.
/// </summary>
public interface ITenantContext
{
    Guid TenantId { get; }
    Guid UserId { get; }

    /// <summary>Филиал из токена; null — у пользователя доступ ко всем филиалам и филиал не выбран.</summary>
    Guid? StoreId { get; }

    /// <summary>Касса, с которой выполнен вход по PIN; null — вход по паролю.</summary>
    Guid? DeviceId { get; }

    string Role { get; }
}

/// <summary>
/// Отключённые кассы. Их токены отклоняются сразу, не дожидаясь истечения (12 ч).
/// Обновляется из базы раз в 30 с; отключение на этом же экземпляре API — мгновенно.
/// </summary>
public interface IRevokedDevices
{
    bool IsRevoked(Guid deviceId);
    void MarkRevoked(Guid deviceId);
}
