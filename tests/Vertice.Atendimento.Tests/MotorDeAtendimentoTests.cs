using Vertice.Atendimento.Core;

namespace Vertice.Atendimento.Tests;

public sealed class MotorDeAtendimentoTests
{
    private const string Maria = "5521911112222";
    private const string Joao = "5521933334444";
    private static readonly DateOnly Terca = new(2026, 11, 24);

    private static async Task<Fixtures.Cenario> AteConfirmarAsync(string numero, Fixtures.Cenario? cenario = null)
    {
        cenario ??= new Fixtures.Cenario();
        await cenario.DizerAsync(numero, "agendar");
        await cenario.DizerAsync(numero, "1");
        await cenario.DizerAsync(numero, "amanhã");
        await cenario.DizerAsync(numero, "1");
        return cenario;
    }

    [Fact]
    public async Task Oi_mostra_o_menu_com_o_nome_do_negocio()
    {
        var c = new Fixtures.Cenario();

        var resposta = await c.DizerAsync(Maria, "Oi!");

        Assert.Contains("assistente do Salão Exemplo", resposta);
        Assert.Contains("1) Agendar um horário", resposta);
    }

    [Fact]
    public async Task Fluxo_completo_de_agendamento_grava_o_horario_certo()
    {
        var c = new Fixtures.Cenario();

        Assert.Contains("Qual serviço", await c.DizerAsync(Maria, "agendar"));
        Assert.Contains("Para qual dia", await c.DizerAsync(Maria, "1"));
        var horarios = await c.DizerAsync(Maria, "amanhã");
        Assert.Contains("Horários livres em 24/11", horarios);
        Assert.Contains("1) 09:00", horarios);
        Assert.Contains("Confirma Corte em 24/11 às 09:00", await c.DizerAsync(Maria, "1"));
        Assert.Contains("Agendado!", await c.DizerAsync(Maria, "sim"));

        var dia = CalculadoraDeHorarios.Instante(c.Negocio, Terca, TimeOnly.MinValue);
        var agendados = await c.TodosAsync(dia, dia.AddDays(1));
        var a = Assert.Single(agendados);
        Assert.Equal(Maria, a.Numero);
        Assert.Equal("corte", a.ServicoId);
        Assert.Equal(CalculadoraDeHorarios.Instante(c.Negocio, Terca, new TimeOnly(9, 0)), a.Inicio);
        Assert.Equal(a.Inicio.AddMinutes(30), a.Fim);
    }

    [Fact]
    public async Task Servico_pode_ser_escolhido_pelo_nome()
    {
        var c = new Fixtures.Cenario();
        await c.DizerAsync(Maria, "quero marcar");

        var resposta = await c.DizerAsync(Maria, "escova");

        Assert.Contains("Escova", resposta);
        Assert.Contains("Para qual dia", resposta);
    }

    [Fact]
    public async Task Dois_clientes_no_mesmo_horario_so_um_fica_com_ele()
    {
        var c = await AteConfirmarAsync(Maria);
        await AteConfirmarAsync(Joao, c); // o João também escolheu 09:00 de amanhã, antes de a Maria confirmar

        Assert.Contains("Agendado!", await c.DizerAsync(Maria, "sim"));
        var joao = await c.DizerAsync(Joao, "sim");

        Assert.Contains("acabou de ser ocupado", joao);
        var dia = CalculadoraDeHorarios.Instante(c.Negocio, Terca, TimeOnly.MinValue);
        Assert.Single(await c.TodosAsync(dia, dia.AddDays(1)));
        // O João volta a escolher o dia e já não vê as 09:00
        var novos = await c.DizerAsync(Joao, "amanhã");
        Assert.DoesNotContain("09:00", novos);
    }

    [Fact]
    public async Task Dizer_nao_na_confirmacao_nao_agenda()
    {
        var c = await AteConfirmarAsync(Maria);

        var resposta = await c.DizerAsync(Maria, "não");

        Assert.Contains("não agendei", resposta);
        var dia = CalculadoraDeHorarios.Instante(c.Negocio, Terca, TimeOnly.MinValue);
        Assert.Empty(await c.TodosAsync(dia, dia.AddDays(1)));
    }

    [Fact]
    public async Task Cancelar_pede_confirmacao_e_libera_o_horario()
    {
        var c = await AteConfirmarAsync(Maria);
        await c.DizerAsync(Maria, "sim");

        Assert.Contains("Seu próximo horário é Corte em 24/11 às 09:00", await c.DizerAsync(Maria, "cancelar"));
        Assert.Contains("Cancelado", await c.DizerAsync(Maria, "sim"));

        var dia = CalculadoraDeHorarios.Instante(c.Negocio, Terca, TimeOnly.MinValue);
        Assert.Empty(await c.TodosAsync(dia, dia.AddDays(1)));
        Assert.Contains("não tem horário marcado", await c.DizerAsync(Maria, "cancelar"));
    }

    [Fact]
    public async Task Cancelar_e_responder_nao_mantem_o_horario()
    {
        var c = await AteConfirmarAsync(Maria);
        await c.DizerAsync(Maria, "sim");
        await c.DizerAsync(Maria, "desmarcar");

        Assert.Contains("mantive o seu horário", await c.DizerAsync(Maria, "não"));

        var dia = CalculadoraDeHorarios.Instante(c.Negocio, Terca, TimeOnly.MinValue);
        Assert.Single(await c.TodosAsync(dia, dia.AddDays(1)));
    }

