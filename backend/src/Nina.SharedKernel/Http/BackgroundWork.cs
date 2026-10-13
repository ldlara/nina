using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nina.SharedKernel.Http;

/// <summary>
/// Trabalho que não deve atrasar a resposta HTTP (ex.: envio de e-mail transacional). Existe para que respostas de
/// endpoints anti-enumeração (NR-12) tenham o mesmo custo independentemente de haver ou não e-mail a enviar.
/// O item não pode depender de serviços com escopo de requisição nem do token da requisição.
/// </summary>
public interface IBackgroundWork
{
    /// <summary>Enfileira o trabalho; retorna imediatamente. Se a fila estiver cheia o item é descartado (e registrado).</summary>
    void Enqueue(Func<CancellationToken, Task> work);
}

/// <summary>Fila limitada em memória processada por um pequeno conjunto de workers (substituível por outbox/fila externa).</summary>
public sealed partial class BackgroundWorkRunner(ILogger<BackgroundWorkRunner> logger) : BackgroundService, IBackgroundWork
{
    private const int Capacity = 10_000;
    private const int Workers = 4;
    private readonly Channel<Func<CancellationToken, Task>> _channel =
        Channel.CreateBounded<Func<CancellationToken, Task>>(new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.Wait });

    public void Enqueue(Func<CancellationToken, Task> work)
    {
        if (!_channel.Writer.TryWrite(work))
        {
            LogDropped(logger);
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Workers).Select(_ => Task.Run(() => RunAsync(stoppingToken), stoppingToken)));

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in _channel.Reader.ReadAllAsync(stoppingToken))
        {
#pragma warning disable CA1031 // um item com falha não pode derrubar o worker
            try
            {
                await work(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex.GetType().Name);
            }
#pragma warning restore CA1031
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Fila de trabalho em segundo plano cheia: item descartado")]
    private static partial void LogDropped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Trabalho em segundo plano falhou ({ExceptionType})")]
    private static partial void LogFailed(ILogger logger, string exceptionType);
}
