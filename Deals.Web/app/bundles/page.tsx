import { Suspense } from "react";
import { redirect } from "next/navigation";
import { ProductShell } from "@/components/navigation/product-shell";
import { getServerSession } from "@/lib/auth/session";
import { BundlesClient } from "./bundles-client";

export default async function BundlesPage() {
  const session = await getServerSession();
  if (!session) redirect("/login");

  return (
    <ProductShell
      title="Bundles"
      subtitle="Bundles activos de las fuentes sincronizadas, con tiers y reparto estimado por juego. Las coincidencias verificadas con tu wishlist llevan un acento distinto."
      meta={<span className="tabler-badge tabler-badge-info">ITAD · gg.deals</span>}
    >
      <Suspense fallback={<p className="app-card p-5 text-sm text-muted">Cargando...</p>}>
        <BundlesClient />
      </Suspense>
    </ProductShell>
  );
}
