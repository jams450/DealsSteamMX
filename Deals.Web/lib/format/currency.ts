export function formatCurrency(value: number, locale = "es-MX", currency = "MXN"): string {
  return new Intl.NumberFormat(locale, {
    style: "currency",
    currency
  }).format(value);
}
