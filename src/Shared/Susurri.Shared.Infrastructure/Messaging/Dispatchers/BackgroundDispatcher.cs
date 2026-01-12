using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Susurri.Shared.Abstractions.Modules;

namespace Susurri.Shared.Infrastructure.Messaging.Dispatchers;


public class BackgroundDispatcher : BackgroundService
{
    private readonly IMessageChannel _channel;
    private readonly IModuleClient _moduleClient;
    private readonly ILogger<BackgroundDispatcher> _logger;

    public BackgroundDispatcher(IMessageChannel channel, IModuleClient moduleClient, ILogger<BackgroundDispatcher> logger)
    {
        _channel = channel;
        _moduleClient = moduleClient;
        _logger = logger;
    }

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Running background dispatcher.");

        await foreach (var message in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await _moduleClient.PublishAsync(message);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, exception.Message);
            }
        }
        
        _logger.LogInformation("Finished running background dispatcher.");
    }
}