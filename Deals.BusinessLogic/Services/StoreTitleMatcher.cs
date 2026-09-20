using System.Globalization;
using System.Text;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Comparación de títulos, para el único camino donde la identidad no se puede leer de un enlace: cuando la
/// tienda no anunció el juego a ITAD y hay que preguntarle a su buscador. El guard es el más fuerte que ese
/// camino permite —igualdad exacta después de normalizar— porque el error no es simétrico: un no-match
/// cuesta un precio que no se muestra, y un falso positivo escribe **el precio de otro juego**.
///
/// No reutiliza <see cref="GameTitleNormalizer"/> por dos motivos, y ninguno es de estilo:
/// <list type="bullet">
/// <item>Ese normalizador quita los tokens de edición finales (<c>deluxe</c>, <c>goty</c>, <c>complete</c>…),
/// que es justo la diferencia entre dos productos que este guard tiene que poder distinguir. Bajo esa
/// normalización <c>Foo</c> y <c>Foo Deluxe Edition</c> son la misma cadena.</item>
/// <item>Su contrato dice que ninguna decisión de identidad puede depender de él.</item>
/// </list>
///
/// Aquí solo se quita lo que nunca distingue nada: mayúsculas, tildes, símbolos de marca y espaciado. Las
/// letras y los dígitos no ASCII se conservan (un título CJK no se convierte en cadena vacía).
/// </summary>
public static class StoreTitleMatcher
{
    /// <summary>
    /// ¿Estos dos títulos son el mismo producto? La comparación se hace sobre las dos partes normalizadas, y
    /// una cadena vacía nunca casa: un título ausente o ilegible es una identidad que falta, no una coincidencia.
    /// </summary>
    public static bool Matches(string? candidate, string? searched)
    {
        var normalizedCandidate = Normalize(candidate);
        return normalizedCandidate.Length > 0 && normalizedCandidate == Normalize(searched);
    }

    public static string Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        // FormD separa "á" en "a" + marca combinante, así que quitar NonSpacingMark quita la tilde.
        var decomposed = title.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            // Los símbolos de marca se caen sin dejar hueco: "OCTOPATH TRAVELER™" y "OCTOPATH TRAVELER" son
            // el mismo producto, y la tienda los usa de forma inconsistente entre el catálogo y el buscador.
            if (character is '™' or '®' or '©' or '\u2120')
            {
                continue;
            }

            // Los apóstrofos también se caen sin dejar hueco, y por un motivo distinto: convertirlos en espacio
            // parte la palabra ("assassin s creed") y deja de casar con la grafía sin apóstrofo del otro lado
            // ("assassins creed"), que es la misma obra. Detectado por el check de `StoreTitleMatcher`.
            if (character is '\'' or '\u2019' or '\u2018' or '\u02BC' or '`' or '\u00B4')
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
