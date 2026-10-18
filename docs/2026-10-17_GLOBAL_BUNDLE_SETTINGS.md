# Variables de entorno del catálogo global de bundles

Agregar estas variables a la configuración privada del despliegue, conservando las variables existentes. No hay credenciales nuevas: el cliente reutiliza la key y el governor de ITAD.

```dotenv
GlobalBundles__Enabled=true
GlobalBundles__PageSize=50
GlobalBundles__MaxPages=40
GlobalBundles__RefreshHours=12
GlobalBundles__JitterMinutes=10
GlobalBundles__StartupDelaySeconds=120
GlobalBundles__MinHoursBetweenRuns=6
```

`docker-compose.yml` propaga las siete opciones al contenedor API. Fuera de Compose deben estar en el entorno del proceso .NET. Este documento no se carga automáticamente.

- `PageSize`: 1–50 bundles por solicitud.
- `MaxPages`: 1–200 páginas por barrido. Si se alcanza con una página completa, no se publica un catálogo parcial: se conserva el snapshot anterior.
- `RefreshHours`: intervalo positivo entre barridos; valor predeterminado 12 horas.
- `JitterMinutes`: 0–1440 minutos adicionales aleatorios.
- `StartupDelaySeconds`: espera inicial no negativa.
- `MinHoursBetweenRuns`: ventana no negativa para evitar repetir el pase de arranque.
- `Enabled=false`: desactiva el job; el catálogo persistido sigue siendo consultable.

Cobertura: bundles activos que ITAD lista para MX, incluyendo el catálogo maduro solicitado al proveedor. No garantiza todos los bundles de todas las tiendas. Las monedas se presentan con su código; no se inventan precios regionales ni conversiones.

No se necesita una nueva migración para este cambio; se requieren las tablas previas de bundles y `job_runs`. La ingesta global usa `source=itad_global` y no cambia los snapshots por juego de `source=itad`. Una página fallida no debe purgar ni avanzar el snapshot.

Antes de desplegar, comprobar los checks y las limitaciones documentadas en [2026-10-17_GLOBAL_BUNDLES.md](2026-10-17_GLOBAL_BUNDLES.md). La configuración no sustituye una validación real del endpoint y de PostgreSQL, todavía no acreditada en este entorno.
