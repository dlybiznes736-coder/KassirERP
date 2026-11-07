using KassirERP.Application.Auth;
using Npgsql;

namespace KassirERP.Api.Tests.Infrastructure;

public sealed record TestUser(Guid Id, string Login, string? Password, string? Pin, Guid? StoreId, string Role);

public sealed record TestTenant(Guid Id, string Code, Guid StoreId, Guid ProductId, Guid VariantId, string Barcode, string ProductName, decimal Stock,
    IReadOnlyDictionary<string, TestUser> Users);

/// <summary>Две фирмы. У каждой — филиал, сотрудники, товар с парой и остатком.</summary>
public sealed class TestData
{
    public required string Suffix { get; init; }
    public required TestTenant A { get; init; }
    public required TestTenant B { get; init; }

    public static TestData Create()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return new TestData { Suffix = suffix, A = Tenant("a", suffix, stock: 7), B = Tenant("b", suffix, stock: 3) };
    }

    private static TestTenant Tenant(string letter, string suffix, decimal stock)
    {
        var store = Guid.NewGuid();
        var users = new Dictionary<string, TestUser>
        {
            // Владелец: пароль, без привязки к филиалу.
            ["owner"] = new(Guid.NewGuid(), "owner", $"Owner-{letter}-pass-1", null, null, "owner"),
            // Кладовщик: пароль, филиал.
            ["keeper"] = new(Guid.NewGuid(), "keeper", $"Keeper-{letter}-pass-1", null, store, "storekeeper"),
            // Кассир: PIN, филиал.
            ["cashier"] = new(Guid.NewGuid(), "cashier", null, "4821", store, "cashier"),
            // Кассир для теста блокировки — его PIN намеренно перебирают.
            ["locked"] = new(Guid.NewGuid(), "cashier-lock", null, "1357", store, "cashier"),
            // Кассир без филиала: PIN-вход ему запрещён.
            ["nostore"] = new(Guid.NewGuid(), "cashier-nostore", null, "2468", null, "cashier"),
        };

        return new TestTenant(Guid.NewGuid(), $"test-{letter}-{suffix}", store, Guid.NewGuid(), Guid.NewGuid(),
            $"T{letter.ToUpperInvariant()}{suffix}", $"Изоляция {suffix} товар {letter.ToUpperInvariant()}", stock, users);
    }

    public async Task InsertAsync(NpgsqlConnection admin, IPasswordHasher hasher)
    {
        foreach (var t in new[] { A, B })
        {
            await Exec(admin, "INSERT INTO tenants (id, name, code, tariff) VALUES ($1, $2, $3, 'online')", t.Id, $"Тест {t.Code}", t.Code);
            await Exec(admin, "INSERT INTO stores (id, tenant_id, name) VALUES ($1, $2, 'Тестовый филиал')", t.StoreId, t.Id);
            foreach (var u in t.Users.Values)
            {
                await Exec(admin,
                    "INSERT INTO users (id, tenant_id, store_id, full_name, login, role, password_hash, pin_hash) VALUES ($1, $2, $3, $4, $5, $6, $7, $8)",
                    u.Id, t.Id, (object?)u.StoreId ?? DBNull.Value, u.Login, u.Login, u.Role,
                    u.Password is null ? DBNull.Value : hasher.Hash(u.Password),
                    u.Pin is null ? DBNull.Value : hasher.Hash(u.Pin));
            }

            await Exec(admin, "INSERT INTO products (id, tenant_id, name, brand) VALUES ($1, $2, $3, 'Тест')", t.ProductId, t.Id, t.ProductName);
            await Exec(admin,
                "INSERT INTO product_variants (id, tenant_id, product_id, size, color, barcode, cost_price, sale_price) VALUES ($1, $2, $3, '42', 'чёрный', $4, 2500, 4990)",
                t.VariantId, t.Id, t.ProductId, t.Barcode);
            // Остаток — штатно, через журнал: триггер сдвинет stock.
            await Exec(admin,
                "INSERT INTO stock_movements (id, tenant_id, store_id, variant_id, qty, reason, occurred_at) VALUES ($1, $2, $3, $4, $5, 'adjustment', now())",
                Guid.NewGuid(), t.Id, t.StoreId, t.VariantId, t.Stock);
        }
    }

    public async Task DeleteAsync(NpgsqlConnection admin)
    {
        // replica: триггеры (запрет UPDATE/DELETE журнала, FK) не срабатывают — порядок не важен.
        await Exec(admin, "SET session_replication_role = replica");
        var ids = new[] { A.Id, B.Id };
        foreach (var table in new[]
                 {
                     "stock", "stock_movements", "payments", "sale_items", "sales", "cash_shifts", "purchase_items", "purchases",
                     "product_photos", "product_variants", "products", "categories", "customers", "suppliers", "sync_outbox",
                     "auth.login_attempts", "users", "stores",
                 })
        {
            await Exec(admin, $"DELETE FROM {table} WHERE tenant_id = ANY($1)", ids);
        }

        await Exec(admin, "DELETE FROM tenants WHERE id = ANY($1)", ids);
        await Exec(admin, "SET session_replication_role = origin");
    }

    private static async Task Exec(NpgsqlConnection connection, string sql, params object[] args)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var arg in args)
        {
            command.Parameters.Add(arg is DBNull
                ? new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Unknown }
                : new NpgsqlParameter { Value = arg });
        }

        await command.ExecuteNonQueryAsync();
    }
}
