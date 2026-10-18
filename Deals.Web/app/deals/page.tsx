import { Suspense } from "react";
import { redirect } from "next/navigation";
import { ProductShell } from "@/components/navigation/product-shell";
import { getServerSession } from "@/lib/auth/session";
import { DealsClient } from "./deals-client";

export default async function DealsPage() {
  const session = await getServerSession();
  if (!session) redirect("/login");

  return (
    <ProductShell
      title="Ofertas"
      subtitle="Buenas ofertas de ITAD para juegos que no pudimos identificar en tu wishlist por su identidad ITAD. El precio original siempre se muestra con su moneda; una conversión se marca como estimada."
      meta={<span className="tabler-badge tabler-badge-info">ITAD</span>}
    >
      <Suspense fallback={<p className="app-card p-5 text-sm text-muted">Cargando...</p>}>
        <DealsClient />
      </Suspense>
    </ProductShell>
  );
}
