using System.Collections.Concurrent;
using Vertice.Atendimento.Core;

namespace Vertice.Atendimento.Infra.Mensageria;

/// <summary>Uma linha da conversa de teste: quem falou (o cliente ou o robô) e o texto.</summary>
public sealed record LinhaDaConversa(string De, string Texto, DateTimeOffset Quando);

/// <summary>
/// Mensageria de mentira, 100% grátis: guarda o que o robô "enviaria" para a tela de teste ler. Serve para desenvolver, demonstrar para
/// o cliente e rodar os testes sem WhatsApp, sem conta da Meta e sem custo.
/// </summary>
public sealed class SimuladorMensageria(TimeProvider relogio) : IMensageria
{
    private readonly ConcurrentDictionary<(string Negocio, string Numero), ConcurrentQueue<LinhaDaConversa>> _conversas = new();

    public Task EnviarTextoAsync(Negocio negocio, string numero, string texto, CancellationToken cancellationToken)
    {
        Registrar(negocio.Id, numero, "robo", texto);
        return Task.CompletedTask;
    }

    public Task EnviarModeloAsync(Negocio negocio, string numero, string modelo, IReadOnlyList<string> parametros, string textoAlternativo, CancellationToken cancellationToken)
    {
        Registrar(negocio.Id, numero, "robo", $"[modelo {modelo}] {textoAlternativo}");
        return Task.CompletedTask;
    }

    /// <summary>Registra o que o cliente (a pessoa que está testando) escreveu.</summary>
    public void RegistrarDoCliente(string negocioId, string numero, string texto) => Registrar(negocioId, numero, "cliente", texto);

    public IReadOnlyList<LinhaDaConversa> Historico(string negocioId, string numero) =>
        _conversas.TryGetValue((negocioId, numero), out var fila) ? fila.ToArray() : [];

    private void Registrar(string negocioId, string numero, string de, string texto) =>
        _conversas.GetOrAdd((negocioId, numero), _ => new ConcurrentQueue<LinhaDaConversa>()).Enqueue(new LinhaDaConversa(de, texto, relogio.GetUtcNow()));
}
