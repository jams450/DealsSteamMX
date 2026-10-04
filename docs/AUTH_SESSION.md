# Renovación de sesión: menos rotaciones concurrentes

El BFF comparte una renovación entre llamadas automáticas y `/api/auth/refresh` manual. Mitiga el rechazo de llamadas concurrentes que intentaban rotar el mismo refresh token. No garantiza eliminar todo inicio de sesión forzado ni certifica la causa en producción.

## Defaults declarados, no estado de producción

| Configuración Compose API | Default | Efecto |
|---|---|---|
| `Jwt__ExpirationHours` | 2 horas | Duración del access token nuevo |
| `Auth__RefreshDays` | 90 días | Duración del refresh token nuevo |

Ambos admiten override de entorno. Los valores efectivos del servidor y el despliegue de este cambio no se verificaron. Cambiar configuración requiere recrear el contenedor API; no basta reiniciarlo. Las expiraciones ya emitidas no se amplían: un nuevo login obtiene los nuevos plazos. No se ejecutó despliegue ni se inspeccionaron secretos.

## Alcance del coordinador

- Un solo proceso Next.js: memoria local, no coordinación entre réplicas, workers o reinicios. Antes de escalar hace falta coordinación compartida; afinidad por sí sola no certifica seguridad.
- Clave SHA-256 del refresh token; sin logs de tokens ni digests. El resultado contiene tokens únicamente en memoria.
- Hasta 256 entradas entre renovaciones pendientes y resultados exitosos. Ventana de éxito de 5 segundos desde finalización. La limpieza ocurre al acceder, no mediante timer.
- Errores no se cachean. Se pueden expulsar resultados completados por capacidad; nunca trabajo pendiente. Saturación de pendientes responde 503, no rechazo de autenticación.
- Timeout propio de 30 segundos para la renovación compartida; abortar un consumidor no cancela la rotación de otros. No hay bucle de reintentos.
- Un consumidor lento con cookie anterior que llega después de 5 segundos, o tras expulsión del resultado, aún puede ser rechazado por el backend. Es una mitigación acotada, no garantía universal.

Backend mantiene rotación atómica estricta; middleware, CSRF y controles 401 permanecen intactos. Server components leen la sesión; no la renuevan. Los callers existentes siguen recibiendo la sesión actualizada y adjuntan la cookie. El proxy conserva sesión/cookie incluso cuando el cuerpo posterior a la renovación no es JSON válido.

## Diagnóstico y límites pendientes

El evento `[bff.auth.refresh_upstream_failed]` informa solo `status`: distinguir 401 (token rechazado), 403 (denegación; investigar origen) y 5xx (fallo de servicio). No registrar cuerpos, cookies, tokens ni claves. Un fallo upstream sigue devolviendo la política existente de rechazo; esta unidad no rediseña errores transitorios.

Persisten rutas fuera del alcance:
- `Deals.Web/app/api/bff/users/route.ts`: parseo JSON no protegido después de renovar puede perder la cookie.
- `Deals.Web/lib/auth/api-session.ts`: si el retry de la llamada original lanza por red/abort después de rotar, no entrega la sesión actualizada al caller.

Cloudflare Access tiene una sesión independiente: su vencimiento puede pedir autenticación aunque la sesión de la app siga vigente. Access protege el hostname/ruta configurado, no automáticamente el acceso directo al puerto. El acceso LAN/directo depende de binding, firewall y controles propios de la app; este cambio no verifica ni modifica esa exposición.

## Checklist de despliegue acotado (operador)

- [ ] Confirmar un único proceso BFF; no usar esta mitigación como coordinación distribuida.
- [ ] Revisar overrides de duración sin divulgar sus valores sensibles.
- [ ] Publicar el código BFF y recrear API con la configuración declarada, en una intervención autorizada separada.
- [ ] Iniciar sesión nueva para comprobar expiraciones nuevas; no esperar extensión retroactiva.
- [ ] Probar llamadas concurrentes y refresh manual, verificando cookie rotada en respuestas normales y error de parseo del proxy.
- [ ] Distinguir login de la app frente a Cloudflare Access y revisar exposición directa al puerto.

Pruebas locales: `node --test Deals.Web/tests/auth-refresh.test.mjs`. Pruebas de comportamiento cubren coordinación pura; los enlaces manual/automático y la rama del proxy tienen comprobaciones estructurales, no integración Next.js ni validación de producción.
