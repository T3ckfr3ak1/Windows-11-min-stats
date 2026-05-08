using System.Threading;

namespace TaskbarResourceMonitor;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    public bool IsPrimaryInstance { get; }

    public SingleInstance(string name)
    {
        _mutex = new Mutex(initiallyOwned: true, name: name, createdNew: out var createdNew);
        IsPrimaryInstance = createdNew;
    }

    public void Dispose()
    {
        if (IsPrimaryInstance)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex.Dispose();
    }
}

