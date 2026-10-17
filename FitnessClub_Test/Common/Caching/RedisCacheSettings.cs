namespace Common.Caching;
public class RedisCacheSettings
{
    public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromHours(1);
}
