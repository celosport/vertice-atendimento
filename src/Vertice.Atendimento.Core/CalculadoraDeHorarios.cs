namespace Vertice.Atendimento.Core;

/// <summary>Quais horários de um dia ainda estão livres para um serviço. Função pura: não acessa banco nem relógio.</summary>
public static class CalculadoraDeHorarios
{
    public const int PassoPadraoMin = 30;

    public static TimeZoneInfo Fuso(Negocio negocio) => TimeZoneInfo.FindSystemTimeZoneById(negocio.Fuso);

    /// <summary>Instante (com o deslocamento correto do fuso do negócio) de uma data e hora locais.</summary>
    public static DateTimeOffset Instante(Negocio negocio, DateOnly dia, TimeOnly hora)
    {
        var local = DateTime.SpecifyKind(dia.ToDateTime(hora), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Fuso(negocio).GetUtcOffset(local));
    }

    public static IReadOnlyList<TimeOnly> Livres(
        Negocio negocio,
        Servico servico,
        DateOnly dia,
        IReadOnlyList<Agendamento> existentes,
        DateTimeOffset agora,
        int passoMin = PassoPadraoMin)
    {
        var livres = new List<TimeOnly>();
        foreach (var janela in negocio.Horarios.Where(j => j.Dia == dia.DayOfWeek).OrderBy(j => j.Abre))
        {
            var minutosAbre = janela.Abre.Hour * 60 + janela.Abre.Minute;
            var minutosFecha = janela.Fecha.Hour * 60 + janela.Fecha.Minute;
            for (var m = minutosAbre; m + servico.DuracaoMin <= minutosFecha; m += passoMin)
            {
                var hora = new TimeOnly(m / 60, m % 60);
                var inicio = Instante(negocio, dia, hora);
                if (inicio <= agora) continue;
                var fim = inicio.AddMinutes(servico.DuracaoMin);
                var ocupado = existentes.Any(e => e.Status == StatusAgendamento.Confirmado && e.Inicio < fim && inicio < e.Fim);
                if (!ocupado) livres.Add(hora);
            }
        }
        return livres;
    }
}
