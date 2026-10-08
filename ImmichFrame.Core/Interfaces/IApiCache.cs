namespace ImmichFrame.Core.Interfaces
{
    public interface IApiCache
    {
        Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> factory);
    }
}