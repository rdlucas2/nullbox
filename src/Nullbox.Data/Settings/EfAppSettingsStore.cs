using Microsoft.EntityFrameworkCore;
using Nullbox.Core.Settings;

namespace Nullbox.Data.Settings;

/// <summary><see cref="IAppSettingsStore"/> over the <c>app_settings</c> table.</summary>
public sealed class EfAppSettingsStore(NullboxDbContext db) : IAppSettingsStore
{
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        => (await db.AppSettings.AsNoTracking().SingleOrDefaultAsync(a => a.Key == key, cancellationToken))?.Value;

    public async Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var row = await db.AppSettings.FindAsync([key], cancellationToken);
        if (value is null)
        {
            if (row is not null)
            {
                db.AppSettings.Remove(row);
            }
        }
        else if (row is null)
        {
            db.AppSettings.Add(new AppSettingRow { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
