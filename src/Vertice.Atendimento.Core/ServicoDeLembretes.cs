namespace Vertice.Atendimento.Core;

/// <summary>
/// Manda o lembrete dos agendamentos das próximas horas, uma vez só por agendamento. Quem chama (um worker) decide a frequência.
/// O lembrete sai como mensagem modelo (<see cref="Modelo"/>), a única que o WhatsApp permite fora da janela de 24 h.
/// </summary>
public sealed class ServicoDeLembretes(
    IAgendamentoRepositorio agenda,
    INegocioRepositorio negocios,
    IMensageria mensageria,
    TimeProvider relogio)
{
    /// <summary>Nome do modelo que precisa estar aprovado na conta do WhatsApp do cliente: "Olá! Lembrete: {{1}} em {{2}} às {{3}}."</summary>
    public const string Modelo = "lembrete_agendamento";

    public static readonly TimeSpan Antecedencia = TimeSpan.FromHours(24);

    /// <returns>Quantos lembretes foram enviados.</returns>
    public async Task<int> ExecutarAsync(CancellationToken cancellationToken)
    {
        var agora = relogio.GetUtcNow();
        var pendentes = await agenda.ListarParaLembreteAsync(agora, agora + Antecedencia, cancellationToken);
        var enviados = 0;

        foreach (var a in pendentes)
        {
            var negocio = await negocios.ObterAsync(a.NegocioId, cancellationToken);
            if (negocio is null) continue;

            var local = TimeZoneInfo.ConvertTime(a.Inicio, CalculadoraDeHorarios.Fuso(negocio));
            var servico = negocio.Servicos.FirstOrDefault(s => s.Id == a.ServicoId)?.Nome ?? "seu horário";
            var data = local.ToString("dd/MM", System.Globalization.CultureInfo.InvariantCulture);
            var hora = local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

            await mensageria.EnviarModeloAsync(negocio, a.Numero, Modelo, [servico, data, hora],
                $"Olá! Lembrete: {servico} em {data} às {hora}. Para cancelar, responda *cancelar*.", cancellationToken);
            // Marca só depois de enviar: se o envio falhar, o próximo ciclo tenta de novo
            await agenda.MarcarLembreteEnviadoAsync(a.Id, cancellationToken);
            enviados++;
        }
        return enviados;
    }
}
