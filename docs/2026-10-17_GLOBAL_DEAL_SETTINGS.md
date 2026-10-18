# Variables del feed global de ofertas ITAD

Última actualización: 2026-10-17. Integración nueva deshabilitada por defecto. Reutiliza configuración privada `ITAD` y el governor existente; no añadir una API key distinta.

```dotenv
GlobalDeals__Enabled=false
GlobalDeals__PageSize=200
GlobalDeals__MaxPages=20
GlobalDeals__RefreshHours=24
GlobalDeals__JitterMinutes=30
GlobalDeals__StartupDelaySeconds=300
GlobalDeals__MinHoursBetweenRuns=12
GlobalDeals__MinDiscountPercent=50
```

Las ocho variables se propagan en `docker-compose.yml`; este documento no carga configuración. Fuera de Compose deben exportarse al proceso API.

- `PageSize`: 1–200; `MaxPages`: 1–200. Si se agota presupuesto antes del fin, conservar el snapshot anterior.
- `RefreshHours`: positivo; `JitterMinutes`: 0–1440.
- Espera inicial y ventana de recuperación: no negativas.
- `MinDiscountPercent`: 0–100, filtro local de descuento. No es calificación del juego ni puntuación de oferta. Un mínimo histórico solo se afirma cuando lo entrega el proveedor en moneda comparable.

Antes de activar aplicar manualmente `SQL/migrations/2026-10-17_global_deals.sql` a la base correspondiente, verificar el contrato y las consultas en runtime y completar checks. No se ha aplicado esta migración ni llamado al proveedor en este entorno.

El feed `/deals` consulta datos persistidos; no dispara solicitudes al proveedor al renderizar. La conversión USD→MXN usa tasa persistida y se etiqueta como estimación; otras monedas sin conversión válida no se inventan como MXN. La exclusión de wishlist solo es demostrable con identidad exacta conocida.

Documentación: [GLOBAL_DEALS](2026-10-17_GLOBAL_DEALS.md). El parser fue corregido contra el esquema oficial y sus pruebas deterministas y verificación independiente aprobaron. Esto no certifica una respuesta autenticada real: validar el proveedor y PostgreSQL en el despliegue antes de habilitar el job.
