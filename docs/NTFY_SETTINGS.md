# Nuevas variables de entorno: ntfy

Plantilla sin secretos para agregar a la configuración del servidor. No contiene un token real. Las alertas permanecen deshabilitadas por defecto.

## Variables

Copiar este bloque a la configuración privada del despliegue (no al repositorio):

```dotenv
Ntfy__Enabled=false
Ntfy__BaseUrl=https://ntfy.amitzi.xyz/
Ntfy__Topic=Deals
Ntfy__AuthToken=SET_NTFY_AUTH_TOKEN
Ntfy__PollSeconds=900
Ntfy__StartupDelaySeconds=60
Ntfy__RequestTimeoutSeconds=10
Ntfy__MaxSnapshotAgeHours=72
Ntfy__MinViableDiscountPercent=50
Ntfy__DispatchBatchSize=20
Ntfy__MaxAttempts=5
Ntfy__RetryBaseSeconds=60
Ntfy__RetryMaxSeconds=3600
Ntfy__MinSecondsBetweenSends=1
```

`AuthToken` contiene solo el token, sin el prefijo `Bearer`. El cliente añade ese prefijo. Nunca guardar el token real en este archivo, logs, commits ni mensajes.

## Uso en Docker Compose

El compose del repositorio propaga `Enabled`, `BaseUrl`, `Topic` y `AuthToken` al contenedor API. Para modificar las otras opciones también hay que declararlas en `api.environment`; definirlas únicamente en el archivo de interpolación de Compose no las introduce en el contenedor.

No reemplazar la configuración existente del despliegue con esta plantilla: solo agregar las nuevas variables. Para ejecución fuera de Compose, exportarlas en el entorno del proceso API.

## Antes de activar

1. Completar y verificar la implementación. Consultar [NTFY_NOTIFICATIONS.md](NTFY_NOTIFICATIONS.md).
2. Aplicar manualmente `SQL/migrations/2026-10-17_ntfy_deal_alerts.sql` en la base de datos correspondiente.
3. Configurar el token real en el almacenamiento privado del servidor.
4. Cambiar `Ntfy__Enabled=true` y reiniciar la API solo cuando se desee comenzar los envíos. La activación puede notificar ofertas ya persistidas que cumplan las reglas, no solo descuentos futuros.

Esta plantilla no carga configuración por sí sola, no aplica migraciones y no envía mensajes.
