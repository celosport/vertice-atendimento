namespace Vertice.Atendimento.Core;

/// <summary>Um serviço que o negócio do cliente vende e agenda (ex.: Corte, 30 min).</summary>
public sealed record Servico(string Id, string Nome, int DuracaoMin, decimal? Preco = null);

/// <summary>Janela em que o negócio atende num dia da semana (ex.: segunda, das 09:00 às 18:00).</summary>
public sealed record JanelaDia(DayOfWeek Dia, TimeOnly Abre, TimeOnly Fecha);

/// <summary>Pergunta frequente: se a mensagem tiver alguma das palavras, responde com o texto pronto.</summary>
public sealed record PerguntaFrequente(IReadOnlyList<string> Palavras, string Resposta);

/// <summary>
/// Um cliente da Vértice (salão, clínica...). Cada negócio tem o seu número de WhatsApp, serviços, horários e respostas.
/// </summary>
public sealed record Negocio(
    string Id,
    string Nome,
    string Fuso,
    string? WhatsAppPhoneNumberId,
    IReadOnlyList<Servico> Servicos,
    IReadOnlyList<JanelaDia> Horarios,
    IReadOnlyList<PerguntaFrequente> Faq,
    string? NumeroDoResponsavel = null);

public enum StatusAgendamento { Confirmado, Cancelado }

public sealed record Agendamento(
    Guid Id,
    string NegocioId,
    string Numero,
    string ServicoId,
    DateTimeOffset Inicio,
    DateTimeOffset Fim,
    StatusAgendamento Status = StatusAgendamento.Confirmado,
    bool LembreteEnviado = false);

public enum Passo
{
    Inicio,
    EscolhendoServico,
    EscolhendoDia,
    EscolhendoHorario,
    Confirmando,
    ConfirmandoCancelamento,

    /// <summary>Uma pessoa assumiu: o robô fica quieto até o cliente pedir "menu".</summary>
    Humano,
}

/// <summary>Onde a conversa de um cliente parou. <see cref="Opcoes"/> são os horários mostrados (ou o escolhido, em Confirmando).</summary>
public sealed record EstadoConversa(Passo Passo = Passo.Inicio, string? ServicoId = null, DateOnly? Dia = null, IReadOnlyList<TimeOnly>? Opcoes = null);

/// <param name="IdExterno">Id da mensagem no WhatsApp: serve para ignorar a mesma mensagem reenviada.</param>
public sealed record MensagemRecebida(string Numero, string Texto, string? PhoneNumberId = null, string? IdExterno = null);
