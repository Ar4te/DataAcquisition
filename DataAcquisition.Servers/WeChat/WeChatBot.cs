using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace DataAcquisition.Servers.WeChat;

public class MyClass
{
    private readonly ThirdLogPushMessageQueue _thirdLogPushMessageQueue;

    public MyClass(ThirdLogPushMessageQueue thirdLogPushMessageQueue)
    {
        _thirdLogPushMessageQueue = thirdLogPushMessageQueue;
    }

    public async Task SendMessageAsync(string message)
    {
        await _thirdLogPushMessageQueue.EnqueueMessageAsync(message);
    }
}

public interface IThirdLogPushService
{
    Task SendMessageAsync(string message, CancellationToken cancellationToken);
}

public class WeChatBot : IThirdLogPushService
{
    private readonly HttpClient _httpClient;
    private readonly string _webhookUrl;

    public WeChatBot(HttpClient httpClient, string webhookUrl)
    {
        _httpClient = httpClient;
        _webhookUrl = webhookUrl;
    }

    public async Task SendMessageAsync(string message, CancellationToken cancellationToken)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(new { msgtype = "text", text = new { content = message, mentioned_list = (List<string>)["@all"] } }),
            Encoding.UTF8,
            "application/json"
        );

        try
        {
            var response = await _httpClient.PostAsync(_webhookUrl, content, cancellationToken);
            response.EnsureSuccessStatusCode();
            Console.WriteLine("Message sent successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending message: {ex.Message}");
        }
    }
}

public class ThirdLogPushMessageQueue
{
    private readonly IEnumerable<IThirdLogPushService> _pushServices;
    private readonly Channel<string> _channel;

    public ThirdLogPushMessageQueue(IEnumerable<IThirdLogPushService> pushServices)
    {
        _pushServices = pushServices;
        _channel = Channel.CreateUnbounded<string>();
    }

    public async Task EnqueueMessageAsync(string message)
    {
        await _channel.Writer.WriteAsync(message);
    }

    public async Task StartProcessingAsync(CancellationToken cancellationToken)
    {
        while (await _channel.Reader.WaitToReadAsync(cancellationToken))
        {
            while (_channel.Reader.TryRead(out var message))
            {
                var tasks = _pushServices.Select(pushService => ProcessMessageAsync(pushService, message, cancellationToken));

                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing message: {ex.Message}");
                }
            }
        }
    }

    private async Task ProcessMessageAsync(IThirdLogPushService service, string message, CancellationToken cancellationToken)
    {
        try
        {
            await service.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in {service.GetType().Name}: {ex.Message}");
        }
    }
}
