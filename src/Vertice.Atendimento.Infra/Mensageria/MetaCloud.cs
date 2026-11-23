using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vertice.Atendimento.Core;

namespace Vertice.Atendimento.Infra.Mensageria;

public sealed class MetaOptions
{
    public const string Secao = "Meta";

    /// <summary>Versão da Graph API. Confira a versão vigente na documentação da Meta antes de ir para produção.</summary>
    public string VersaoApi { get; set; } = "v21.0";

    /// <summary>Token de acesso (de usuário do sistema). SEGREDO: só por variável de ambiente ou user-secrets, nunca no Git.</summary>
    public string AccessToken { get; set; } = "";

    /// <summary>Texto que você inventa e cola no painel da Meta ao cadastrar o webhook.</summary>
    public string VerifyToken { get; set; } = "";

    /// <summary>Segredo do app na Meta; usado para conferir a assinatura de cada webhook. SEGREDO.</summary>
    public string AppSecret { get; set; } = "";

    public string IdiomaModelos { get; set; } = "pt_BR";
}

/// <summary>Envio pela API oficial do WhatsApp (Cloud API da Meta). Cada negócio usa o seu <c>WhatsAppPhoneNumberId</c>.</summary>
public sealed class MetaCloudMensageria(HttpClient http, IOptions<MetaOptions> opcoes, ILogger<MetaCloudMensageria> log) : IMensageria
{
    public Task EnviarTextoAsync(Negocio negocio, string numero, string texto, CancellationToken cancellationToken) =>
        EnviarAsync(negocio, new
        {
            messaging_product = "whatsapp",
            to = numero,
            type = "text",
            text = new { body = texto },
        }, cancellationToken);

    public Task EnviarModeloAsync(Negocio negocio, string numero, string modelo, IReadOnlyList<string> parametros, string textoAlternativo, CancellationToken cancellationToken) =>
        EnviarAsync(negocio, new
        {
            messaging_product = "whatsapp",
            to = numero,
            type = "template",
            template = new
            {
                name = modelo,
                language = new { code = opcoes.Value.IdiomaModelos },
                components = new[]
                {
                    new { type = "body", parameters = parametros.Select(p => new { type = "text", text = p }).ToArray() },
                },
            },
        }, cancellationToken);

    private async Task EnviarAsync(Negocio negocio, object corpo, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(negocio.WhatsAppPhoneNumberId))
            throw new InvalidOperationException($"O negócio {negocio.Id} não tem WhatsAppPhoneNumberId.");

        var o = opcoes.Value;
        using var pedido = new HttpRequestMessage(HttpMethod.Post, $"https://graph.facebook.com/{o.VersaoApi}/{negocio.WhatsAppPhoneNumberId}/messages")
        {
            Content = JsonContent.Create(corpo),
        };
        pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.AccessToken);

        using var resposta = await http.SendAsync(pedido, cancellationToken);
        if (!resposta.IsSuccessStatusCode)
        {
            // Só o código: o corpo pode ter o número do cliente. Nunca registrar o token.
            log.LogWarning("Meta recusou o envio ({Status}) para o negócio {Negocio}.", (int)resposta.StatusCode, negocio.Id);
            throw new HttpRequestException($"A Meta recusou o envio ({(int)resposta.StatusCode}).");
        }
    }
}

/// <summary>Lê o que a Meta manda no webhook e confere a assinatura. Funções puras: testáveis sem rede.</summary>
public static class MetaWebhook
{
    /// <summary>Extrai só as mensagens de texto recebidas (ignora status de entrega, imagens, áudios etc.).</summary>
    public static IReadOnlyList<MensagemRecebida> Extrair(string json)
    {
        var resultado = new List<MensagemRecebida>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("entry", out var entradas)) return resultado;

        foreach (var entrada in entradas.EnumerateArray())
        {
            if (!entrada.TryGetProperty("changes", out var mudancas)) continue;
            foreach (var mudanca in mudancas.EnumerateArray())
            {
                if (!mudanca.TryGetProperty("value", out var valor)) continue;
                var phoneId = valor.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("phone_number_id", out var pid) ? pid.GetString() : null;
                if (!valor.TryGetProperty("messages", out var mensagens)) continue;

                foreach (var m in mensagens.EnumerateArray())
                {
                    if (m.TryGetProperty("type", out var tipo) && tipo.GetString() != "text") continue;
                    var de = m.TryGetProperty("from", out var f) ? f.GetString() : null;
                    var corpo = m.TryGetProperty("text", out var t) && t.TryGetProperty("body", out var b) ? b.GetString() : null;
                    if (string.IsNullOrEmpty(de) || corpo is null) continue;
                    var id = m.TryGetProperty("id", out var i) ? i.GetString() : null;
                    resultado.Add(new MensagemRecebida(de, corpo, phoneId, id));
                }
            }
        }
        return resultado;
    }

    /// <summary>Confere o cabeçalho <c>X-Hub-Signature-256</c> ("sha256=" + HMAC-SHA256 do corpo com o segredo do app).</summary>
    public static bool AssinaturaValida(string appSecret, byte[] corpo, string? cabecalho)
    {
        if (string.IsNullOrEmpty(appSecret) || string.IsNullOrEmpty(cabecalho) || !cabecalho.StartsWith("sha256=", StringComparison.Ordinal)) return false;
        byte[] enviado;
        try { enviado = Convert.FromHexString(cabecalho["sha256=".Length..]); }
        catch (FormatException) { return false; }

        var esperado = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), corpo);
        return CryptographicOperations.FixedTimeEquals(esperado, enviado);
    }
}
