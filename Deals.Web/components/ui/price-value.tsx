import { formatCurrency } from "@/lib/format/currency";

// Importes en la unidad mínima de su moneda. Mismo criterio que el detalle del juego
// (`app/games/[steamAppId]/game-client.tsx`): se formatea en la moneda original del dato, sin convertir.
// Una moneda que no es MXN se ve distinta porque Intl imprime su código (p.ej. "XYZ 1,234.00") en vez de
// "MX$", y el llamador decide si esa moneda tiene sentido para mostrar. Sin importe o sin moneda no hay
// nada que formatear: el par nunca se pinta a medias y no se asume ninguna divisa.
export function formatMinor(amountMinor: number | null, currency: string | null) {
  if (amountMinor === null || currency === null) return null;
  return formatCurrency(amountMinor / 100, "es-MX", currency);
}

interface PriceValueProps {
  readonly amountMinor: number | null;
  readonly currency: string | null;
}

// Un precio sin dato no se rellena con 0 ni se marca como gratis: lee "—" en tono muted.
export function PriceValue({ amountMinor, currency }: PriceValueProps) {
  const display = formatMinor(amountMinor, currency);
  return display === null ? (
    <span className="text-muted">—</span>
  ) : (
    <span className="deal-price text-primary">{display}</span>
  );
}

interface PriceFactProps extends PriceValueProps {
  readonly label: string;
}

// Versión para las tiles móviles: etiqueta arriba, importe abajo, dos por fila a 360px.
export function PriceFact({ label, amountMinor, currency }: PriceFactProps) {
  return (
    <div className="min-w-0">
      <p className="text-xs text-muted">{label}</p>
      <PriceValue amountMinor={amountMinor} currency={currency} />
    </div>
  );
}
