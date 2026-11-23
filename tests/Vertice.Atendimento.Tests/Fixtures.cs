using Microsoft.Extensions.Time.Testing;
using Vertice.Atendimento.Core;
using Vertice.Atendimento.Infra;
using Vertice.Atendimento.Infra.Mensageria;

namespace Vertice.Atendimento.Tests;

internal static class Fixtures
{
    /// <summary>Segunda-feira, 23/11/2026, 10:00 em São Paulo (UTC-3, sem horário de verão).</summary>
    public static readonly DateTimeOffset SegundaDezHoras = new(2026, 11, 23, 10, 0, 0, TimeSpan.FromHours(-3));

    public static Negocio Salao(string? responsavel = null) => new(
        "salao", "Salão Exemplo", "America/Sao_Paulo", "1111",
        [new Servico("corte", "Corte", 30, 50m), new Servico("escova", "Escova", 45, 60m), new Servico("cor", "Coloração", 90, 180m)],
        [
            .. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
                .Select(d => new JanelaDia(d, new TimeOnly(9, 0), new TimeOnly(18, 0))),
            new JanelaDia(DayOfWeek.Saturday, new TimeOnly(9, 0), new TimeOnly(13, 0)),
        ],
        [new PerguntaFrequente(["endereco", "onde fica"], "Ficamos na Rua Exemplo, 123.")],
        responsavel);

    public sealed class Cenario
    {
        public FakeTimeProvider Relogio { get; } = new(SegundaDezHoras);
        public SimuladorMensageria Mensageria { get; }
        public AgendamentoRepositorioMemoria Agenda { get; } = new();
        public ConversaRepositorioMemoria Conversas { get; } = new();
        public MotorDeAtendimento Motor { get; }
        public Negocio Negocio { get; }

        public Cenario(string? responsavel = null)
        {
            Negocio = Salao(responsavel);
            Mensageria = new SimuladorMensageria(Relogio);
            Motor = new MotorDeAtendimento(Conversas, Agenda, Mensageria, Relogio);
        }

        /// <summary>O cliente escreve e devolve as respostas do robô a essa mensagem.</summary>
        public async Task<string> DizerAsync(string numero, string texto)
        {
            var antes = Mensageria.Historico(Negocio.Id, numero).Count;
            await Motor.ProcessarAsync(Negocio, new MensagemRecebida(numero, texto, Negocio.WhatsAppPhoneNumberId), CancellationToken.None);
            return string.Join("\n---\n", Mensageria.Historico(Negocio.Id, numero).Skip(antes).Select(l => l.Texto));
        }

        public Task<IReadOnlyList<Agendamento>> TodosAsync(DateTimeOffset de, DateTimeOffset ate) =>
            Agenda.ListarConfirmadosAsync(Negocio.Id, de, ate, CancellationToken.None);
    }
}
