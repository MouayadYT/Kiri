using Assistant.Core.Home;

namespace Assistant.UI.Tests;

/// <summary>A Home Assistant in memory: it takes the address and token a test says it takes, and remembers what it was asked.</summary>
internal sealed class FakeHomeAssistant : IHomeAssistant
{
    public event EventHandler? Changed;

    /// <summary>How a connection or a check ends; <see cref="HomeFailure.None"/> for one that works.</summary>
    public HomeFailure Answer { get; set; } = HomeFailure.None;

    /// <summary>How many devices it says it has.</summary>
    public int DeviceCount { get; set; } = 12;

    /// <summary>The address and the token of each attempt to connect.</summary>
    public List<(string Address, string Token)> Attempts { get; } = [];

    public int Checks { get; private set; }

    public bool IsConnected => Address is not null;

    public string? Address { get; private set; }

    public IReadOnlyList<HomeDevice> Known { get; private set; } = [];

    /// <summary>Gives it the devices it lists.</summary>
    public FakeHomeAssistant With(params HomeDevice[] devices)
    {
        Known = devices;
        return this;
    }

    /// <summary>Starts it as connected already, as after an earlier run.</summary>
    public FakeHomeAssistant ConnectedAt(string address)
    {
        Address = address;
        return this;
    }

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task TakeOverEarlierAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<HomeStatus> ConnectAsync(string address, string token, CancellationToken cancellationToken = default)
    {
        Attempts.Add((address, token));
        if (Answer != HomeFailure.None)
        {
            return Task.FromResult(new HomeStatus(Answer, null));
        }

        Address = address.Contains("://", StringComparison.Ordinal) ? address.TrimEnd('/') : "http://" + address.TrimEnd('/');
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new HomeStatus(HomeFailure.None, Address, DeviceCount));
    }

    public Task<HomeStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        Checks++;
        return Task.FromResult(Address is null ? new HomeStatus(HomeFailure.NotConnected, null) : new HomeStatus(Answer, Address, Answer == HomeFailure.None ? DeviceCount : 0));
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Address = null;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task<HomeDevices> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Address is null ? HomeDevices.Failed(HomeFailure.NotConnected) : new HomeDevices(Answer, Known));

    public Task<HomeCallResult> CallAsync(
        string domain, string service, string deviceId, IReadOnlyDictionary<string, double>? data = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new HomeCallResult(Address is null ? HomeFailure.NotConnected : Answer));
}
