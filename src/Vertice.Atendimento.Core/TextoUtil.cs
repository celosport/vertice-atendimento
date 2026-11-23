using System.Globalization;
using System.Text;

namespace Vertice.Atendimento.Core;

public static class TextoUtil
{
    /// <summary>Minúsculas, sem acentos nem pontuação nas pontas e com espaços simples: "Olá!" vira "ola".</summary>
    public static string Normalizar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return "";
        var decomposto = texto.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposto.Length);
        foreach (var c in decomposto)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        var limpo = sb.ToString().Normalize(NormalizationForm.FormC).Trim(' ', '.', '!', '?', ',', ';', ':');
        return string.Join(' ', limpo.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static bool Contem(string normalizado, params string[] palavras) =>
        palavras.Any(p => ContemPalavra(normalizado, p));

    /// <summary>Procura a palavra (ou frase) inteira, para "pessoa" não casar com "pessoalmente".</summary>
    public static bool ContemPalavra(string normalizado, string palavra)
    {
        var p = Normalizar(palavra);
        if (p.Length == 0) return false;
        var texto = " " + normalizado + " ";
        return texto.Contains(" " + p + " ", StringComparison.Ordinal);
    }
}
