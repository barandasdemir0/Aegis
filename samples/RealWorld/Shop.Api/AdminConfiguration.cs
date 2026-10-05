namespace Shop.Api;

/// <summary>
/// Çalışırken değiştirilebilen yapılandırma (gerçek hayatta Azure App Configuration / Consul gibi bir kaynağın yerine).
/// Değer değişince değişiklik belirteci tetiklenir: IOptionsMonitor ve Aegis'in yeniden yüklenen boru hatları yeni değeri görür.
/// </summary>
public sealed class AdminConfigurationSource : IConfigurationSource
{
    public AdminConfigurationProvider Provider { get; } = new();

    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}

public sealed class AdminConfigurationProvider : ConfigurationProvider
{
    public void Put(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (key, value) in values)
        {
            Data[key] = value;
        }

        OnReload();
    }
}
