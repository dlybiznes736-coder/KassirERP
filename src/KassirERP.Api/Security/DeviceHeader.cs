namespace KassirERP.Api.Security;

/// <summary>
/// Касса подтверждает себя заголовком <c>Authorization: Device &lt;deviceId&gt;.&lt;deviceSecret&gt;</c>.
/// JwtBearer такой заголовок не трогает (у него схема Bearer).
/// </summary>
internal static class DeviceHeader
{
    private const string Scheme = "Device ";

    public static bool TryParse(HttpRequest request, out Guid deviceId, out string secret)
    {
        deviceId = Guid.Empty;
        secret = "";
        var value = request.Headers.Authorization.ToString();
        if (!value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var credentials = value[Scheme.Length..].Trim();
        var dot = credentials.IndexOf('.');
        if (dot <= 0 || dot == credentials.Length - 1 || !Guid.TryParse(credentials[..dot], out deviceId))
        {
            return false;
        }

        secret = credentials[(dot + 1)..];
        return secret.Length <= 200;
    }
}
