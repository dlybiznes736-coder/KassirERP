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

    string Role { get; }
}
