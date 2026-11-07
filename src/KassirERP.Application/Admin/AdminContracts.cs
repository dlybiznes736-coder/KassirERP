using System.Text.RegularExpressions;
using KassirERP.Application.Abstractions;

namespace KassirERP.Application.Admin;

// Настройки фирмы через API вместо SQL (партия C): доступ сотрудников к
// филиалам (user_stores) и весовые префиксы (weight_barcode_prefixes).

/// <summary>Stores — явный список доступа (вход по PIN только в эти филиалы; пусто — PIN не пускает никуда).</summary>
public sealed record UserView(Guid Id, string Login, string FullName, string Role, Guid? StoreId, bool IsActive, IReadOnlyList<Guid> Stores);

public sealed record SetUserStores(IReadOnlyList<Guid> StoreIds);

/// <summary>Prefixes пуст — весовыми считаются все префиксы 20–29.</summary>
public sealed record WeightPrefixesView(IReadOnlyList<string> Prefixes, bool AllPrefixes);

public sealed record SetWeightPrefixes(IReadOnlyList<string> Prefixes);

public enum AdminStatus
{
    Done,
    NotFound,
    Invalid,
}

public sealed record AdminResult(AdminStatus Status, string? Detail = null, object? Body = null);

public interface IAdminStore
{
    Task<IReadOnlyList<UserView>> UsersAsync(CancellationToken ct);
    Task<UserView?> UserAsync(Guid userId, CancellationToken ct);
    Task<AdminResult> SetUserStoresAsync(Guid userId, IReadOnlyList<Guid> storeIds, CancellationToken ct);
    Task<IReadOnlyList<string>> WeightPrefixesAsync(CancellationToken ct);
    Task<AdminResult> SetWeightPrefixesAsync(IReadOnlyList<string> prefixes, CancellationToken ct);
}

public sealed class AdminValidationException(string message) : AppValidationException(message);

public sealed partial class AdminService(IAdminStore store)
{
    public Task<IReadOnlyList<UserView>> UsersAsync(CancellationToken ct) => store.UsersAsync(ct);

    public Task<UserView?> UserAsync(Guid userId, CancellationToken ct) => store.UserAsync(userId, ct);

    public Task<AdminResult> SetUserStoresAsync(Guid userId, SetUserStores request, CancellationToken ct)
    {
        var ids = request.StoreIds ?? throw new AdminValidationException("Нужен список storeIds (пустой — доступа по PIN нет).");
        if (ids.Count > 500 || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Count)
        {
            throw new AdminValidationException("storeIds: до 500 разных филиалов.");
        }

        return store.SetUserStoresAsync(userId, ids, ct);
    }

    public async Task<WeightPrefixesView> WeightPrefixesAsync(CancellationToken ct)
    {
        var prefixes = await store.WeightPrefixesAsync(ct);
        return new WeightPrefixesView(prefixes, prefixes.Count == 0);
    }

    public Task<AdminResult> SetWeightPrefixesAsync(SetWeightPrefixes request, CancellationToken ct)
    {
        var prefixes = request.Prefixes ?? throw new AdminValidationException("Нужен список prefixes (пустой — все 20–29).");
        if (prefixes.Any(p => p is null || !PrefixPattern().IsMatch(p)) || prefixes.Distinct().Count() != prefixes.Count)
        {
            throw new AdminValidationException("Префикс весового штрихкода — две цифры от 20 до 29, без повторов.");
        }

        return store.SetWeightPrefixesAsync(prefixes.Order(StringComparer.Ordinal).ToList(), ct);
    }

    [GeneratedRegex("^2[0-9]$")]
    private static partial Regex PrefixPattern();
}
