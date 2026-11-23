using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Npgsql;
using Vertice.Atendimento.Api;
using Vertice.Atendimento.Core;
using Vertice.Atendimento.Infra;
using Vertice.Atendimento.Infra.Mensageria;
using Vertice.Atendimento.Infra.Postgres;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<MetaOptions>(config.GetSection(MetaOptions.Secao));
builder.Services.AddSingleton<MensagensJaProcessadas>();

// ---- negócios (clientes da Vértice) ----
var arquivoNegocios = Path.Combine(builder.Environment.ContentRootPath, config["Negocios:Arquivo"] ?? "negocios.json");
builder.Services.AddSingleton<INegocioRepositorio>(_ => NegocioRepositorioJson.DeArquivo(arquivoNegocios));

// ---- mensageria: Simulador (grátis, para testar) ou Meta (API oficial do WhatsApp) ----
var provedor = config["Mensageria:Provedor"] ?? "Simulador";
var simulador = string.Equals(provedor, "Simulador", StringComparison.OrdinalIgnoreCase);
if (simulador)
{
    builder.Services.AddSingleton<SimuladorMensageria>();
    builder.Services.AddSingleton<IMensageria>(sp => sp.GetRequiredService<SimuladorMensageria>());
}
else if (string.Equals(provedor, "Meta", StringComparison.OrdinalIgnoreCase))
{
    var meta = config.GetSection(MetaOptions.Secao).Get<MetaOptions>() ?? new MetaOptions();
    if (string.IsNullOrWhiteSpace(meta.AccessToken) || string.IsNullOrWhiteSpace(meta.AppSecret) || string.IsNullOrWhiteSpace(meta.VerifyToken))
        throw new InvalidOperationException("Mensageria:Provedor = Meta exige Meta:AccessToken, Meta:AppSecret e Meta:VerifyToken (use variáveis de ambiente).");
    builder.Services.AddHttpClient<IMensageria, MetaCloudMensageria>(c => c.Timeout = TimeSpan.FromSeconds(15));
}
else
{
    throw new InvalidOperationException($"Mensageria:Provedor desconhecido: {provedor}. Use Simulador ou Meta.");
}

// ---- armazenamento: Memoria (testes e demonstração) ou Postgres ----
var usaPostgres = string.Equals(config["Armazenamento:Tipo"], "Postgres", StringComparison.OrdinalIgnoreCase);
if (usaPostgres)
{
    var conexao = config.GetConnectionString("Postgres") ?? throw new InvalidOperationException("Falta ConnectionStrings:Postgres.");
    builder.Services.AddSingleton(NpgsqlDataSource.Create(conexao));
    builder.Services.AddSingleton<IConversaRepositorio, ConversaRepositorioPostgres>();
    builder.Services.AddSingleton<IAgendamentoRepositorio, AgendamentoRepositorioPostgres>();
}
else
{
    builder.Services.AddSingleton<IConversaRepositorio, ConversaRepositorioMemoria>();
    builder.Services.AddSingleton<IAgendamentoRepositorio, AgendamentoRepositorioMemoria>();
}

builder.Services.AddSingleton<MotorDeAtendimento>();
builder.Services.AddSingleton<ServicoDeLembretes>();
if (config.GetValue("Lembretes:Habilitado", true)) builder.Services.AddHostedService<LembretesWorker>();

var app = builder.Build();

if (usaPostgres)
    await SchemaPostgres.AplicarAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), Path.Combine(AppContext.BaseDirectory, "database", "schema.sql"), CancellationToken.None);

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/saude", () => Results.Ok(new { status = "ok" }));

// ---- webhook da Meta ----
// Cadastro do webhook: a Meta faz um GET com um desafio e espera o mesmo texto de volta, se o token bater.
app.MapGet("/webhook/whatsapp", (HttpContext http, IOptions<MetaOptions> opcoes) =>
{
    var q = http.Request.Query;
    var esperado = opcoes.Value.VerifyToken;
    var recebido = q["hub.verify_token"].ToString();
    var confere = q["hub.mode"] == "subscribe" && esperado.Length > 0
                  && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(esperado), Encoding.UTF8.GetBytes(recebido));
    return confere ? Results.Text(q["hub.challenge"].ToString()) : Results.StatusCode(StatusCodes.Status403Forbidden);
});

// Mensagens recebidas. A Meta reenvia se não receber 200 rápido, então respondemos 200 mesmo quando uma mensagem falha (e registramos).
app.MapPost("/webhook/whatsapp", async (HttpContext http, IOptions<MetaOptions> opcoes,
    INegocioRepositorio negocios, MotorDeAtendimento motor, MensagensJaProcessadas vistas, ILogger<Program> log, CancellationToken ct) =>
{
    using var memoria = new MemoryStream();
    await http.Request.Body.CopyToAsync(memoria, ct);
    var corpo = memoria.ToArray();

    if (!MetaWebhook.AssinaturaValida(opcoes.Value.AppSecret, corpo, http.Request.Headers["X-Hub-Signature-256"]))
        return Results.StatusCode(StatusCodes.Status401Unauthorized);

    IReadOnlyList<MensagemRecebida> mensagens;
    try { mensagens = MetaWebhook.Extrair(Encoding.UTF8.GetString(corpo)); }
    catch (System.Text.Json.JsonException) { return Results.BadRequest(); }

    foreach (var mensagem in mensagens)
    {
        if (mensagem.IdExterno is not null && !vistas.PrimeiraVez(mensagem.IdExterno)) continue; // reenvio da mesma mensagem
        try
        {
            var negocio = mensagem.PhoneNumberId is null ? null : await negocios.ObterPorPhoneNumberIdAsync(mensagem.PhoneNumberId, ct);
            if (negocio is null)
            {
                log.LogWarning("Mensagem para um número de WhatsApp sem negócio cadastrado ({PhoneNumberId}).", mensagem.PhoneNumberId);
                continue;
            }
            await motor.ProcessarAsync(negocio, mensagem, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError("Falha ao atender uma mensagem ({Tipo}).", ex.GetType().Name); // sem o texto: pode ter dado pessoal
        }
    }
    return Results.Ok();
});

// ---- simulador: só existe com Mensageria:Provedor = Simulador ----
if (simulador)
{
    app.MapGet("/", () => Results.Redirect("/simulador.html"));

    app.MapGet("/simulador/negocios", async (INegocioRepositorio negocios, CancellationToken ct) =>
        (await negocios.ListarAsync(ct)).Select(n => new { n.Id, n.Nome }));

    app.MapGet("/simulador/{negocioId}/{numero}/historico", (string negocioId, string numero, SimuladorMensageria sim) =>
        Results.Ok(sim.Historico(negocioId, numero)));

    app.MapPost("/simulador/{negocioId}/mensagens", async (string negocioId, EnviarSimulacao corpo, INegocioRepositorio negocios,
        MotorDeAtendimento motor, SimuladorMensageria sim, CancellationToken ct) =>
    {
        var negocio = await negocios.ObterAsync(negocioId, ct);
        if (negocio is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(corpo.Numero) || corpo.Numero.Length > 20) return Results.BadRequest(new { erro = "Informe o número." });

        sim.RegistrarDoCliente(negocio.Id, corpo.Numero, corpo.Texto ?? "");
        await motor.ProcessarAsync(negocio, new MensagemRecebida(corpo.Numero, corpo.Texto ?? ""), ct);
        return Results.Ok(sim.Historico(negocio.Id, corpo.Numero));
    });
}

app.Run();

namespace Vertice.Atendimento.Api
{
    public sealed record EnviarSimulacao(string? Numero, string? Texto);
}

public partial class Program;
