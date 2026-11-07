using KassirERP.Application.Auth;
using KassirERP.Application.Devices;
using Npgsql;

namespace KassirERP.Api.Tests.Infrastructure;

/// <param name="AccessStores">Явный список филиалов (user_stores); null — только свой StoreId, если он есть.</param>
public sealed record TestUser(Guid Id, string Login, string? Password, string? Pin, Guid? StoreId, string Role, IReadOnlyList<Guid>? AccessStores = null)
{
    public IReadOnlyList<Guid> Stores => AccessStores ?? (StoreId is { } s ? [s] : []);
}

/// <summary>Касса с известным секретом (в базе — только его хеш, как после регистрации).</summary>
public sealed record TestDevice(Guid Id, string Secret, Guid StoreId);

public sealed record TestTenant(
    Guid Id, string Code, Guid StoreId, Guid Store2Id, Guid Store3Id,
    Guid ProductId, Guid VariantId, string Barcode, string ProductName, decimal Stock,
    IReadOnlyDictionary<string, TestUser> Users,
    IReadOnlyDictionary<string, TestDevice> Devices,
    Guid WeightedVariantId, string Plu, decimal PricePerKg, decimal WeightedStock,
    Guid ExpiryVariantId)
{
    /// <summary>Касса для входа пользователя по PIN: в его филиале (первом из доступных) или в филиале 1.</summary>
    public TestDevice DeviceFor(TestUser user) =>
        Devices.Values.First(d => d.StoreId == (user.StoreId ?? user.Stores.FirstOrDefault(StoreId)));
}

/// <summary>
/// Две фирмы. У каждой — два филиала с кассами, сотрудники, обувная пара с
/// остатком, весовой товар с PLU и товар с учётом сроков годности.
/// </summary>
public sealed class TestData
{
    public required string Suffix { get; init; }
    public required TestTenant A { get; init; }
    public required TestTenant B { get; init; }

