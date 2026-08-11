namespace LuxusDemandas.Api.Services;

public sealed class LuxusParceirosAttachmentRecoveryWorker : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LuxusParceirosAttachmentRecoveryWorker> _logger;

    public LuxusParceirosAttachmentRecoveryWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<LuxusParceirosAttachmentRecoveryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(InitialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var integration = scope.ServiceProvider
                    .GetRequiredService<LuxusParceirosIntegrationService>();
                var recovered = await integration
                    .RecoverMissingSaleAttachmentsAsync(stoppingToken);
                if (recovered > 0)
                {
                    _logger.LogInformation(
                        "Recuperação automática importou anexos de {Recovered} venda(s) do Luxus Parceiros.",
                        recovered);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                _logger.LogError(
                    error,
                    "Falha no ciclo de recuperação automática dos anexos do Luxus Parceiros.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
