using Npgsql;

namespace KassirERP.Infrastructure.Persistence;

/// <summary>
/// Пул соединений, видимый только внутри Infrastructure. NpgsqlDataSource
/// намеренно не регистрируется в DI сам по себе: код Api и Application не может
/// открыть соединение в обход <see cref="TenantDb"/> (данные тенанта) и
/// AuthStore (функции входа).
/// </summary>
internal sealed class KassirDataSource(NpgsqlDataSource inner) : IAsyncDisposable
{
    public NpgsqlDataSource Inner { get; } = inner;

    public ValueTask DisposeAsync() => Inner.DisposeAsync();
}