    /// <param name="extraCashiers">Дополнительные кассиры фирмы A, у каждого своя касса: cashier-01 / kassa-01…, PIN 4821.</param>
    public static TestData Create(int extraCashiers = 0)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return new TestData
        {
            Suffix = suffix,
            A = Tenant("a", suffix, stock: 7, plu: "01234", extraCashiers),
            B = Tenant("b", suffix, stock: 3, plu: "05555", 0),
        };
    }

    private static TestTenant Tenant(string letter, string suffix, decimal stock, string plu, int extraCashiers)
    {
        var store = Guid.NewGuid();
        var store2 = Guid.NewGuid();
        var store3 = Guid.NewGuid();
        var users = new Dictionary<string, TestUser>
        {
            // Владелец: пароль, без привязки к филиалу.
            ["owner"] = new(Guid.NewGuid(), "owner", $"Owner-{letter}-pass-1", null, null, "owner"),
            // Кладовщик: пароль, филиал 1.
            ["keeper"] = new(Guid.NewGuid(), "keeper", $"Keeper-{letter}-pass-1", null, store, "storekeeper"),
            // Менеджер филиала 1: подключает кассы.
            ["manager"] = new(Guid.NewGuid(), "manager", $"Manager-{letter}-pass-1", "9090", store, "manager"),
            // Кассир: PIN, филиал 1.
            ["cashier"] = new(Guid.NewGuid(), "cashier", null, "4821", store, "cashier"),
            // Кассир, чей логин есть только в этой фирме.
            ["own"] = new(Guid.NewGuid(), $"cashier-{letter}", null, "7777", store, "cashier"),
            // Кассир для теста блокировки — его PIN намеренно перебирают.
            ["locked"] = new(Guid.NewGuid(), "cashier-lock", null, "1357", store, "cashier"),
            // Кассир без филиалов: вход по PIN запрещён (доступ — только явный список).
            ["nostore"] = new(Guid.NewGuid(), "cashier-nostore", null, "2468", null, "cashier"),
            // Кассир с доступом к филиалам 1 и 2 (не к 3).
            ["multi"] = new(Guid.NewGuid(), "cashier-multi", null, "3579", null, "cashier", [store, store2]),
        };
        var devices = new Dictionary<string, TestDevice>
        {
            ["kassa1"] = new(Guid.NewGuid(), DeviceSecrets.NewSecret(), store),
            ["kassa2"] = new(Guid.NewGuid(), DeviceSecrets.NewSecret(), store2),
            ["kassa3"] = new(Guid.NewGuid(), DeviceSecrets.NewSecret(), store3),
        };
        for (var i = 1; i <= extraCashiers; i++)
        {
            users[$"cashier-{i:D2}"] = new(Guid.NewGuid(), $"cashier-{i:D2}", null, "4821", store, "cashier");
            devices[$"kassa-{i:D2}"] = new(Guid.NewGuid(), DeviceSecrets.NewSecret(), store);
        }

        return new TestTenant(Guid.NewGuid(), $"test-{letter}-{suffix}", store, store2, store3,
            Guid.NewGuid(), Guid.NewGuid(), $"T{letter.ToUpperInvariant()}{suffix}", $"Изоляция {suffix} товар {letter.ToUpperInvariant()}", stock,
            users, devices,
            Guid.NewGuid(), plu, PricePerKg: 520.00m, WeightedStock: 12.5m,
            Guid.NewGuid());
    }

    public async Task InsertAsync(NpgsqlConnection admin, IPasswordHasher hasher)
    {
        foreach (var t in new[] { A, B })
        {
            await Exec(admin, "INSERT INTO tenants (id, name, code, tariff) VALUES ($1, $2, $3, 'online')", t.Id, $"Тест {t.Code}", t.Code);
            await Exec(admin,
                "INSERT INTO stores (id, tenant_id, name) VALUES ($1, $2, 'Тестовый филиал 1'), ($3, $2, 'Тестовый филиал 2'), ($4, $2, 'Тестовый филиал 3')",
                t.StoreId, t.Id, t.Store2Id, t.Store3Id);
            foreach (var u in t.Users.Values)
            {
                await Exec(admin,
                    "INSERT INTO users (id, tenant_id, store_id, full_name, login, role, password_hash, pin_hash) VALUES ($1, $2, $3, $4, $5, $6, $7, $8)",
                    u.Id, t.Id, (object?)u.StoreId ?? DBNull.Value, u.Login, u.Login, u.Role,
                    u.Password is null ? DBNull.Value : hasher.Hash(u.Password),
                    u.Pin is null ? DBNull.Value : hasher.Hash(u.Pin));
                foreach (var accessStore in u.Stores)
                {
                    await Exec(admin, "INSERT INTO user_stores (tenant_id, user_id, store_id) VALUES ($1, $2, $3)", t.Id, u.Id, accessStore);
                }
            }

            foreach (var (name, d) in t.Devices)
            {
                await Exec(admin, "INSERT INTO devices (id, tenant_id, store_id, name, secret_hash) VALUES ($1, $2, $3, $4, $5)",
                    d.Id, t.Id, d.StoreId, name, DeviceSecrets.Hash(d.Secret));
            }

            // Обувь: пара с размером и цветом, остаток через журнал.
            await Exec(admin, "INSERT INTO products (id, tenant_id, name, brand) VALUES ($1, $2, $3, 'Тест')", t.ProductId, t.Id, t.ProductName);
            await Exec(admin,
                "INSERT INTO product_variants (id, tenant_id, product_id, size, color, barcode, cost_price, sale_price) VALUES ($1, $2, $3, '42', 'чёрный', $4, 2500, 4990)",
                t.VariantId, t.Id, t.ProductId, t.Barcode);
            await Movement(admin, t, t.VariantId, t.Stock);

            // Весовой товар: без размера и цвета, unit = kg, PLU.
            var weightedProduct = Guid.NewGuid();
            await Exec(admin, "INSERT INTO products (id, tenant_id, name) VALUES ($1, $2, $3)", weightedProduct, t.Id, $"Колбаса весовая {t.Code}");
            await Exec(admin,
                "INSERT INTO product_variants (id, tenant_id, product_id, unit, plu, cost_price, sale_price) VALUES ($1, $2, $3, 'kg', $4, 300, $5)",
                t.WeightedVariantId, t.Id, weightedProduct, t.Plu, t.PricePerKg);
            await Movement(admin, t, t.WeightedVariantId, t.WeightedStock);

            // Товар с учётом сроков годности, остатка нет.
            var expiryProduct = Guid.NewGuid();
            await Exec(admin, "INSERT INTO products (id, tenant_id, name) VALUES ($1, $2, $3)", expiryProduct, t.Id, $"Молоко {t.Code}");
            await Exec(admin,
                "INSERT INTO product_variants (id, tenant_id, product_id, cost_price, sale_price, tracks_expiry) VALUES ($1, $2, $3, 60, 90, true)",
                t.ExpiryVariantId, t.Id, expiryProduct);
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
                     "transfer_items", "transfers", "price_changes", "price_list_items", "price_lists",
                     "product_photos", "product_variants", "products", "categories", "customers", "suppliers", "sync_outbox",
                     "weight_barcode_prefixes", "device_enrollments", "devices", "auth.login_attempts", "user_stores", "users", "stores",
                 })
        {
            await Exec(admin, $"DELETE FROM {table} WHERE tenant_id = ANY($1)", ids);
        }

        await Exec(admin, "DELETE FROM tenants WHERE id = ANY($1)", ids);
        await Exec(admin, "SET session_replication_role = origin");
    }

    private static Task Movement(NpgsqlConnection admin, TestTenant t, Guid variant, decimal qty) =>
        Exec(admin,
            "INSERT INTO stock_movements (id, tenant_id, store_id, variant_id, qty, reason, occurred_at) VALUES ($1, $2, $3, $4, $5, 'adjustment', now())",
            Guid.NewGuid(), t.Id, t.StoreId, variant, qty);

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
