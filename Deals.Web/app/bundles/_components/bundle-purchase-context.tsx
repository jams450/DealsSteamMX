"use client";

// Lanzador de la compra del bundle, compartido con las tarjetas por contexto. Existe para que la tarjeta
// (`BundleCard`) siga recibiendo exactamente `item` y `now`: el reloj compartido de vencimiento tiene su
// propia prueba de regresión que fija esa línea, y la acción de compra es otra responsabilidad. Un lanzador
// ausente deja la tarjeta sin botón en vez de romper el render.
import { createContext, useContext } from "react";
import type { BundleCatalogItem } from "@/lib/contracts/bundles";

export type BundlePurchaseLauncher = (item: BundleCatalogItem) => void;

const BundlePurchaseContext = createContext<BundlePurchaseLauncher | null>(null);

export const BundlePurchaseProvider = BundlePurchaseContext.Provider;

export function useBundlePurchaseLauncher(): BundlePurchaseLauncher | null {
  return useContext(BundlePurchaseContext);
}
