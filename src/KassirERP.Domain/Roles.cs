namespace KassirERP.Domain;

/// <summary>Роли сотрудников — значения users.role в базе.</summary>
public static class Roles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Manager = "manager";
    public const string Cashier = "cashier";
    public const string Storekeeper = "storekeeper";

    /// <summary>Кто заводит и меняет товары.</summary>
    public static readonly IReadOnlyList<string> CatalogWriters = [Owner, Admin, Manager, Storekeeper];
}
