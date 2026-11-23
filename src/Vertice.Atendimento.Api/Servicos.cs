using System.Collections.Concurrent;
using Vertice.Atendimento.Core;

namespace Vertice.Atendimento.Api;

/// <summary>Lembra por alguns minutos os ids de mensagens já tratadas: a Meta reenvia o webhook quando acha que não chegou.</summary>
public sealed class MensagensJaProcessadas(TimeProvider relogio)
{
    private static readonly TimeSpan Validade = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _vistas = new();

    /// <returns><c>true</c> se é a primeira vez que este id aparece.</returns>
    public bool PrimeiraVez(string id)
    {
        var agora = relogio.GetUtcNow();
        if (_vistas.Count > 5000)
            foreach (var (chave, quando) in _vistas)
                if (agora - quando > Validade) _vistas.TryRemove(chave, out _);
        return _vistas.TryAdd(id, agora);
    }
}

/// <summary>Roda o envio de lembretes a cada poucos minutos. Desligue com <c>Lembretes:Habilitado = false</c> (os testes fazem isso).</summary>
public sealed class LembretesWorker(ServicoDeLembretes lembretes, IConfiguration config, ILogger<LembretesWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalo = TimeSpan.FromMinutes(config.GetValue("Lembretes:IntervaloMinutos", 5));
        using var timer = new PeriodicTimer(intervalo);
        do
        {
            try
            {
                var enviados = await lembretes.ExecutarAsync(stoppingToken);
                if (enviados > 0) log.LogInformation("Lembretes enviados: {Quantidade}.", enviados);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Um ciclo com problema não derruba o serviço: o próximo tenta de novo (o lembrete só é marcado depois de enviado)
                log.LogError("Falha no ciclo de lembretes ({Tipo}).", ex.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
