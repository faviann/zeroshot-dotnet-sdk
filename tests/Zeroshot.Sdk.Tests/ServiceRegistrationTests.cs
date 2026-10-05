using Microsoft.Extensions.DependencyInjection;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class ServiceRegistrationTests
{
    private static readonly Uri Origin = new("http://127.0.0.1:1/");
    private static readonly NativeBinding Binding = NativeBinding.CallerSupplied(NativeSchemas.NativeVersion, NativeSchemas.SourceRevision);
    private static ZeroshotClientOptions Options(TransportOptions? transport = null)
        => new() { Target = Origin, NativeBinding = Binding, Transport = transport ?? new() };

    [Test]
    public void ResolvesOneClientPairForTheConfiguredTarget()
    {
        using var provider = new ServiceCollection().AddZeroshotClient(Options()).BuildServiceProvider();
        var zeroshot = provider.GetRequiredService<ZeroshotClient>();
        Check(ReferenceEquals(zeroshot, provider.GetRequiredService<ZeroshotClient>()), "ZeroshotClient is a singleton.");
        Check(ReferenceEquals(provider.GetRequiredService<NativeClient>(), provider.GetRequiredService<NativeClient>()), "NativeClient is a singleton.");
        Check(zeroshot.Target == Origin && provider.GetRequiredService<NativeClient>().Origin == Origin);
        Check(zeroshot.NativeBinding == Binding);
    }

    [Test]
    public void ZeroshotClientTakesItsControlSlotOnTheRegisteredNativeClient()
    {
        using var provider = new ServiceCollection()
            .AddZeroshotClient(Options(new() { MaxOecpConnections = 1 })).BuildServiceProvider();
        provider.GetRequiredService<ZeroshotClient>();
        var native = provider.GetRequiredService<NativeClient>();
        var refused = Catch<InvalidOperationException>(() => new ZeroshotClient(native, Binding));
        Check(refused.Message.Contains("MaxOecpConnections"), refused.Message);
    }

    [Test]
    public async Task DisposingTheProviderDisposesBothClients()
    {
        var provider = new ServiceCollection().AddZeroshotClient(Options()).BuildServiceProvider();
        var zeroshot = provider.GetRequiredService<ZeroshotClient>();
        var native = provider.GetRequiredService<NativeClient>();
        provider.Dispose();
        Catch<ObjectDisposedException>(() => zeroshot.GetRun(new RunId("0195af77-1000-7000-8000-000000000001")));
        await Failure<ObjectDisposedException>(() => native.Target.DiscoverAsync(), _ => true);
    }

    [Test]
    public void OptionsFactoryReadsTheContainerOnce()
    {
        var calls = 0;
        var services = new ServiceCollection().AddSingleton(Origin);
        services.AddZeroshotClient(provider =>
        {
            calls++;
            return new ZeroshotClientOptions { Target = provider.GetRequiredService<Uri>(), NativeBinding = Binding };
        });
        using var provider = services.BuildServiceProvider();
        var zeroshot = provider.GetRequiredService<ZeroshotClient>();
        var native = provider.GetRequiredService<NativeClient>();
        Check(calls == 1, $"Factory ran {calls} times.");
        Check(zeroshot.Target == Origin && native.Origin == Origin);
    }

    [Test]
    public void NullArgumentsAreRejected()
    {
        Catch<ArgumentNullException>(() => ZeroshotServiceCollectionExtensions.AddZeroshotClient(null!, Options()));
        Catch<ArgumentNullException>(() => ZeroshotServiceCollectionExtensions.AddZeroshotClient(null!, _ => Options()));
        Catch<ArgumentNullException>(() => new ServiceCollection().AddZeroshotClient((ZeroshotClientOptions)null!));
        Catch<ArgumentNullException>(() => new ServiceCollection().AddZeroshotClient((Func<IServiceProvider, ZeroshotClientOptions>)null!));
        using var provider = new ServiceCollection().AddZeroshotClient(_ => null!).BuildServiceProvider();
        Catch<InvalidOperationException>(() => provider.GetRequiredService<ZeroshotClient>());
    }

    private static T Catch<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
