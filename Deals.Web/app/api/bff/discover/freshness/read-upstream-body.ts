// Lectura guardada del cuerpo de una respuesta upstream en el BFF de actualización de precios.
//
// Un 2xx cuyo cuerpo no es JSON —el HTML de un proxy, un cuerpo truncado, un stream cortado a mitad de la
// respuesta— no puede convertirse en una excepción sin tipo: en una ruta de Next eso termina en un 500
// genérico que además descarta la cookie de sesión que el auto-refresh acaba de rotar. Por eso el camino
// exitoso lee el cuerpo por aquí y trata el fallo como un error upstream tipado (`UPSTREAM_ERROR`) con su
// `traceId`, usando los mismos helpers que el resto del BFF.
//
// La distinción importa: un cuerpo ilegible es un fallo de TRANSPORTE (error), mientras que un JSON legible
// pero vacío o incompleto es un cuerpo leído que `normalizeCatalogFreshnessPage` degrada a página vacía. El
// panel no debe decir «no hay juegos» cuando en realidad no pudo leer la respuesta.
export type UpstreamJsonRead =
  | { readonly ok: true; readonly body: unknown }
  | { readonly ok: false };

export async function readUpstreamJson(response: { json: () => Promise<unknown> }): Promise<UpstreamJsonRead> {
  try {
    return { ok: true, body: await response.json() };
  } catch {
    return { ok: false };
  }
}
