/**
 * Lee un cuerpo de petición que debe ser un arreglo JSON, acotado antes de leer y después. El límite se
 * comprueba primero con `content-length` (aviso temprano, el header puede mentir o faltar) y luego con
 * los bytes reales ya leídos. Devuelve `null` cuando el cuerpo no es un arreglo: la forma inválida no
 * puede leerse como "arreglo vacío".
 *
 * Vive aquí y no dentro de cada route handler porque la importación de consola tiene dos puertas
 * (`preview` y `commit`) con el mismo contrato de entrada.
 */
export async function readJsonArrayBody(request: Request, maxBytes: number): Promise<unknown[] | null> {
  const declaredLength = Number(request.headers.get("content-length") ?? "");
  if (Number.isFinite(declaredLength) && declaredLength > maxBytes) {
    return null;
  }

  const raw = await request.text();
  if (new TextEncoder().encode(raw).length > maxBytes) {
    return null;
  }

  let payload: unknown;
  try {
    payload = JSON.parse(raw);
  } catch {
    return null;
  }

  return Array.isArray(payload) ? payload : null;
}
