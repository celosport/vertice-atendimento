using Vertice.Atendimento.Core;

namespace Vertice.Atendimento.Tests;

public sealed class CalculadoraDeHorariosTests
{
    private static readonly Negocio Salao = Fixtures.Salao();
    private static readonly Servico Corte = Salao.Servicos[0];
    private static readonly Servico Escova = Salao.Servicos[1];
    private static readonly Servico Coloracao = Salao.Servicos[2];
    private static readonly DateOnly Terca = new(2026, 11, 24);

    private static Agendamento Ocupado(DateOnly dia, TimeOnly hora, int minutos) =>
        new(Guid.NewGuid(), Salao.Id, "x", "corte", CalculadoraDeHorarios.Instante(Salao, dia, hora), CalculadoraDeHorarios.Instante(Salao, dia, hora).AddMinutes(minutos));

    [Fact]
    public void Dia_inteiro_livre_vai_da_abertura_ate_a_ultima_vaga_que_cabe()
    {
        var livres = CalculadoraDeHorarios.Livres(Salao, Corte, Terca, [], Fixtures.SegundaDezHoras);

        Assert.Equal(18, livres.Count);
        Assert.Equal(new TimeOnly(9, 0), livres[0]);
        Assert.Equal(new TimeOnly(17, 30), livres[^1]);
    }

    [Fact]
    public void Servico_longo_nao_comeca_se_nao_terminar_antes_de_fechar()
    {
        var livres = CalculadoraDeHorarios.Livres(Salao, Coloracao, Terca, [], Fixtures.SegundaDezHoras);

        Assert.Equal(new TimeOnly(16, 30), livres[^1]); // 16:30 + 90 min = 18:00
    }

    [Fact]
    public void Hoje_so_mostra_horarios_que_ainda_nao_passaram()
    {
        var livres = CalculadoraDeHorarios.Livres(Salao, Corte, new DateOnly(2026, 11, 23), [], Fixtures.SegundaDezHoras);

        Assert.Equal(new TimeOnly(10, 30), livres[0]); // agora são 10:00: o das 10:00 já começou
    }

    [Fact]
    public void Horario_ocupado_some_e_o_que_encosta_nele_tambem()
    {
        var ocupado = Ocupado(Terca, new TimeOnly(10, 0), 30);

        var corte = CalculadoraDeHorarios.Livres(Salao, Corte, Terca, [ocupado], Fixtures.SegundaDezHoras);
        var escova = CalculadoraDeHorarios.Livres(Salao, Escova, Terca, [ocupado], Fixtures.SegundaDezHoras);

        Assert.DoesNotContain(new TimeOnly(10, 0), corte);
        Assert.Contains(new TimeOnly(9, 30), corte); // termina às 10:00, quando o outro começa
        Assert.Contains(new TimeOnly(10, 30), corte);
        Assert.DoesNotContain(new TimeOnly(9, 30), escova); // 9:30 + 45 min invade as 10:00
        Assert.Contains(new TimeOnly(9, 0), escova);
    }

    [Fact]
    public void Agendamento_cancelado_libera_o_horario()
    {
        var cancelado = Ocupado(Terca, new TimeOnly(10, 0), 30) with { Status = StatusAgendamento.Cancelado };

        var livres = CalculadoraDeHorarios.Livres(Salao, Corte, Terca, [cancelado], Fixtures.SegundaDezHoras);

        Assert.Contains(new TimeOnly(10, 0), livres);
    }

    [Fact]
    public void Sabado_fecha_ao_meio_dia_e_domingo_nao_tem_atendimento()
    {
        var sabado = CalculadoraDeHorarios.Livres(Salao, Corte, new DateOnly(2026, 11, 28), [], Fixtures.SegundaDezHoras);
        var domingo = CalculadoraDeHorarios.Livres(Salao, Corte, new DateOnly(2026, 11, 29), [], Fixtures.SegundaDezHoras);

        Assert.Equal(8, sabado.Count);
        Assert.Equal(new TimeOnly(12, 30), sabado[^1]);
        Assert.Empty(domingo);
    }
}
