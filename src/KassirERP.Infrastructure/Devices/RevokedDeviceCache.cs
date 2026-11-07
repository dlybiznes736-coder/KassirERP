using System.Collections.Concurrent;
using KassirERP.Application.Abstractions;
using KassirERP.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KassirERP.Infrastructure.Devices;

/// <summary>
/// Отключённые кассы в памяти API. Раз в 30 с — из базы (auth.revoked_devices:
/// отключённые за последние 13 ч — дольше токен по PIN не живёт). Отключение
/// через этот экземпляр попадает в кэш сразу.
/// </summary>
internal sealed class RevokedDeviceCache(KassirDataSource dataSource, ILogger<RevokedDeviceCache> logger)
    : BackgroundService, IRevokedDevices
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private volatile ConcurrentDictionary<Guid, byte> _revoked = new();

    public bool IsRevoked(Guid deviceId) => _revoked.ContainsKey(deviceId);

    public void MarkRevoked(Guid deviceId) => _revoked[deviceId] = 0;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        do
        {
            try
            {
                var fresh = new ConcurrentDictionary<Guid, byte>();
                await using var command = dataSource.Inner.CreateCommand("SELECT auth.revoked_devices()");
                await using var reader = await command.ExecuteReaderAsync(stoppingToken);
                while (await reader.ReadAsync(stoppingToken))
                {
                    fresh[reader.GetGuid(0)] = 0;
                }

                // Отключённые здесь за время запроса не теряем.
                foreach (var id in _revoked.Keys)
                {
                    fresh.TryAdd(id, 0);
                }

                _revoked = fresh;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // База недоступна — остаётся прежний список; вход по PIN при этом всё равно не пройдёт.
                logger.LogWarning(ex, "Не удалось обновить список отключённых касс");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
