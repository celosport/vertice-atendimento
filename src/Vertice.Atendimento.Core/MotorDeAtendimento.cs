using System.Globalization;
using System.Text.RegularExpressions;

namespace Vertice.Atendimento.Core;

/// <summary>
/// O robô de atendimento: lê a mensagem do cliente, decide a resposta pelo ponto em que a conversa parou (menu, agendar, cancelar,
/// perguntas frequentes, falar com uma pessoa) e responde pelo <see cref="IMensageria"/>.
/// É determinístico (sem IA): fácil de testar e de explicar ao cliente. A IA entra depois, só para entender frases livres.
/// </summary>
public sealed partial class MotorDeAtendimento(
    IConversaRepositorio conversas,
    IAgendamentoRepositorio agenda,
    IMensageria mensageria,
    TimeProvider relogio)
{
    private const int MaximoDeHorariosMostrados = 8;

    /// <summary>Só dá para marcar até 90 dias à frente (evita datas digitadas errado, como um dia que já passou).</summary>
    private const int DiasDeAgendaAberta = 90;

    private static readonly string[] PalavrasMenu = ["oi", "ola", "oie", "opa", "menu", "inicio", "voltar", "bom dia", "boa tarde", "boa noite"];
    private static readonly string[] PalavrasHumano = ["atendente", "humano", "pessoa", "gerente", "responsavel"];
    private static readonly string[] PalavrasAgendar = ["agendar", "agendamento", "marcar", "reservar"];
    private static readonly string[] PalavrasCancelar = ["cancelar", "desmarcar"];
    private static readonly string[] PalavrasSim = ["sim", "s", "confirmo", "confirmar", "ok", "pode"];
    private static readonly string[] PalavrasNao = ["nao", "n", "negativo"];

    private sealed record Resultado(IReadOnlyList<string> Respostas, EstadoConversa Novo, string? AvisarResponsavel = null);

    public async Task ProcessarAsync(Negocio negocio, MensagemRecebida mensagem, CancellationToken cancellationToken)
    {
        var texto = TextoUtil.Normalizar(mensagem.Texto);
        var estado = await conversas.ObterAsync(negocio.Id, mensagem.Numero, cancellationToken);
        var resultado = await DecidirAsync(negocio, mensagem.Numero, texto, estado, cancellationToken);

        foreach (var resposta in resultado.Respostas)
            await mensageria.EnviarTextoAsync(negocio, mensagem.Numero, resposta, cancellationToken);
        if (resultado.AvisarResponsavel is { } aviso && negocio.NumeroDoResponsavel is { } responsavel)
            await mensageria.EnviarTextoAsync(negocio, responsavel, aviso, cancellationToken);

        await conversas.SalvarAsync(negocio.Id, mensagem.Numero, resultado.Novo, cancellationToken);
    }

    private async Task<Resultado> DecidirAsync(Negocio n, string numero, string t, EstadoConversa estado, CancellationToken ct)
    {
        // Com uma pessoa atendendo, o robô fica em silêncio até o cliente pedir o menu
        if (estado.Passo == Passo.Humano)
            return TextoUtil.Contem(t, "menu", "voltar", "bot", "robo") ? Menu(n) : new Resultado([], estado);

        if (t.Length == 0)
            return new Resultado(["Por enquanto eu só entendo texto. Responda *menu* para ver as opções."], estado);

        if (TextoUtil.Contem(t, PalavrasHumano) || (estado.Passo == Passo.Inicio && t == "4"))
            return Humano(n, numero);
        if (TextoUtil.Contem(t, PalavrasMenu) && estado.Passo != Passo.Confirmando && estado.Passo != Passo.ConfirmandoCancelamento)
            return Menu(n);

        return estado.Passo switch
        {
            Passo.Inicio => await InicioAsync(n, numero, t, ct),
            Passo.EscolhendoServico => EscolhendoServico(n, t, estado),
            Passo.EscolhendoDia => await EscolhendoDiaAsync(n, t, estado, ct),
            Passo.EscolhendoHorario => EscolhendoHorario(n, t, estado),
            Passo.Confirmando => await ConfirmandoAsync(n, numero, t, estado, ct),
            Passo.ConfirmandoCancelamento => await ConfirmandoCancelamentoAsync(n, numero, t, ct),
            _ => Menu(n),
        };
    }

    // ---- menu e perguntas ----

    private static Resultado Menu(Negocio n) => new(
        [$"Olá! Sou o assistente do {n.Nome}. Como posso ajudar?\n\n1) Agendar um horário\n2) Cancelar meu agendamento\n3) Serviços e horários de atendimento\n4) Falar com uma pessoa\n\nÉ só responder com o número."],
        new EstadoConversa());

    private static Resultado Humano(Negocio n, string numero) => new(
        ["Certo, vou chamar uma pessoa da equipe para continuar a conversa. Para voltar ao assistente, responda *menu*."],
        new EstadoConversa(Passo.Humano),
        $"O cliente {numero} pediu para falar com uma pessoa no atendimento de {n.Nome}.");

    private async Task<Resultado> InicioAsync(Negocio n, string numero, string t, CancellationToken ct)
    {
        if (t == "1" || TextoUtil.Contem(t, PalavrasAgendar)) return ListarServicos(n);
        if (t == "2" || TextoUtil.Contem(t, PalavrasCancelar)) return await InicioCancelamentoAsync(n, numero, ct);
        if (t == "3") return Informacoes(n);
        var faq = n.Faq.FirstOrDefault(f => f.Palavras.Any(p => TextoUtil.ContemPalavra(t, p)));
        if (faq is not null) return new Resultado([faq.Resposta], new EstadoConversa());
        return new Resultado(["Não entendi. Responda com o número da opção ou *menu* para ver de novo."], new EstadoConversa());
    }

    private static Resultado Informacoes(Negocio n)
    {
        var servicos = string.Join("\n", n.Servicos.Select(s => $"- {s.Nome} ({s.DuracaoMin} min){Preco(s)}"));
        var horarios = string.Join("\n", n.Horarios.OrderBy(h => OrdemDia(h.Dia)).Select(h => $"- {NomeDia(h.Dia)}: {h.Abre:HH\\:mm} às {h.Fecha:HH\\:mm}"));
        return new Resultado([$"Nossos serviços:\n{servicos}\n\nHorários de atendimento:\n{horarios}\n\nPara marcar, responda *agendar*."], new EstadoConversa());
    }

    // ---- agendar ----

    private static Resultado ListarServicos(Negocio n)
    {
        var lista = string.Join("\n", n.Servicos.Select((s, i) => $"{i + 1}) {s.Nome} ({s.DuracaoMin} min){Preco(s)}"));
        return new Resultado([$"Qual serviço você quer?\n\n{lista}\n\nResponda com o número. Para voltar, responda *menu*."], new EstadoConversa(Passo.EscolhendoServico));
    }

    private static Resultado EscolhendoServico(Negocio n, string t, EstadoConversa estado)
    {
        var servico = EscolherServico(n, t);
        if (servico is null)
            return new Resultado(["Não encontrei esse serviço. Responda com o número da lista ou *menu* para voltar."], estado);
        return new Resultado(
            [$"Ótimo, {servico.Nome}. Para qual dia? Responda *hoje*, *amanhã* ou a data (ex.: 25/11)."],
            new EstadoConversa(Passo.EscolhendoDia, servico.Id));
    }

    private async Task<Resultado> EscolhendoDiaAsync(Negocio n, string t, EstadoConversa estado, CancellationToken ct)
    {
        var servico = n.Servicos.FirstOrDefault(s => s.Id == estado.ServicoId);
        if (servico is null) return Menu(n);

        var agora = relogio.GetUtcNow();
        var hoje = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(agora, CalculadoraDeHorarios.Fuso(n)).DateTime);
        if (!TentarLerDia(t, hoje, out var dia))
            return new Resultado(["Não entendi o dia (ou ele está distante demais). Responda *hoje*, *amanhã* ou a data no formato 25/11."], estado);

        var inicioDia = CalculadoraDeHorarios.Instante(n, dia, TimeOnly.MinValue);
        var existentes = await agenda.ListarConfirmadosAsync(n.Id, inicioDia, inicioDia.AddDays(1), ct);
        var livres = CalculadoraDeHorarios.Livres(n, servico, dia, existentes, agora).Take(MaximoDeHorariosMostrados).ToList();
        if (livres.Count == 0)
            return new Resultado([$"Não temos horários livres em {dia:dd/MM}. Tente outro dia (hoje, amanhã ou uma data)."], estado);

        var lista = string.Join("\n", livres.Select((h, i) => $"{i + 1}) {h:HH\\:mm}"));
        return new Resultado(
            [$"Horários livres em {dia:dd/MM}:\n\n{lista}\n\nResponda com o número do horário."],
            new EstadoConversa(Passo.EscolhendoHorario, servico.Id, dia, livres));
    }

    private Resultado EscolhendoHorario(Negocio n, string t, EstadoConversa estado)
    {
        var opcoes = estado.Opcoes ?? [];
        TimeOnly? escolhido = null;
        if (int.TryParse(t, out var indice) && indice >= 1 && indice <= opcoes.Count) escolhido = opcoes[indice - 1];
        else if (TentarLerHora(t, out var digitado) && opcoes.Contains(digitado)) escolhido = digitado;

        if (escolhido is null)
            return new Resultado(["Escolha um dos horários da lista, respondendo com o número."], estado);

        var servico = n.Servicos.First(s => s.Id == estado.ServicoId);
        return new Resultado(
            [$"Confirma {servico.Nome} em {estado.Dia:dd/MM} às {escolhido:HH\\:mm}? Responda *sim* ou *não*."],
            estado with { Passo = Passo.Confirmando, Opcoes = [escolhido.Value] });
    }

    private async Task<Resultado> ConfirmandoAsync(Negocio n, string numero, string t, EstadoConversa estado, CancellationToken ct)
    {
        if (TextoUtil.Contem(t, PalavrasNao)) return new Resultado(["Tudo bem, não agendei. Responda *menu* para ver as opções."], new EstadoConversa());
        if (!TextoUtil.Contem(t, PalavrasSim)) return new Resultado(["Responda *sim* para confirmar ou *não* para desistir."], estado);

        var servico = n.Servicos.First(s => s.Id == estado.ServicoId);
        var hora = estado.Opcoes![0];
        var inicio = CalculadoraDeHorarios.Instante(n, estado.Dia!.Value, hora);
        var agendamento = new Agendamento(Guid.NewGuid(), n.Id, numero, servico.Id, inicio, inicio.AddMinutes(servico.DuracaoMin));

        if (!await agenda.TentarCriarAsync(agendamento, ct))
            return new Resultado(
                ["Esse horário acabou de ser ocupado por outra pessoa. Vamos escolher outro: para qual dia? (*hoje*, *amanhã* ou uma data)"],
                new EstadoConversa(Passo.EscolhendoDia, servico.Id));

        return new Resultado(
            [$"Agendado! {servico.Nome} em {estado.Dia:dd/MM} às {hora:HH\\:mm}. Vou te lembrar antes do horário. Para cancelar, responda *cancelar*."],
            new EstadoConversa());
    }

    // ---- cancelar ----

    private async Task<Resultado> InicioCancelamentoAsync(Negocio n, string numero, CancellationToken ct)
    {
        var proximo = await agenda.ProximoDoClienteAsync(n.Id, numero, relogio.GetUtcNow(), ct);
        if (proximo is null)
            return new Resultado(["Você não tem horário marcado. Para agendar, responda *agendar*."], new EstadoConversa());

        var local = TimeZoneInfo.ConvertTime(proximo.Inicio, CalculadoraDeHorarios.Fuso(n));
        var nomeServico = n.Servicos.FirstOrDefault(sv => sv.Id == proximo.ServicoId)?.Nome ?? "Seu horário";
        return new Resultado(
            [$"Seu próximo horário é {nomeServico} em {local:dd/MM} às {local:HH\\:mm}. Quer cancelar? Responda *sim* ou *não*."],
            new EstadoConversa(Passo.ConfirmandoCancelamento));
    }

    private async Task<Resultado> ConfirmandoCancelamentoAsync(Negocio n, string numero, string t, CancellationToken ct)
    {
        var proximo = await agenda.ProximoDoClienteAsync(n.Id, numero, relogio.GetUtcNow(), ct);
        if (proximo is null)
            return new Resultado(["Você não tem horário marcado. Para agendar, responda *agendar*."], new EstadoConversa());

        var local = TimeZoneInfo.ConvertTime(proximo.Inicio, CalculadoraDeHorarios.Fuso(n));
        var nomeServico = n.Servicos.FirstOrDefault(s => s.Id == proximo.ServicoId)?.Nome ?? "Seu horário";

        if (TextoUtil.Contem(t, PalavrasSim))
        {
            await agenda.CancelarAsync(proximo.Id, ct);
            return new Resultado([$"Cancelado: {nomeServico} em {local:dd/MM} às {local:HH\\:mm}. Para marcar de novo, responda *agendar*."], new EstadoConversa());
        }
        if (TextoUtil.Contem(t, PalavrasNao))
            return new Resultado(["Tudo bem, mantive o seu horário."], new EstadoConversa());

        return new Resultado(
            [$"Seu próximo horário é {nomeServico} em {local:dd/MM} às {local:HH\\:mm}. Quer cancelar? Responda *sim* ou *não*."],
            new EstadoConversa(Passo.ConfirmandoCancelamento));
    }

    // ---- utilidades ----

    /// <summary>Aceita "10:30", "10h", "10h30" ou "10:30" digitados no lugar do número da lista.</summary>
    internal static bool TentarLerHora(string t, out TimeOnly hora)
    {
        hora = default;
        var m = HoraRegex().Match(t);
        if (!m.Success) return false;
        var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        if (h > 23 || min > 59) return false;
        hora = new TimeOnly(h, min);
        return true;
    }

    private static Servico? EscolherServico(Negocio n, string t)
    {
        if (int.TryParse(t, out var i) && i >= 1 && i <= n.Servicos.Count) return n.Servicos[i - 1];
        return n.Servicos.FirstOrDefault(s => TextoUtil.ContemPalavra(t, s.Nome));
    }

    internal static bool TentarLerDia(string t, DateOnly hoje, out DateOnly dia)
    {
        dia = default;
        if (t == "hoje") { dia = hoje; return true; }
        if (t == "amanha") { dia = hoje.AddDays(1); return true; }
        if (t == "depois de amanha") { dia = hoje.AddDays(2); return true; }

        var m = DataRegex().Match(t);
        if (!m.Success) return false;
        var d = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var mes = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var temAno = m.Groups[3].Success;
        var ano = temAno ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : hoje.Year;
        if (temAno && ano < 100) ano += 2000;
        if (mes is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(ano, mes)) return false;

        var candidato = new DateOnly(ano, mes, d);
        if (!temAno && candidato < hoje) candidato = candidato.AddYears(1); // "10/01" dito em dezembro é janeiro do ano que vem
        if (candidato < hoje || candidato > hoje.AddDays(DiasDeAgendaAberta)) return false;
        dia = candidato;
        return true;
    }

    private static string Preco(Servico s) => s.Preco is { } p ? $" - R$ {p.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',')}" : "";

    private static int OrdemDia(DayOfWeek d) => d == DayOfWeek.Sunday ? 7 : (int)d;

    private static string NomeDia(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "Segunda",
        DayOfWeek.Tuesday => "Terça",
        DayOfWeek.Wednesday => "Quarta",
        DayOfWeek.Thursday => "Quinta",
        DayOfWeek.Friday => "Sexta",
        DayOfWeek.Saturday => "Sábado",
        _ => "Domingo",
    };

    [GeneratedRegex(@"^(\d{1,2})[/\-.](\d{1,2})(?:[/\-.](\d{2,4}))?$")]
    private static partial Regex DataRegex();

    [GeneratedRegex(@"^(\d{1,2})\s*(?:h|:)\s*(\d{2})?$")]
    private static partial Regex HoraRegex();
}
