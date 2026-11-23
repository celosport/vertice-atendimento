using System.Collections.Concurrent;
using Vertice.Atendimento.Core;

namespace Vertice.Atendimento.Infra;

/// <summary>Conversas na memória: some quando o serviço reinicia. Serve para testes, simulador e demonstração.</summary>
public sealed class ConversaRepositorioMemoria : IConversaRepositorio
{
    private readonly ConcurrentDictionary<(string, string), EstadoConversa> _estados = new();

    public Task<EstadoConversa> ObterAsync(string negocioId, string numero, CancellationToken cancellationToken) =>
        Task.FromResult(_estados.GetValueOrDefault((negocioId, numero), new EstadoConversa()));

    public Task SalvarAsync(string negocioId, string numero, EstadoConversa estado, CancellationToken cancellationToken)
    {
        _estados[(negocioId, numero)] = estado;
        return Task.CompletedTask;
    }
}

/// <summary>Agenda na memória. A criação é atômica (lock): dois clientes nunca ficam com o mesmo horário.</summary>
public sealed class AgendamentoRepositorioMemoria : IAgendamentoRepositorio
{
    private readonly object _trava = new();
    private readonly List<Agendamento> _lista = [];

    public Task<IReadOnlyList<Agendamento>> ListarConfirmadosAsync(string negocioId, DateTimeOffset de, DateTimeOffset ate, CancellationToken cancellationToken)
    {
        lock (_trava)
            return Task.FromResult<IReadOnlyList<Agendamento>>(_lista
                .Where(a => a.NegocioId == negocioId && a.Status == StatusAgendamento.Confirmado && a.Inicio < ate && de < a.Fim).ToList());
    }

    public Task<bool> TentarCriarAsync(Agendamento agendamento, CancellationToken cancellationToken)
    {
        lock (_trava)
        {
            var conflito = _lista.Any(a => a.NegocioId == agendamento.NegocioId && a.Status == StatusAgendamento.Confirmado
                                           && a.Inicio < agendamento.Fim && agendamento.Inicio < a.Fim);
            if (conflito) return Task.FromResult(false);
            _lista.Add(agendamento);
            return Task.FromResult(true);
        }
    }

    public Task<Agendamento?> ProximoDoClienteAsync(string negocioId, string numero, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        lock (_trava)
            return Task.FromResult(_lista
                .Where(a => a.NegocioId == negocioId && a.Numero == numero && a.Status == StatusAgendamento.Confirmado && a.Inicio > agora)
                .OrderBy(a => a.Inicio).FirstOrDefault());
    }

    public Task CancelarAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_trava)
        {
            var i = _lista.FindIndex(a => a.Id == id);
            if (i >= 0) _lista[i] = _lista[i] with { Status = StatusAgendamento.Cancelado };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Agendamento>> ListarParaLembreteAsync(DateTimeOffset de, DateTimeOffset ate, CancellationToken cancellationToken)
    {
        lock (_trava)
            return Task.FromResult<IReadOnlyList<Agendamento>>(_lista
                .Where(a => a.Status == StatusAgendamento.Confirmado && !a.LembreteEnviado && a.Inicio > de && a.Inicio <= ate).ToList());
    }

    public Task MarcarLembreteEnviadoAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_trava)
        {
            var i = _lista.FindIndex(a => a.Id == id);
            if (i >= 0) _lista[i] = _lista[i] with { LembreteEnviado = true };
        }
        return Task.CompletedTask;
    }
}