    [Fact]
    public async Task Cliente_so_cancela_o_proprio_horario()
    {
        var c = await AteConfirmarAsync(Maria);
        await c.DizerAsync(Maria, "sim");

        Assert.Contains("não tem horário marcado", await c.DizerAsync(Joao, "2"));
    }

    [Fact]
    public async Task Pedir_uma_pessoa_silencia_o_robo_ate_o_cliente_pedir_o_menu_e_avisa_o_responsavel()
    {
        const string responsavel = "5521988887777";
        var c = new Fixtures.Cenario(responsavel);

        Assert.Contains("vou chamar uma pessoa", await c.DizerAsync(Maria, "quero falar com uma pessoa"));
        Assert.Contains(c.Mensageria.Historico(c.Negocio.Id, responsavel), l => l.Texto.Contains(Maria) && l.Texto.Contains("falar com uma pessoa"));

        Assert.Equal("", await c.DizerAsync(Maria, "alô, tem alguém aí?"));
        Assert.Equal("", await c.DizerAsync(Maria, "oi"));
        Assert.Contains("assistente do Salão Exemplo", await c.DizerAsync(Maria, "menu"));
    }

    [Fact]
    public async Task Pergunta_frequente_e_respondida_sem_sair_do_menu()
    {
        var c = new Fixtures.Cenario();

        Assert.Contains("Rua Exemplo, 123", await c.DizerAsync(Maria, "Qual o endereço?"));
        Assert.Contains("Rua Exemplo, 123", await c.DizerAsync(Maria, "onde fica vocês"));
    }

    [Fact]
    public async Task Opcao_3_lista_servicos_e_horarios()
    {
        var c = new Fixtures.Cenario();

        var resposta = await c.DizerAsync(Maria, "3");

        Assert.Contains("Corte (30 min) - R$ 50,00", resposta);
        Assert.Contains("Segunda: 09:00 às 18:00", resposta);
        Assert.Contains("Sábado: 09:00 às 13:00", resposta);
    }

    [Theory]
    [InlineData("32/13")]
    [InlineData("qualquer coisa")]
    [InlineData("20/11")] // já passou: seria só no ano que vem, longe demais
    public async Task Dia_invalido_ou_distante_pergunta_de_novo(string dia)
    {
        var c = new Fixtures.Cenario();
        await c.DizerAsync(Maria, "agendar");
        await c.DizerAsync(Maria, "1");

        Assert.Contains("Não entendi o dia", await c.DizerAsync(Maria, dia));
    }

    [Fact]
    public async Task Dia_sem_atendimento_avisa_e_deixa_tentar_outro()
    {
        var c = new Fixtures.Cenario();
        await c.DizerAsync(Maria, "agendar");
        await c.DizerAsync(Maria, "1");

        Assert.Contains("Não temos horários livres em 29/11", await c.DizerAsync(Maria, "29/11")); // domingo
        Assert.Contains("Horários livres em 28/11", await c.DizerAsync(Maria, "28/11")); // sábado
    }

    [Fact]
    public async Task Hoje_ja_nao_oferece_horarios_que_passaram()
    {
        var c = new Fixtures.Cenario();
        await c.DizerAsync(Maria, "agendar");
        await c.DizerAsync(Maria, "1");

        var resposta = await c.DizerAsync(Maria, "hoje");

        Assert.Contains("1) 10:30", resposta); // agora são 10:00
        Assert.DoesNotContain("09:00", resposta);
    }

    [Fact]
    public async Task Servico_inexistente_e_horario_fora_da_lista_repetem_a_pergunta()
    {
        var c = new Fixtures.Cenario();
        await c.DizerAsync(Maria, "agendar");
        Assert.Contains("Não encontrei esse serviço", await c.DizerAsync(Maria, "massagem"));
        await c.DizerAsync(Maria, "1");
        await c.DizerAsync(Maria, "amanhã");

        Assert.Contains("Escolha um dos horários", await c.DizerAsync(Maria, "99"));
    }

    [Fact]
    public async Task Mensagem_sem_texto_e_texto_sem_sentido_nao_quebram()
    {
        var c = new Fixtures.Cenario();

        Assert.Contains("só entendo texto", await c.DizerAsync(Maria, ""));
        Assert.Contains("Não entendi", await c.DizerAsync(Maria, "blablabla"));
    }

    [Fact]
    public async Task Menu_no_meio_do_fluxo_volta_ao_inicio()
    {
        var c = new Fixtures.Cenario();
        await c.DizerAsync(Maria, "agendar");

        Assert.Contains("1) Agendar um horário", await c.DizerAsync(Maria, "menu"));
        Assert.Contains("Qual serviço", await c.DizerAsync(Maria, "1"));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("09:00")]
    [InlineData("9h")]
    [InlineData("9h00")]
    public async Task Horario_pode_ser_escolhido_pelo_numero_ou_digitando_a_hora(string escolha)
    {
        var c = new Fixtures.Cenario();
        await c.DizerAsync(Maria, "agendar");
        await c.DizerAsync(Maria, "1");
        await c.DizerAsync(Maria, "amanhã");

        Assert.Contains("Confirma Corte em 24/11 às 09:00", await c.DizerAsync(Maria, escolha));
    }

    [Fact]
    public async Task Hora_que_nao_esta_na_lista_nao_e_aceita()
    {
        var c = new Fixtures.Cenario();
        await c.DizerAsync(Maria, "agendar");
        await c.DizerAsync(Maria, "1");
        await c.DizerAsync(Maria, "amanhã");

        Assert.Contains("Escolha um dos horários", await c.DizerAsync(Maria, "23h"));
        Assert.Contains("Escolha um dos horários", await c.DizerAsync(Maria, "25:99"));
    }
}
