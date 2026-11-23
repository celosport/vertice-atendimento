using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Vertice.Atendimento.Core;

namespace Vertice.Atendimento.Tests;

public sealed class ApiTests : IDisposable
{
    private const string VerifyToken = "token-de-verificacao";
    private readonly string _arquivoNegocios = Path.Combine(Path.GetTempPath(), $"negocios-{Guid.NewGuid():N}.json");

    public ApiTests() =>
        File.WriteAllText(_arquivoNegocios, """
            [{"id":"salao","nome":"Salão Teste","fuso":"America/Sao_Paulo","whatsAppPhoneNumberId":"1111",
              "servicos":[{"id":"corte","nome":"Corte","duracaoMin":30}],
              "horarios":[{"dia":"Monday","abre":"09:00:00","fecha":"18:00:00"}],
              "faq":[]}]
            """);

    public void Dispose() => File.Delete(_arquivoNegocios);

    private WebApplicationFactory<Program> Fabrica(Action<IServiceCollection>? servicos = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Negocios:Arquivo", _arquivoNegocios);
            b.UseSetting("Lembretes:Habilitado", "false");
            b.UseSetting("Meta:AppSecret", MetaWebhookTests.Segredo);
            b.UseSetting("Meta:VerifyToken", VerifyToken);
            if (servicos is not null) b.ConfigureTestServices(servicos);
        });

    private static HttpRequestMessage Webhook(string corpo, string? assinatura)
    {
        var pedido = new HttpRequestMessage(HttpMethod.Post, "/webhook/whatsapp") { Content = new StringContent(corpo, Encoding.UTF8, "application/json") };
        if (assinatura is not null) pedido.Headers.Add("X-Hub-Signature-256", assinatura);
        return pedido;
    }

    private sealed class MensageriaQueFalha : IMensageria
    {
        public Task EnviarTextoAsync(Negocio negocio, string numero, string texto, CancellationToken cancellationToken) => throw new HttpRequestException("falhou");
        public Task EnviarModeloAsync(Negocio negocio, string numero, string modelo, IReadOnlyList<string> parametros, string textoAlternativo, CancellationToken cancellationToken) => throw new HttpRequestException("falhou");
    }

    [Fact]
    public async Task Saude_responde_ok()
    {
        using var f = Fabrica();
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().GetAsync("/saude")).StatusCode);
    }

    [Fact]
    public async Task Simulador_atende_uma_conversa_inteira_sem_whatsapp_real()
    {
        using var f = Fabrica();
        var http = f.CreateClient();

        var negocios = await http.GetFromJsonAsync<JsonElement>("/simulador/negocios");
        Assert.Equal("salao", negocios[0].GetProperty("id").GetString());

        var resposta = await http.PostAsJsonAsync("/simulador/salao/mensagens", new { numero = "5521911112222", texto = "oi" });
        resposta.EnsureSuccessStatusCode();
        var historico = (await resposta.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();

        Assert.Equal("cliente", historico[0].GetProperty("de").GetString());
        Assert.Equal("oi", historico[0].GetProperty("texto").GetString());
        Assert.Equal("robo", historico[1].GetProperty("de").GetString());
        Assert.Contains("assistente do Salão Teste", historico[1].GetProperty("texto").GetString());
    }

    [Fact]
    public async Task Simulador_recusa_negocio_inexistente_e_numero_vazio()
    {
        using var f = Fabrica();
        var http = f.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/simulador/nao-existe/mensagens", new { numero = "5521", texto = "oi" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/simulador/salao/mensagens", new { numero = "", texto = "oi" })).StatusCode);
    }

    [Fact]
    public async Task Cadastro_do_webhook_so_devolve_o_desafio_com_o_token_certo()
    {
        using var f = Fabrica();
        var http = f.CreateClient();

        var certo = await http.GetAsync($"/webhook/whatsapp?hub.mode=subscribe&hub.verify_token={VerifyToken}&hub.challenge=12345");
        Assert.Equal(HttpStatusCode.OK, certo.StatusCode);
        Assert.Equal("12345", await certo.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/webhook/whatsapp?hub.mode=subscribe&hub.verify_token=errado&hub.challenge=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/webhook/whatsapp?hub.mode=subscribe&hub.challenge=1")).StatusCode);
    }

    [Fact]
    public async Task Webhook_sem_assinatura_valida_e_recusado_e_nao_atende()
    {
        using var f = Fabrica();
        var http = f.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Webhook(MetaWebhookTests.PayloadTexto, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Webhook(MetaWebhookTests.PayloadTexto, MetaWebhookTests.Assinar(MetaWebhookTests.PayloadTexto, "outro")))).StatusCode);

        var historico = await http.GetFromJsonAsync<JsonElement>("/simulador/salao/5521911112222/historico");
        Assert.Equal(0, historico.GetArrayLength());
    }

    [Fact]
    public async Task Webhook_assinado_atende_a_mensagem_e_ignora_o_reenvio_da_mesma()
    {
        using var f = Fabrica();
        var http = f.CreateClient();
        var assinatura = MetaWebhookTests.Assinar(MetaWebhookTests.PayloadTexto);

        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Webhook(MetaWebhookTests.PayloadTexto, assinatura))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Webhook(MetaWebhookTests.PayloadTexto, assinatura))).StatusCode); // a Meta reenviou

        var historico = await http.GetFromJsonAsync<JsonElement>("/simulador/salao/5521911112222/historico");
        var linhas = historico.EnumerateArray().ToList();
        Assert.Single(linhas); // uma resposta do robô, só (o simulador só registra o que o robô enviou neste caminho)
        Assert.Contains("assistente do Salão Teste", linhas[0].GetProperty("texto").GetString());
    }

    [Fact]
    public async Task Webhook_para_numero_sem_negocio_ou_com_json_quebrado_nao_derruba()
    {
        using var f = Fabrica();
        var http = f.CreateClient();
        var outroNumero = MetaWebhookTests.PayloadTexto.Replace("\"phone_number_id\":\"1111\"", "\"phone_number_id\":\"9999\"");
        const string quebrado = "{ isto não é json";

        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Webhook(outroNumero, MetaWebhookTests.Assinar(outroNumero)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(Webhook(quebrado, MetaWebhookTests.Assinar(quebrado)))).StatusCode);
    }

    [Fact]
    public async Task Falha_ao_responder_nao_vira_erro_para_a_Meta_para_ela_nao_reenviar()
    {
        using var f = Fabrica(s => s.AddSingleton<IMensageria, MensageriaQueFalha>());
        var http = f.CreateClient();

        var resposta = await http.SendAsync(Webhook(MetaWebhookTests.PayloadTexto, MetaWebhookTests.Assinar(MetaWebhookTests.PayloadTexto)));

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }
}
