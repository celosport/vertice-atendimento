using Vertice.Atendimento.Core;
using Vertice.Atendimento.Infra;

namespace Vertice.Atendimento.Tests;

public sealed class LembretesETests
{
    private sealed class NegociosFalso(Negocio negocio) : INegocioRepositorio
    {
        public Task<Negocio?> ObterAsync(string id, CancellationToken cancellationToken) => Task.FromResult<Negocio?>(id == negocio.Id ? negocio : null);
        public Task<Negocio?> ObterPorPhoneNumberIdAsync(string phoneNumberId, CancellationToken cancellationToken) => Task.FromResult<Negocio?>(negocio);
        public Task<IReadOnlyList<Negocio>> ListarAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Negocio>>([negocio]);
    }

    private sealed class MensageriaQueFalha : IMensageria
    {
        public Task EnviarTextoAsync(Negocio negocio, string numero, string texto, CancellationToken cancellationToken) => throw new HttpRequestException("Meta fora do ar");
        public Task EnviarModeloAsync(Negocio negocio, string numero, string modelo, IReadOnlyList<string> parametros, string textoAlternativo, CancellationToken cancellationToken) => throw new HttpRequestException("Meta fora do ar");
    }

    private static Agendamento Marcar(Negocio n, int dia, int hora) => new(
        Guid.NewGuid(), n.Id, "5521911112222", "corte",
        CalculadoraDeHorarios.Instante(n, new DateOnly(2026, 11, dia), new TimeOnly(hora, 0)),
        CalculadoraDeHorarios.Instante(n, new DateOnly(2026, 11, dia), new TimeOnly(hora, 30)));

    [Fact]
    public async Task Manda_o_lembrete_das_proximas_24_horas_uma_unica_vez()
    {
        var c = new Fixtures.Cenario();
        await c.Agenda.TentarCriarAsync(Marcar(c.Negocio, 24, 9), default); // daqui a 23 h
        await c.Agenda.TentarCriarAsync(Marcar(c.Negocio, 26, 9), default); // daqui a 71 h: ainda cedo
        var servico = new ServicoDeLembretes(c.Agenda, new NegociosFalso(c.Negocio), c.Mensageria, c.Relogio);

        Assert.Equal(1, await servico.ExecutarAsync(default));
        Assert.Equal(0, await servico.ExecutarAsync(default)); // já foi lembrado

        var lembrete = Assert.Single(c.Mensageria.Historico(c.Negocio.Id, "5521911112222"));
        Assert.Equal("[modelo lembrete_agendamento] Olá! Lembrete: Corte em 24/11 às 09:00. Para cancelar, responda *cancelar*.", lembrete.Texto);
    }

    [Fact]
    public async Task Quando_o_dia_do_agendamento_chega_perto_o_lembrete_sai()
    {
        var c = new Fixtures.Cenario();
        await c.Agenda.TentarCriarAsync(Marcar(c.Negocio, 26, 9), default);
        var servico = new ServicoDeLembretes(c.Agenda, new NegociosFalso(c.Negocio), c.Mensageria, c.Relogio);
        Assert.Equal(0, await servico.ExecutarAsync(default));

        c.Relogio.Advance(TimeSpan.FromHours(48)); // agora faltam 23 h

        Assert.Equal(1, await servico.ExecutarAsync(default));
    }

    [Fact]
    public async Task Agendamento_cancelado_nao_recebe_lembrete()
    {
        var c = new Fixtures.Cenario();
        var a = Marcar(c.Negocio, 24, 9);
        await c.Agenda.TentarCriarAsync(a, default);
        await c.Agenda.CancelarAsync(a.Id, default);
        var servico = new ServicoDeLembretes(c.Agenda, new NegociosFalso(c.Negocio), c.Mensageria, c.Relogio);

        Assert.Equal(0, await servico.ExecutarAsync(default));
    }

    [Fact]
    public async Task Se_o_envio_falha_o_lembrete_nao_e_marcado_e_sai_no_proximo_ciclo()
    {
        var c = new Fixtures.Cenario();
        await c.Agenda.TentarCriarAsync(Marcar(c.Negocio, 24, 9), default);

        var quebrado = new ServicoDeLembretes(c.Agenda, new NegociosFalso(c.Negocio), new MensageriaQueFalha(), c.Relogio);
        await Assert.ThrowsAsync<HttpRequestException>(() => quebrado.ExecutarAsync(default));

        var normal = new ServicoDeLembretes(c.Agenda, new NegociosFalso(c.Negocio), c.Mensageria, c.Relogio);
        Assert.Equal(1, await normal.ExecutarAsync(default));
    }
}
