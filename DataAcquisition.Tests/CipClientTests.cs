using DataAcquisition.Clients.CIP;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit.Abstractions;

namespace DataAcquisition.Tests;

public class CipClientTests
{
    private readonly Mock<ILogger> _mockLogger;
    private readonly CipClient _client;
    private readonly ITestOutputHelper _testOutputHelper;

    public CipClientTests(ITestOutputHelper testOutputHelper)
    {
        _mockLogger = new Mock<ILogger>();
        _client = new CipClient("127.0.0.1", 44818, _mockLogger.Object);
        _testOutputHelper = testOutputHelper;
    }

    [Fact]
    public async Task ConnectAsync_Should_Successfully_Connect_And_Disconnect()
    {
        await _client.ConnectAsync();
        Assert.True(_client.IsConnected);

        _testOutputHelper.WriteLine($"SessionHandle: 0x{_client.SessionHandle:X8}");
        _client.Dispose();
    }

    [Fact]
    public async Task ReadTagAsync_Should_Return_Correct_Value()
    {
        await _client.ConnectAsync();
        _testOutputHelper.WriteLine($"SessionHandle: 0x{_client.SessionHandle:X8}");
        var res = await _client.ReadTagAsync("MyDevice.Tag1");
        Assert.NotNull(res);
        _testOutputHelper.WriteLine($"MyDevice.Tag1: {res}");
        _client.Dispose();
    }

    [Fact]
    public async Task WriteTagAsync_AndRead_Should_Return_Correct_Value()
    {
        await _client.ConnectAsync();
        _testOutputHelper.WriteLine($"SessionHandle: 0x{_client.SessionHandle:X8}");
        for (int i = 0; i < 100; i++)
        {
            var t = new Random().Next(0, 444);
            await _client.WriteTagAsync("MyDevice.Tag1", (uint)t);
            var res = await _client.ReadTagAsync("MyDevice.Tag1");
            Assert.Equal(res, (uint)t);
            _testOutputHelper.WriteLine($"MyDevice.Tag1: {res}");
        }
        _client.Dispose();
    }

    [Fact]
    public async Task ConcurrentReadAsync()
    {
        List<CipClient> clients = new();
        for (int i = 0; i < 100; i++)
        {
            var client = new CipClient("127.0.0.1", 44818, _mockLogger.Object)
            {
                ClientId = $"client_{i}"
            };
            await client.ConnectAsync();
            clients.Add(client);
        }

        var tasks = clients.Select(async client =>
        {
            var res = await client.ReadTagAsync("MyDevice.Tag" + new Random().Next(1, 4));
            _testOutputHelper.WriteLine($"SessionHandle: 0x{client.SessionHandle:X8} {client.ClientId}: {res}");
            client.Dispose();
        });

        await Task.WhenAll(tasks);
        _client.Dispose();
    }

    [Fact]
    public async Task CircleReadTestMetrics()
    {
        await _client.ConnectAsync();
        _testOutputHelper.WriteLine($"SessionHandle: 0x{_client.SessionHandle:X8}");
        for (int i = 0; i < 1000; i++)
        {
            var res = await _client.ReadTagAsync("MyDevice.Tag1");
            //_testOutputHelper.WriteLine($"MyDevice.Tag1: {res}");
        }

        _testOutputHelper.WriteLine($"{_client.Metrics.RequestsPerSecond.Value}");
        _client.Dispose();
    }
}