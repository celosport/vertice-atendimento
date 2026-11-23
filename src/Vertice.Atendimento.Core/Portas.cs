namespace Vertice.Atendimento.Core;

/// <summary>
/// Como o robô fala com o cliente. Há o simulador (grátis, para testar e demonstrar) e a API oficial da Meta; trocar de um para o
/// outro é só configuração. Outros provedores entram implementando esta interface.
/// </summary>
public interface IMensageria
{
    Task EnviarTextoAsync(Negocio negocio, string numero, string texto, CancellationToken cancellationToken);

    /// <summary>
    /// Mensagem modelo (aprovada pela Meta): é a única que o WhatsApp deixa mandar quando o cliente não escreveu nas últimas 24 h,
    /// como o lembrete de horário. <paramref name="textoAlternativo"/> é o que o simulador mostra no lugar.
    /// </summary>
    Task EnviarModeloAsync(Negocio negocio, string numero, string modelo, IReadOnlyList<string> parametros, string textoAlternativo, CancellationToken cancellationToken);
}

public interface INegocioRepositorio
{
    Task<Negocio?> ObterAsync(string id, CancellationToken cancellationToken);
    Task<Negocio?> ObterPorPhoneNumberIdAsync(string phoneNumberId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Negocio>> ListarAsync(CancellationToken cancellationToken);
}

public interface IConversaRepositorio
{
    Task<EstadoConversa> ObterAsync(string negocioId, string numero, CancellationToken cancellationToken);
    Task SalvarAsync(string negocioId, string numero, EstadoConversa estado, CancellationToken cancellationToken);
}

public interface IAgendamentoRepositorio
{
    /// <summary>Agendamentos confirmados que tocam o intervalo (usados para saber quais horários estão livres).</summary>
    Task<IReadOnlyList<Agendamento>> ListarConfirmadosAsync(string negocioId, DateTimeOffset de, DateTimeOffset ate, CancellationToken cancellationToken);

    /// <summary>Cria o agendamento; devolve <c>false</c> se o horário já foi ocupado (decidido de forma atômica).</summary>
    Task<bool> TentarCriarAsync(Agendamento agendamento, CancellationToken cancellationToken);

    Task<Agendamento?> ProximoDoClienteAsync(string negocioId, string numero, DateTimeOffset agora, CancellationToken cancellationToken);
    Task CancelarAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Confirmados, no intervalo e ainda sem lembrete enviado.</summary>
    Task<IReadOnlyList<Agendamento>> ListarParaLembreteAsync(DateTimeOffset de, DateTimeOffset ate, CancellationToken cancellationToken);

    Task MarcarLembreteEnviadoAsync(Guid id, CancellationToken cancellationToken);
}
